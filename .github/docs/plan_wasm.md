# Seiton Playground WASM メモリクラッシュ — 調査結果と対処計画

> Playground (Blazor WASM) でキーボード入力中に WASM ランタイムが OOM クラッシュする問題の根本原因分析と段階的改善計画。

---

## 1. 現象

エディタに YAML を数行タイプすると、WASM ランタイムが GC ヒープを確保できずクラッシュする。

```
Error: Garbage collector could not allocate 16384u bytes of memory for major heap section.
```

再現手順:
1. default サンプルワークフローを起動
2. エディタ末尾に以下をキーボード入力:
```yaml

  foo:
    needs: [test]
    runs-on: ubuntu-24.04
    steps:
      - nam  ← この辺りで落ちることが多い
```

非確定的だが、5〜10 キーストローク以内で発生する。

---

## 2. 根本原因

### 2.1 直接原因: D-5d lint-level job skipping が未使用

`PlaygroundLintRunner.RunToJsonUtf8` は `IncrementalParseContext.ParseIncrementally()` で D-5b/5c (parse-level job skipping) を使用しているが、**lint 側の D-5d (lint-level job skipping) を使っていない**。

```csharp
// PlaygroundLintRunner.cs L105 — skipJobs パラメータなし
lintResult = Engine.CheckWithParseResult(utf8Yaml, filePath, LintWithFixMetadata, parseResult);
```

`IncrementalParseContext.LintIncrementally()` (L945-1100) は D-5d を既に実装済みだが、`RunToJsonUtf8` からは呼ばれていない。

### 2.2 ベンチマーク根拠

| シナリオ | アロケーション (10回) | 1回あたり | ソース |
|---|---|---|---|
| PlaygroundLintBenchmark PartialChange Large | **109 MB** | **~10.9 MB** | 現状の RunToJsonUtf8 パス |
| PlaygroundLintBenchmark FullChange Large | 7.3 MB | ~730 KB | 同上 (全ジョブ変更) |
| IncrementalParseBenchmark FullPipeline Large (D-5d有り) | 2.6 MB / 20回 | **~131 KB** | D-5d 有効パス |
| LintBenchmark Large (parse+lint, FixEnabled=true) | — | ~670 KB | LintEngine.Check 単体 |

PartialChange (=キーストローク相当) が FullChange より 15× 多いのは、incremental parse path で **parse は skip するが lint は全ジョブ実行** するため。D-5d を有効にすれば ~131 KB/call まで削減可能。

### 2.3 WASM ヒープ設定

```xml
<!-- Seiton.Playground.csproj -->
<EmccInitialHeapSize>67108864</EmccInitialHeapSize>   <!-- 64 MB -->
<EmccMaximumHeapSize>1073741824</EmccMaximumHeapSize>  <!-- 1 GB -->
```

ヒープ上限は十分だが、Mono WASM の GC は native GC ほど即座に回収できない。~10.9 MB/call × debounce 300ms ≈ 3回/秒のペースでは GC が追いつかずクラッシュする。

---

## 3. 原因の階層 (影響度順)

### 3.1 D-5d lint job skipping 未使用 (影響: ~10×)

- **場所**: `PlaygroundLintRunner.RunToJsonUtf8` → `Engine.CheckWithParseResult` (skipJobs なし)
- **影響**: 変更されていない 5/6 ジョブの lint を毎回実行。各ジョブで expression パース、DynamicContextTypeBuilder、rule traversal が発生。
- **参照**: `IncrementalParseContext.LintIncrementally()` は `_lastReusedJobs` + `_cachedJobDiagnostics` で D-5d を実装済み。

### 3.2 Expression cache の全面無効化 (影響: ~5×)

- **場所**: `LintConfig.PrepareForRun` (L160-162) — source bytes が 1 バイトでも異なると `_expressionCache.Clear()`
- **影響**: Large ワークフロー (6 jobs × 8 steps) の 150+ expressions が毎回再パース。各 expression で `ExpressionNode[]`, `int[]`, `Diagnostic[]` が `ToArray()` でヒープ確保。
- **備考**: cache key は既に XXH64 content hash だが、`PrepareForRun` が source 全体比較で `.Clear()` を呼ぶため、キー入力ごとに全キャッシュが失われる。

### 3.3 ExpressionParser.Parse の配列アロケーション (影響: ~3×)

- **場所**: `ExpressionParser.cs` L591-595 — `NodesToArray()`, `ArgumentsToArray()`, `DiagnosticsToArray()`
- **影響**: expression 1個あたり ~200-500 bytes × 150 expressions = ~30-75 KB/call
- **備考**: lint path 用の `ParseAndValidateInline` (PooledBuffer 使用、配列確保なし) は存在するが、`LintConfig.ParseExpression()` は `ExpressionParser.Parse()` (配列確保パス) を呼んでいる。

### 3.4 DynamicContextTypeBuilder の per-job Dictionary 確保 (影響: ~2×)

- **場所**: `DynamicContextTypeBuilder.cs` — 20+ 箇所の `new Dictionary<Utf8String, ExprType>()`
- **影響**: 各ジョブで matrix, needs, jobs, steps, secrets の各コンテキスト用に 5-8 個の Dictionary を生成。6 jobs × 5 dict = 30 個/call、~20-40 KB。

### 3.5 Retained Arena の蓄積 (影響: ~1×、累積型)

- **場所**: `IncrementalParseContext` — `_retainedArenas` (MaxRetainedArenas=4)
- **影響**: old arena が reused job を参照するため dispose できず蓄積。各 Arena が Job[], Step[], SliceMap buffers を保持。MaxRetainedArenas に達するとフルパースで解放されるが、それまで ~50-100 KB/arena が滞留。

### 3.6 JSON output の毎回コピー (影響: ~0.5×)

- **場所**: `PlaygroundLintRunner.RunToJsonUtf8` L112 — `JsonBuffer.WrittenSpan.ToArray()`
- **影響**: ~5-10 KB/call の新規 byte[] 確保。

---

## 4. 対処計画

### Phase 1: D-5d lint job skipping の有効化 (Critical)

**目的**: 変更されていないジョブの lint をスキップし、per-call アロケーションを ~10.9 MB → ~1.5 MB に削減。

**方針**: `PlaygroundLintRunner.RunToJsonUtf8` で `IncrementalParseContext` が提供する `_lastReusedJobs` 情報を活用し、`Engine.CheckWithParseResult` に `skipJobs` パラメータを渡す。2つのアプローチがある:

- **A. `IncrementalParseContext.LintIncrementally()` を利用**: 既存の D-5d 実装を呼ぶ。ただし戻り値が `JsonElement[]` であり、`RunToJsonUtf8` は `byte[]` (UTF-8 JSON) を返す必要がある。
- **B. `RunToJsonUtf8` 内に D-5d ロジックを組み込む**: `IncrementalParseContext` から `_lastReusedJobs` 相当の情報を公開し、`RunToJsonUtf8` が `skipJobs` + cached diagnostics のマージを行う。

**変更対象**:
- `src/Seiton.Playground.Core/PlaygroundLintRunner.cs`
- `src/Seiton.Playground.Core/IncrementalParseContext.cs` (公開 API 追加の場合)

**検証**:
1. `dotnet test --project tests/Seiton.Playground.Tests` — 既存テスト通過
2. `dotnet test --project tests/Seiton.Core.Tests` — Core テスト通過
3. PlaygroundLintBenchmark で PartialChange Large のアロケーションを確認:
   ```
   cd src/Seiton.Benchmark
   dotnet run -c Release -- --filter PlaygroundLintBenchmark
   ```
   **成功基準**: PartialChange Large が 109 MB → 30 MB 以下 (10回計、3 MB/call 以下)

---

### Phase 2: Expression cache invalidation の改善 (High)

**目的**: source 全体が変わっても、expression の content hash が同一ならキャッシュヒットさせる。

**方針**: `LintConfig.PrepareForRun` で `_expressionCache.Clear()` を呼ぶ条件を撤廃する。expression cache のキーは既に XXH64 content hash なので、source が変わっても同一 expression はヒットする。ただし、**offset が変わると同一 content でも別のキャッシュエントリになる可能性**があるため、`ExpressionCacheEntry` の offset 依存性を確認し、content hash のみでルックアップするよう調整する。

**変更対象**:
- `src/Seiton.Core/Linting/LintConfig.cs` — `PrepareForRun` の cache clear 条件変更

**検証**:
1. `dotnet test` — 全テスト通過
2. LintBenchmark で Large FixEnabled=true のアロケーションが悪化しないこと:
   ```
   cd src/Seiton.Benchmark
   dotnet run -c Release -- --filter LintBenchmark
   ```
3. PlaygroundLintBenchmark で PartialChange Large のアロケーション改善を確認
   **成功基準**: Phase 1 の結果からさらに ~30-50% 削減

---

### Phase 3: ExpressionParser.Parse の配列確保削減 (Medium)

**目的**: lint path での expression パースで `ToArray()` を避ける。

**方針**: `LintConfig.ParseExpression()` が `ExpressionParser.ParseAndValidateInline` (PooledBuffer ベース) を使うように変更する。もしくは `ExpressionParser.Parse` 内で `PooledBuffer` → `DetachArray` パターンに変更し、呼び出し元が arena に登録して lifecycle 管理する。

**変更対象**:
- `src/Seiton.Core/Parsing/ExpressionParser.cs`
- `src/Seiton.Core/Linting/LintConfig.cs`

**検証**:
1. `dotnet test` — 全テスト通過
2. LintBenchmark Large のアロケーション:
   ```
   cd src/Seiton.Benchmark
   dotnet run -c Release -- --filter LintBenchmark
   ```
   **成功基準**: Large FixEnabled=false で ~640 KB → ~600 KB 以下

---

### Phase 4: DynamicContextTypeBuilder Dictionary pool 化 (Medium)

**目的**: per-job の Dictionary 確保を再利用パターンに変更。

**方針**: `BuildMatrixOverride`, `BuildNeedsOverride`, `BuildJobsOverride` 等で使う `Dictionary<Utf8String, ExprType>` を、rule またはビジター側でフィールドとして保持し `.Clear()` + 再利用する。`BuildStepsOverrideInto` が既に reusable dictionary を受け取るパターンを採用しているので、それを他のメソッドにも展開する。

**変更対象**:
- `src/Seiton.Core/Parsing/DynamicContextTypeBuilder.cs`
- 関連する rule/visitor コード

**検証**:
1. `dotnet test` — 全テスト通過
2. LintBenchmark Large のアロケーション:
   **成功基準**: Large FixEnabled=false で ~20-40 KB 削減

---

### Phase 5: JSON output バッファ再利用 (Low)

**目的**: `WrittenSpan.ToArray()` のコピーを削減。

**方針**: 前回と同一長の場合に前回バッファを上書き再利用する。もしくは `_lastJsonOutput` のキャッシュロジックを改善して、identity check だけでなく content-hash check も行い、不要なコピーを回避する。

**変更対象**:
- `src/Seiton.Playground.Core/PlaygroundLintRunner.cs`

**検証**:
1. `dotnet test --project tests/Seiton.Playground.Tests` — テスト通過
2. PlaygroundLintBenchmark で NoChange のアロケーションが悪化しないこと

---

## 5. 期待効果サマリ

| Phase | 対象 | 現状 (per-call) | 改善後 (推定) | 削減率 |
|---|---|---|---|---|
| Phase 1 | D-5d 有効化 | ~10.9 MB | ~1.5 MB | ~86% |
| Phase 2 | Expression cache | ~1.5 MB | ~700 KB | ~53% |
| Phase 3 | ExprParser 配列 | ~700 KB | ~600 KB | ~14% |
| Phase 4 | DynCtxBuilder Dict | ~600 KB | ~550 KB | ~8% |
| Phase 5 | JSON copy | ~550 KB | ~540 KB | ~2% |

**Phase 1 単体で WASM クラッシュは解消される可能性が非常に高い**。IncrementalParseBenchmark の実測値 (131 KB/call with D-5d) がこれを裏付ける。

---

## 6. 実装上の注意

- **テストファースト**: 各 Phase で既存テストを実行してからコード変更する。変更後は再度全テスト通過を確認。
- **ベンチマーク比較**: 各 Phase で BenchmarkDotNet の `Allocated` 列を変更前・変更後で比較記録する。
- **WASM 手動確認**: Phase 1 完了後に `dotnet run --project src/Seiton.Playground` でローカル起動し、再現手順を実行してクラッシュしないことを確認する。
- **IncrementalParseContext の複雑さ**: `LintIncrementally()` は `ParseIncrementally()` と密結合。`RunToJsonUtf8` から呼ぶ場合、JSON シリアライズ形式の差異 (`JsonElement[]` vs `byte[]`) に注意。
- **Expression cache の安全性**: `.Clear()` を撤廃する場合、異なる source での hash collision リスクを評価する。XXH64 64-bit は実用上十分だが、明示的にドキュメントする。
- **Performance Requirements SKILL 準拠**: 新しい `new List<T>`, `new Dictionary<T,T>`, `new T[]` を lint/parse ホットパスに追加しない。

---

## 7. Lessons Learned (実装後に更新)

_(各 Phase 完了後に実際の結果・予想との差分・追加で発見した問題を記録する)_
