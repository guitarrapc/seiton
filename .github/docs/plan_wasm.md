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

### Phase 1 結果 (2026-05-12)

**アプローチ**: Option B を採用 — `IncrementalParseContext` に `BuildSkipJobs()` / `MergeDiagnosticsWithCache()` public メソッドを追加し、`RunToJsonUtf8` 内で D-5d ロジックを使用。既存の `LintIncrementally()` もこれらの共通メソッドを呼ぶようリファクタリング。

**変更ファイル**:
- `src/Seiton.Playground.Core/IncrementalParseContext.cs` — `BuildSkipJobs()`, `MergeDiagnosticsWithCache()` 追加、`LintIncrementally()` リファクタリング
- `src/Seiton.Playground.Core/PlaygroundLintRunner.cs` — `RunToJsonUtf8` で D-5d 有効化

**テスト結果**: 全テスト通過 (Playground: 68/68、Core: 1290/1290)

**ベンチマーク結果** (PlaygroundLintBenchmark, ShortRun):

| シナリオ | Before Alloc (10回) | After Alloc (10回) | 削減率 | Per-call |
|---|---|---|---|---|
| **PartialChange Large** | **109,104 KB** | **627 KB** | **-99.4%** | **~63 KB/call** |
| PartialChange Small | 116.95 KB | 154 KB | +32%* | ~15 KB/call |
| FullChange Large | 7,305 KB | 409 KB | -94.4% | ~41 KB/call |
| FullChange Small | 25,784 KB | 67 KB | -99.7% | ~7 KB/call |
| NoChange Large | 233.83 KB | 0 B | -100% | 0 B/call |
| NoChange Small | 13.75 KB | 0 B | -100% | 0 B/call |

\* Small PartialChange の +32% は、D-5d キャッシュ構築コスト（`CacheJobDiagnostics` の per-job 配列確保）が 1 job の場合に相対的に大きいため。絶対値は 154 KB と十分小さく、WASM では問題にならない。

**予想との差分**:
- 予想: PartialChange Large ~1.5 MB/call → 実測: **~63 KB/call** — 予想を大幅に上回る改善。D-5d による job skip に加え、expression cache が reused source で有効に機能したことが寄与。
- NoChange パスが 0 B allocation になったのは、identity check shortcircuit で `EncodeToDoubleBuffer` すら呼ばないため。以前は `ReferenceEquals` チェック後も一部の処理が走っていた可能性。
- FullChange も大幅改善 (-94.4%) は、D-5d の job cache が 2回目以降のベンチマーク iterations で効いたため。

### Phase 2 結果 (2026-05-12)

**アプローチ**: `LintConfig.PrepareForRun()` の `_expressionCache.Clear()` を削除し、expression cache を source 変更を跨いで保持するように変更。cache key は既に XXH64(expression bytes) で content-based なので、異なる source でも同一 expression はヒットする。

**変更内容**:
1. `_expressionCache.Clear()` を削除 — source 変更時にもキャッシュを保持
2. collision guard を簡素化 — offset ベースの `Utf8Yaml.AsSpan(entry.Offset, entry.Length).SequenceEqual(expression)` を `entry.Length == expression.Length` に変更。XXH64 + length 一致で false positive は < 1/2^64
3. `ExpressionCacheEntry` から `Offset` フィールドを除去 — source 参照が不要に
4. `Utf8Yaml is null` チェックを除去 — キャッシュが source 非依存に

**変更ファイル**:
- `src/Seiton.Core/Linting/LintConfig.cs` — `PrepareForRun()` の cache clear 撤廃、`ParseExpression()` の collision guard 簡素化、`ExpressionCacheEntry` から `Offset` 除去

**安全性の根拠**:
- `ExpressionParser.Parse(ReadOnlySpan<byte>)` は expression bytes のみを入力に取り、source YAML への参照を持たない
- `ExpressionParseResult` の中身は `RootNode`(int)、`ExpressionNode[]`、`int[]`、`Diagnostic[]` — すべて expression 内部の相対位置。source YAML のオフセットは含まれない
- `ExpressionNode.Token` は `Utf8Slice`（offset + length）だが、expression span 内の相対位置
- 同一 expression は source のどこに出現しても、source が何回変わっても、パース結果は同一

**テスト結果**: 全テスト通過 (1529/1529)

**ベンチマーク結果** (PlaygroundLintBenchmark, ShortRun):

| シナリオ | Size | Before Alloc (10回) | After Alloc (10回) | 削減率 | Per-call |
|---|---|---|---|---|---|
| PartialChange | Small | 154,000 B | 144,560 B | -6.1% | ~14 KB/call |
| FullChange | Small | 67,427 B | 65,470 B | -2.9% | ~7 KB/call |
| PartialChange | Large | 626,809 B | 617,956 B | -1.4% | ~62 KB/call |
| FullChange | Large | 409,287 B | 407,498 B | -0.4% | ~41 KB/call |

CoreLintBenchmark: 回帰なし（全サイズで Allocated 同値）

**予想との差分**:
- 予想: ~30-50% 削減 → 実測: **~1-6%** — Phase 1 で expression cache が reused source 上で既に効いていたため、残存する expression 再パースの量が予想より小さかった。PartialChange では skip された job の expression は lint 自体が走らず、FullChange では全 expression が新規だが 2 回目以降のイテレーションでキャッシュヒットする。
- 改善幅は控えめだが、キャッシュが source 変更を跨いで生存するようになったことで、同一 expression の再パースを完全に防止する効果がある。

### Phase 3 結果 (2026-05-12)

**アプローチ**: `ExpressionParseResult` のフィールドを `T[]` から `ReadOnlyMemory<T>` に変更し、`ExpressionParser.Parse()` で `PooledBuffer.DetachArray()` を使用して `ToArray()` コピーを回避。downstream の全メソッドシグネチャを `ExpressionNode[]`/`int[]` → `ReadOnlySpan<ExpressionNode>`/`ReadOnlySpan<int>` に変更。

**変更内容**:
1. `ExpressionSyntax.cs` — `ExpressionParseResult` のフィールドを `ReadOnlyMemory<T>` に変更
2. `ExpressionParser.cs` — `Parse()` で `DetachArray()` を使用。空バッファは pool に返却し、非空バッファのみ detach
3. 18 ファイルで約 90 箇所のメソッドシグネチャ・call site 変更（`.Span` 追加、パラメータ型変更）
4. `WorkflowSecretsRule.cs` / `JobSecretsRule.cs` — delegate 型は `ReadOnlySpan<T>` を取れないため `ReadOnlyMemory<T>` に変更

**変更ファイル**:
- `src/Seiton.Core/Parsing/ExpressionSyntax.cs`, `ExpressionParser.cs`, `ExpressionSemanticAnalyzer.cs`, `ExpressionExtractor.cs`, `ExpressionVisitor.cs`, `DynamicContextTypeBuilder.cs`
- `src/Seiton.Core/Linting/Rules/` — FakeTernaryRule, IfCondRule, JobSecretsRule, WorkflowSecretsRule, SecretsOutsideEnvRule, UnredactedSecretsRule, SecretsWholeContextAccessRule, ExprUndefinedVarRule, RunContextDirectUseAnalyzer, RunEnvContextDirectUseRule, RunSecretsContextDirectUseRule, RunInputsContextDirectUseRule, TemplateInjectionRule
- `tests/Seiton.Core.Tests/ExpressionTests.cs`

**テスト結果**: 全テスト通過 (1529/1529)

**ベンチマーク結果**:

CoreLintBenchmark: 回帰なし（全サイズで Allocated 同値）

PlaygroundLintBenchmark: 回帰なし（実質ゼロ変化）

| シナリオ | Size | Phase 2 | Phase 3 | 変化 |
|---|---|---|---|---|
| PartialChange | Small | 144,560 B | 144,560 B | 0% |
| FullChange | Small | 65,470 B | 65,470 B | 0% |
| PartialChange | Large | 617,956 B | 617,369 B | -0.1% |
| FullChange | Large | 407,498 B | 407,498 B | 0% |

**予想との差分**:
- 予想: ~14% 削減 (Large FixEnabled=false: 717 KB → ~600 KB) → 実測: **0%**
- Phase 2 でキャッシュが source 変更を跨いで生存するため、BenchmarkDotNet の warmup 中に全 expression がキャッシュされ、measured iterations では `Parse()` 自体が呼ばれない。`DetachArray()` は初回パース時のみ効果があるが、warmup で吸収される。
- 実際のアロケーション改善はコールドスタート（初回 lint 実行）でのみ発生。150 unique expressions × ~120 bytes/expression ≈ ~18 KB の `ToArray()` コピーが回避される。
- 主な価値はアロケーション削減ではなく、**アーキテクチャ改善**: `ExpressionParseResult` が ArrayPool backed `ReadOnlyMemory<T>` を使用し、GC-tracked ヒープ確保を回避する設計に移行。

### Phase 4 結果 (2026-07-11)

**アプローチ**: `BuildStepsOverrideInto` パターンを `BuildMatrixOverride`・`BuildNeedsOverride` に展開。caller 側で reusable dictionary を保持し、per-job 呼び出しで `.Clear()` + 再利用する。per-need/per-job entry の nested dictionary（2-entry `{result, outputs}`）は各 need/job ごとに異なる `outputsType` を持ち同時に参照されるため、プールの対象外とした。

**変更内容**:
1. `DynamicContextTypeBuilder.cs` — `BuildMatrixOverrideInto(Dictionary<Utf8String, ExprType> reusableProps, ...)` 追加。既存 `BuildMatrixOverride` と同一ロジックだが、引数の dictionary を `.Clear()` して再利用
2. `DynamicContextTypeBuilder.cs` — `BuildNeedsOverrideInto(Dictionary<Utf8String, ExprType> reusableProps, ...)` 追加。main props dict のみ再利用、per-need entry dict は新規確保のまま
3. `ExprUndefinedVarRule.cs` — `_matrixOverrideProps`, `_needsOverrideProps` フィールド追加。`VisitJobPre` で `BuildMatrixOverrideInto` / `BuildNeedsOverrideInto` を使用

**変更ファイル**:
- `src/Seiton.Core/Parsing/DynamicContextTypeBuilder.cs` — `BuildMatrixOverrideInto`, `BuildNeedsOverrideInto` 追加
- `src/Seiton.Core/Linting/Rules/ExprUndefinedVarRule.cs` — reusable dict fields 追加、`VisitJobPre` の call site 変更

**テスト結果**: 全テスト通過 (1529/1529)

**ベンチマーク結果**:

CoreLintBenchmark:

| シナリオ | Phase 3 | Phase 4 | 変化 |
|---|---|---|---|
| Large FixEnabled=false | 717.68 KB | 710.18 KB | **-7.5 KB** |
| Large FixEnabled=true | 747.73 KB | 740.23 KB | **-7.5 KB** |

PlaygroundLintBenchmark:

| シナリオ | Size | Phase 3 | Phase 4 | 変化 |
|---|---|---|---|---|
| PartialChange | Large | 617,369 B | 612,164 B | **-5,205 B** |
| FullChange | Large | 407,498 B | 407,413 B | -85 B |
| PartialChange | Small | 144,560 B | 140,720 B | -3,840 B |
| FullChange | Small | 65,470 B | 65,372 B | -98 B |

**予想との差分**:
- 予想: ~20-40 KB 削減 → 実測: **~7.5 KB** — 予想の約 1/3。
- top-level dict (matrix 1個 + needs 1個) × 6 jobs = 12 dict が再利用対象。各 dict のベースオーバーヘッド（object header + buckets + entries 配列）が ~600-1200 bytes で、12 × ~650 bytes ≈ ~7.8 KB と実測に一致。
- 残りの 12-28 KB gap は nested per-need/per-job entry dict（2-entry の `{result, outputs}`）と `BuildJobOutputsType` の per-need output dict による。これらは同時参照のため単純プールできない。
- PartialChange Large で -5.2 KB の改善は、D-5d job skipping 下で変更されたジョブのみが `VisitJobPre` を実行するため、top-level dict 再利用の効果が 1 job 分に限定されるが、複数 iteration の平均で見ると有意な改善。
- FullChange Large が noise レベルなのは、全ジョブが変更される場合でも BenchmarkDotNet の warmup 後は dict が既に warm 状態のため追加改善が限定的。

### Phase 5 結果 (2026-05-12)

**アプローチ**: `PlaygroundLintRunner.RunToJsonUtf8` で `JsonBuffer.WrittenSpan.ToArray()` の代わりに、`_lastJsonOutput` バッファを再利用するパターンを導入。3段階の分岐: (1) content 同一 → キャッシュ返却 (0 alloc)、(2) length 同一・content 異なる → in-place コピー (0 alloc)、(3) length 変化 → 新規確保。

**変更内容**:
1. `PlaygroundLintRunner.cs` — `WrittenSpan.ToArray()` を 3段階バッファ再利用ロジックに置換

**変更ファイル**:
- `src/Seiton.Playground.Core/PlaygroundLintRunner.cs` — JSON output バッファ再利用

**テスト結果**: 全テスト通過 (1529/1529)

**ベンチマーク結果**:

CoreLintBenchmark: 回帰なし（全サイズで Allocated 同値）

PlaygroundLintBenchmark:

| シナリオ | Size | Phase 4 | Phase 5 | 変化 |
|---|---|---|---|---|
| PartialChange | Large | 612,164 B | 376,176 B | **-235,988 B (-38.5%)** |
| FullChange | Large | 407,413 B | 170,853 B | **-236,560 B (-58.1%)** |
| PartialChange | Small | 140,720 B | 126,800 B | **-13,920 B (-9.9%)** |
| FullChange | Small | 65,372 B | 51,452 B | **-13,920 B (-21.3%)** |

**予想との差分**:
- 予想: ~5-10 KB/call 削減 → 実測: **~236 KB (10回計、~23.6 KB/call)** — 予想を大幅に上回る改善。
- `_lastJsonOutput` は `byte[]` 1 個分だが、BenchmarkDotNet の 10 回のイテレーションすべてで再利用されるため、10 回 × ~23.6 KB = ~236 KB の `ToArray()` コピーが回避された。
- Large ワークフロー（6 jobs, 40+ diagnostics）の JSON 出力は ~23 KB と推定される。PartialChange と FullChange でほぼ同一の削減量（~236 KB）なのは、どちらも同じ diagnostics 数を出力するため JSON サイズが近いことによる。
- Small ワークフローでは ~1.4 KB/call × 10 回 = ~14 KB の削減。
- 初期予想（§3.6 「影響: ~0.5×」「~5-10 KB/call」）は JSON サイズを過小評価していた。実際のワークフローでは diagnostic 数 × JSON フィールド数により、出力が 20 KB 超になる。
