# パフォーマンス改善計画

## ベースライン（2026-04-20 計測）

**環境**: BenchmarkDotNet v0.15.6 / .NET 10.0.6 / AMD Ryzen 9 7950X3D / ShortRun

| Method | Size | Mean | Allocated | Alloc Ratio |
|---|---|---:|---:|---:|
| WorkflowParser.Parse (AST) | Small (1×3) | 39.0 μs | 31,328 B | 1.00 |
| WorkflowParser.Parse (AST) | Medium (6×8) | 683 μs | 251,040 B | 1.00 |
| WorkflowParser.Parse (AST) | Large (20×12) | 9,484 μs | 1,162,256 B | 1.00 |
| ExpressionExtractor | Small | 3.83 μs | 14,256 B | 0.46 |
| ExpressionExtractor | Medium | 42.0 μs | 156,320 B | 0.62 |
| ExpressionExtractor | Large | 204 μs | 758,376 B | 0.65 |
| VYaml raw event scan | Small | 10.8 μs | 0 B | 0.00 |
| VYaml raw event scan | Medium | 84.1 μs | 0 B | 0.00 |
| VYaml raw event scan | Large | 379 μs | 0 B | 0.00 |
| VYaml scan + adapter mapping | Small | 11.2 μs | 0 B | 0.00 |
| VYaml scan + adapter mapping | Large | 392 μs | 0 B | 0.00 |

### ベースラインの考察

- **ベンチマーク対象**: `WorkflowParser.Parse` はパーサーのみ（LintEngine は含まない）。ExpressionParser の呼び出しは含まれる。
- **VYaml 基盤コスト**: raw event scan はゼロアロケーション。パーサーのアロケーションは全て Seiton 側のコード由来。
- **Large ケースの非線形性**: VYaml raw scan は Large 379μs に対し WorkflowParser は 9,484μs（25倍）。Medium では 84μs vs 683μs（8倍）。Large でオーバーヘッド比が拡大している。
- **1ステップあたりのコスト**: Small ~10.3 KB/step、Medium ~5.2 KB/step、Large ~4.8 KB/step。固定費の按分でスケール効率は悪くないが、絶対量が大きい。

### アロケーション内訳（調査結果）

| 発生源 | 箇所数 | スコープ | 推定インパクト |
|---|---:|---|---|
| `new HashSet<Utf8String>()` (重複キー検出) | 35 | per-mapping (per-job, per-event, per-section) | **高** — 最多のアロケーション源。毎回 HashSet + 内部配列 + Utf8String(byte[] copy) を生成 |
| `TryRegisterMappingKey` — Utf8String byte[] copy | 35× N keys | per-key (success path) | **高** — 全キーで byte[] ToArray を実行（重複検出のため） |
| `DecodeUtf8` (Encoding.UTF8.GetString) | 82 | per-key (diagnostic message 引数) | **中-高** — エラーメッセージ生成用だが、success path でも eager 評価される呼び出しが多数 |
| `$"..."` 文字列補間 (diagnostic message) | 多数 | per-key | **中** — ParseString 等の errorMessage 引数として eager評価。エラーなくても string 生成 |
| `new List<T>` (Event, Step, StringNode 等) | 12 | per-sequence | **低-中** — AST 構築で必要。サイズ不明な動的リスト |
| `.ToArray()` (List→Array 変換) | 11 | per-parse, per-sequence | **低** — 最終結果構築時。必要だが二重変換を避ける余地あり |
| ExpressionParser 内部 List×3 + ToArray×3 | 各式ごと | per-expression | **中** — 式の数に比例。Large では数百回呼ばれる |

---

## 改善フェーズ

### Phase 1: 診断メッセージの遅延評価（最小リスク・中効果）

**目的**: success path での不要な string アロケーションを排除する。

#### 1-A: errorMessage 引数の遅延化

現在、ParseString / ParseExpression / ParseInt / ParseFloat 等のスカラーパーサーに渡す `string errorMessage` は、success path でもエラーが起きなくても呼び出し側で eager 評価される:

```csharp
// 現在: DecodeUtf8 + 文字列補間が常に実行される
job.Name = ParseString(ref reader, diagnostics,
    $"expected string for 'name' in job '{DecodeUtf8(source, jobId)}'");
```

**改善策**:
1. `string errorMessage` パラメータを消し、呼び出し側で `AddError` を明示的に呼ぶパターンに変更
2. または `ReadOnlySpan<byte>` ベースの errorMessage テンプレート + context 分離

**選択肢**:
- **A**: `errorMessage` を除去し、パーサーが `null` を返したら呼び出し側で `AddError` — 最もシンプルだが呼び出し側のコード行数が増加
- **B**: `DefaultInterpolatedStringHandler` ベースの遅延評価ファクトリ — C# 10+ のコンパイラ最適化を利用、API 互換性は維持しにくい
- **C**: 定数 string のみ許可し、コンテキスト情報は別パラメータで渡す — 中間的

推奨: **選択肢 A**（段階的に適用可能、最もシンプル）

**影響範囲**: WorkflowParser.Jobs.cs, WorkflowParser.On.cs, WorkflowParser.Steps.cs 等の全スカラーパース呼び出し箇所

**完了条件**:
- [ ] DecodeUtf8 が success path のスカラーパーサー引数で呼ばれていないこと（grep 確認）
- [ ] `$"..."` 文字列補間が success path のスカラーパーサー引数で使われていないこと
- [ ] ベンチマーク: Small/Medium/Large の Allocated が減少していること
- [ ] 全テスト通過（477 tests）
- [ ] 診断メッセージの内容が変わっていないこと（テストで検証）

**推定効果**: Large ケースで ~100–200 KB 削減（82 callsite × 平均数十バイトの string × 240 steps）

---

### Phase 2: HashSet<Utf8String> の削除（中リスク・高効果）

**目的**: 35箇所の per-mapping HashSet 割り当てと、per-key の byte[] copy を排除する。

#### 2-A: ビットフラグ方式による固定キーの重複検出

GitHub Actions ワークフローの各マッピングは固定キーセット（例: job には `name`, `runs-on`, `if`, `needs`, `steps`, `uses`, `with`, `env`, `outputs`, `strategy`, `timeout-minutes`, `continue-on-error`, `services`, `container`, `defaults`, `permissions`, `concurrency`, `environment` 等）を持つ。

**改善策**:
- 固定キーについては `ulong` ビットフラグで seen/unseen を管理
- unknown key の重複検出のみ HashSet に残す（unknown key は稀なのでコスト極小）
- `TryRegisterMappingKey` を `TryRegisterKnownKey(int keyIndex, ref ulong seen)` に置換

```csharp
// After: ゼロアロケーション
ulong seen = 0;
// key dispatch 内:
case "name"u8:
    if (!TrySetBit(ref seen, 0)) { AddError(..., "duplicate key 'name'"); }
    job.Name = ParseString(ref reader, diagnostics);
    break;
```

**影響範囲**: WorkflowParser の全 mapping パーサー（35箇所）。各マッピングループの構造変更が必要。

**完了条件**:
- [ ] `new HashSet<Utf8String>` が WorkflowParser*.cs から全て除去されていること
- [ ] `TryRegisterMappingKey` メソッドが削除されていること
- [ ] ベンチマーク: Large の Allocated が 20–40% 減少していること
- [ ] 重複キー検出が引き続き動作すること（既存テストで検証）
- [ ] 全テスト通過

**推定効果**: Large で ~200–400 KB 削減。HashSet 内部配列 + Utf8String の byte[] copy が最大のアロケーション源。

---

### Phase 3: ExpressionParser のアロケーション削減（低リスク・中効果）

**目的**: 式ごとに生成される List×3 + ToArray×3 を削減する。

#### 3-A: ArrayPool ベースの内部バッファ

ExpressionParser は式ごとに `List<ExpressionNode>`, `List<int>`, `List<Diagnostic>` を new し、結果構築時に `.ToArray()` する。

**改善策**:
1. 内部バッファを `ArrayPool<T>.Shared.Rent` で取得し、結果サイズが確定してから `.AsSpan(0, count).ToArray()` で最小配列を作る
2. または `ref struct ExpressionParserState` に stackalloc 済みバッファを持たせる（式は通常小さいため 64 要素程度で十分）

#### 3-B: 関数呼び出しの directArgs 最適化

`ParseFunctionCall` 内で `new List<int>(4)` を引数ごとに生成している。

**改善策**: `Span<int>` stackalloc（GitHub Actions の関数は最大引数数が小さい）に置換。

**完了条件**:
- [ ] ExpressionParser 内の `new List<T>` が除去または pooled になっていること
- [ ] ベンチマーク: ExpressionExtractor の Allocated が減少していること
- [ ] 式解析の正確性が変わっていないこと（ExpressionTests 全通過）
- [ ] 全テスト通過

**推定効果**: Large の ExpressionExtractor で ~50–100 KB 削減

---

### Phase 4: LintEngine のアロケーション削減（中リスク・中効果）

**目的**: パフォーマンスベンチマークの対象を Lint まで拡張し、LintEngine 固有のアロケーションを削減する。

#### 4-A: Lint ベンチマークの追加

現在のベンチマークは `WorkflowParser.Parse` のみ。LintEngine を含む end-to-end ベンチマークを追加する。

#### 4-B: インライン抑制パーサーの UTF-8 化

`ParseInlineSuppression` が `Encoding.UTF8.GetString(utf8Yaml)` + `text.Split('\n')` でファイル全体を string に変換している。これはファイルサイズに比例する大きなアロケーション。

**改善策**: `ReadOnlySpan<byte>` ベースの行スキャンに書き換え。`\n` を探して行頭の `# seiton-ignore:` パターンをバイト比較で検出する。

#### 4-C: ジョブ ID デコードの遅延化

`BuildKnownJobIds` / `BuildJobScopes` が各ジョブ ID を string にデコードしている（`Encoding.UTF8.GetString`）。

**改善策**: Utf8String ベースの比較に変更し、string デコードを診断出力時のみに限定。

**完了条件**:
- [ ] `LintBenchmark` クラスが追加されていること
- [ ] `ParseInlineSuppression` が `Encoding.UTF8.GetString` を使わなくなっていること
- [ ] ベンチマーク: Lint end-to-end の Allocated がベースラインから改善していること
- [ ] 全テスト通過

**推定効果**: Lint フェーズで Large YAML の場合 ~50–200 KB 削減（ファイルサイズ依存）

---

### Phase 5: AST List → Array の最適化（低リスク・小効果）

**目的**: AST 構築時の List→Array 変換のオーバーヘッドを削減する。

#### 5-A: 要素数 0/1 のファーストパス

`ParseStringOrStringSequence` 等で `List<T>` を作る前に、要素数 0 または 1 の場合は `[]` / `[single]` を直接返す。

#### 5-B: 初期容量の最適化

`new List<Step>(4)` 等で初期容量を指定し、小規模ケースでの再割り当てを防ぐ。

**完了条件**:
- [ ] 主要 List 生成が初期容量付きであること
- [ ] ベンチマーク: Small の Allocated がわずかに改善していること
- [ ] 全テスト通過

**推定効果**: 小（数 KB）。アーキテクチャ的整理の意味合いが強い。

---

## 効果の見積もりサマリー

| フェーズ | 推定 Allocated 削減 (Large) | リスク | 工数 |
|---|---|---|---|
| Phase 1: 診断メッセージ遅延評価 | ~100–200 KB | 低 | 中 |
| Phase 2: HashSet 削除 + ビットフラグ | ~200–400 KB | 中 | 大 |
| Phase 3: ExpressionParser 最適化 | ~50–100 KB | 低 | 小 |
| Phase 4: LintEngine 最適化 | ~50–200 KB | 中 | 中 |
| Phase 5: List→Array 最適化 | ~数 KB | 低 | 小 |
| **合計** | **~400–900 KB** | | |

Large ベースライン 1,162 KB に対し、全フェーズで 35–77% のアロケーション削減を目指す。

---

## 実行順序と依存関係

```
Phase 1 (診断遅延) ──→ Phase 2 (HashSet削除) に部分依存
                         （Phase 1 で errorMessage を整理した後の方が Phase 2 のリファクタが容易）
Phase 3 (ExpressionParser) は独立
Phase 4 (LintEngine) は独立（ただし 4-A のベンチマーク追加を先に行う）
Phase 5 (List最適化) は独立
```

推奨順: **Phase 1 → Phase 2 → Phase 3 → Phase 4 → Phase 5**

各フェーズ完了後の状態確認:
1. `dotnet build` 成功
2. `dotnet test` 全通過（477 tests）
3. ベンチマーク実行（`cd src/Seiton.Benchmark && dotnet run -c Release`）
4. ベースラインとの Allocated / Mean 比較
5. 新規 `GetScalarString()` が success path に追加されていないことの grep 確認

---

## 実装ステータス

### Phase 1: 診断メッセージの遅延評価 — 🔲 未着手

### Phase 2: HashSet<Utf8String> の削除 — 🔲 未着手

### Phase 3: ExpressionParser のアロケーション削減 — 🔲 未着手

### Phase 4: LintEngine のアロケーション削減 — 🔲 未着手

### Phase 5: AST List → Array の最適化 — 🔲 未着手
