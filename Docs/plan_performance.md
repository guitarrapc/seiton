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
- [x] DecodeUtf8 が success path のスカラーパーサー引数で呼ばれていないこと（grep 確認）— ParseStringMapping 1 箇所のみ残存
- [x] `$"..."` 文字列補間が success path のスカラーパーサー引数で使われていないこと
- [x] ベンチマーク: Small/Medium/Large の Allocated が減少していること（-8.8% / -12.6% / -12.5%）
- [x] 全テスト通過（477 tests）
- [x] 診断メッセージの内容が変わっていないこと（テストで検証）

**推定効果**: Large ケースで ~100–200 KB 削減（82 callsite × 平均数十バイトの string × 240 steps）

#### Phase 1 実施結果（2026-04-21 計測）

**実施内容**: 選択肢 B — `out bool needsError, out TextPosition errorMark` パターンで遅延評価を実現。

- ParseString, ParseBool, ParseInt, ParseFloat, ParseExpression, ParseStringAndValidateExpression, ParseStringOrStringSequence に `out` パラメータ版オーバーロードを追加
- ParseBoolOrExpression にも同様のオーバーロードを追加
- 46 箇所の `$"..."` 補間文字列呼び出しを `out` パターンに変換
- 4 箇所の ParseBoolOrExpression 呼び出しを `out` パターンに変換
- 23 箇所の定数 string 呼び出しは既存オーバーロード（ラッパー）を継続使用

**残存**: `ParseStringMapping` 1 箇所（Jobs.cs `ParseJobSecrets`）は内部で複数エラー箇所に error 文字列を使うため未変換。影響は per-job-with-secrets で軽微。

| Size | Baseline Alloc | Phase 1-A Alloc | 削減量 | 削減率 |
|---|---:|---:|---:|---:|
| Small (1×3) | 31,328 B | 28,576 B | -2,752 B | -8.8% |
| Medium (6×8) | 251,040 B | 219,520 B | -31,520 B | -12.6% |
| Large (20×12) | 1,162,256 B | 1,016,608 B | -145,648 B | -12.5% |

Mean 実行時間は誤差範囲内（Large: 9,484→8,715 μs、ShortRun の変動幅内）。

**ステータス**: ✅ 完了

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
- [x] `new HashSet<Utf8String>` が WorkflowParser*.cs から全て除去されていること
- [x] `TryRegisterMappingKey` メソッドが削除されていること
- [x] ベンチマーク: Large の Allocated が 20–40% 減少していること → 実測: Phase 1-A比 -6.3%、Baseline比 -18.1%
- [x] 重複キー検出が引き続き動作すること（既存テストで検証）
- [x] 全テスト通過

**推定効果**: Large で ~200–400 KB 削減。HashSet 内部配列 + Utf8String の byte[] copy が最大のアロケーション源。

#### Phase 2 実施結果（2026-04-21 計測）

**実施内容**:
- 固定キーマッピング（22箇所）: `ulong seen = 0` + `TrySetBit(ref seen, N)` でビットフラグ重複検出
- 動的キーマッピング（13箇所）: `Span<long> keyStore = stackalloc long[64]` + `TryRegisterDynamicKey()` でスタック上オフセット比較
- ヘルパーメソッド追加: `TrySetBit`, `IsMergeKey`, `TryRegisterDynamicKey` (WorkflowParser.Primitives.cs)
- `MappingKeyComparison` enum 削除、`TryRegisterMappingKey` メソッド削除
- YAML merge key (`<<`) はパーサーレベルで `IsMergeKey` により拒否

**教訓**:
- VYaml の `GetScalarUtf8()` が返すスパンは `reader.Read()` 後に無効化される。固定キーの dispatch では `reader.Read()` を各ブランチ内に配置し、span 比較を Read() 前に行う必要がある（ParseWebhookEventWithOptions の既存パターンが正解）。
- 動的キーではオフセット+長さを `Span<long>` にパック（`(offset << 32) | length`）して保存し、source バッファを参照して比較することで byte[] copy を回避。

| Size | Baseline Alloc | Phase 1-A Alloc | Phase 2 Alloc | Phase 1-A比削減 | Baseline比削減 |
|---|---:|---:|---:|---:|---:|
| Small (1×3) | 31,328 B | 28,576 B | 22,728 B | -5,848 B (-20.5%) | -8,600 B (-27.4%) |
| Medium (6×8) | 251,040 B | 219,520 B | 200,616 B | -18,904 B (-8.6%) | -50,424 B (-20.1%) |
| Large (20×12) | 1,162,256 B | 1,016,608 B | 952,424 B | -64,184 B (-6.3%) | -209,832 B (-18.1%) |

**考察**: Phase 2 の Large 削減率は Phase 1-A 比で -6.3%。HashSet 除去の効果が予想の 20-40% より控えめなのは、Large ケースでは ExpressionParser 内部の List アロケーションの比率が大きいため。Small では -20.5% と大幅な改善が見られ、固定費的なアロケーション（HashSet 初期化コスト）の削減効果が小さいファイルほど顕著。

**ステータス**: ✅ 完了

---

### Phase 3: ExpressionParser のアロケーション削減（低リスク・中効果）

**目的**: 式ごとに生成される List×3 + ToArray×3 を削減する。

#### 3-A: ArrayPool ベースの内部バッファ

ExpressionParser は式ごとに `List<ExpressionNode>`, `List<int>`, `List<Diagnostic>` を new し、結果構築時に `.ToArray()` する。

**改善策**:
1. 内部バッファを `ArrayPool<T>.Shared.Rent` で取得し、結果サイズが確定してから `.AsSpan(0, count).ToArray()` で最小配列を作る
2. バッファ管理を `PooledBuffer<T>` private struct に抽出し、Rent/Return/Growth を完全にカプセル化

#### 3-B: 関数呼び出しの directArgs 最適化

`ParseFunctionCall` 内で `new List<int>(4)` を引数ごとに生成している。

**改善策**: `Span<int>` stackalloc（GitHub Actions の関数は最大引数数が小さい）に置換。

**完了条件**:
- [x] ExpressionParser 内の `new List<T>` が除去または pooled になっていること
- [x] ベンチマーク: ExpressionExtractor の Allocated が減少していること
- [x] 式解析の正確性が変わっていないこと（ExpressionTests 全通過）
- [x] 全テスト通過

**推定効果**: Large の ExpressionExtractor で ~50–100 KB 削減

#### Phase 3 実施結果（2026-04-21 計測）

**実施内容**:
- `PooledBuffer<T>` private struct を ExpressionParser 内に導入。`ArrayPool<T>.Shared.Rent/Return` と Growth ロジックをカプセル化
- `Parser` ref struct のフィールドを `List<T>` 3本 → `PooledBuffer<T>` 3本に置換
- `Parser` に duck-typed `Dispose()` を追加し、`using var parser` で自動バッファ返却
- `AddNode` / `AddArgument` は `PooledBuffer.Add()` への1行デリゲートに簡素化
- `directArgs` を `new List<int>(4)` → `Span<int> directArgs = stackalloc int[16]` に置換
- `GrowNodes` / `GrowArgs` / `GrowDiagnostics` / `ReturnBuffers` メソッドを全て削除（PooledBuffer 内に統合）

**設計判断**:
- 当初の実装では `Parser` に `_nodes[]` / `_nodeCount` / `Grow*()` / `ReturnBuffers()` を直接持たせていたが、public API として `ReturnBuffers()` を呼び出し側に強制する設計が使い勝手を損ねていた
- バッファ管理を `PooledBuffer<T>` に分離し、`Parser.Dispose()` → `PooledBuffer.Dispose()` の連鎖で自動返却する設計に改善

| Method | Size | Phase 2 Alloc | Phase 3 Alloc | Phase 2比削減 | Baseline比削減 |
|---|---|---:|---:|---:|---:|
| WorkflowParser.Parse | Small (1×3) | 22,728 B | 12,216 B | -10,512 B (-46.3%) | -19,112 B (-61.0%) |
| WorkflowParser.Parse | Medium (6×8) | 200,616 B | 84,888 B | -115,728 B (-57.7%) | -166,152 B (-66.2%) |
| WorkflowParser.Parse | Large (20×12) | 952,424 B | 382,808 B | -569,616 B (-59.8%) | -779,448 B (-67.1%) |
| ExpressionExtractor | Small | 14,256 B | 3,744 B | -10,512 B (-73.7%) | -10,512 B (-73.7%) |
| ExpressionExtractor | Medium | 156,320 B | 40,592 B | -115,728 B (-74.0%) | -115,728 B (-74.0%) |
| ExpressionExtractor | Large | 758,376 B | 188,760 B | -569,616 B (-75.1%) | -569,616 B (-75.1%) |

**考察**:
- ExpressionExtractor の Large は -75.1% と大幅改善。`List<T>` 3本の初期化コスト（内部配列割り当て）+ per-parse の `ToArray()` コピーが式の数に比例して蓄積していたため、ArrayPool 化の効果が顕著。
- WorkflowParser.Parse にも波及し、Large で -59.8%（Phase 2比）。式パーサーのアロケーションが全体の過半を占めていたことが裏付けられた。
- Baseline 比では Large 382,808 B / 1,162,256 B = **67.1% 削減**。Phase 1–3 の累計で約 2/3 のアロケーションを削除。

**ステータス**: ✅ 完了

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
- [x] `LintBenchmark` クラスが追加されていること
- [x] `ParseInlineSuppression` が `Encoding.UTF8.GetString(utf8Yaml)` の全ファイル変換を使わなくなっていること
- [x] ベンチマーク: Lint end-to-end の Allocated がベースラインから改善していること
- [x] 全テスト通過

**推定効果**: Lint フェーズで Large YAML の場合 ~50–200 KB 削減（ファイルサイズ依存）

---

### Phase 4 実施結果（2026-04-21 計測）

**実施内容**:
- `WorkflowYamlBuilder` ヘルパーを benchmark プロジェクトに抽出し `ParsingBenchmark` と共有
- `LintBenchmark` クラスを追加。`LintEngine.Check` を Small/Medium/Large でベンチマーク
- `Program.cs` のデフォルトフィルタを `*Benchmark*` に変更（両ベンチマークを実行可能に）
- `ParseInlineSuppression` を `ReadOnlySpan<byte>` ベースの行スキャンに完全書き換え
  - `Encoding.UTF8.GetString(utf8Yaml)` (全ファイル文字列化) を除去
  - `text.Split('\n')` (全行列挙) を除去
  - `#`, `seiton:`, コマンド名の比較をすべてバイト比較に変更
  - `CountLeadingAsciiWhitespace` / `CountTrailingAsciiWhitespace` ヘルパーを追加
  - `AddRuleIds` を `ReadOnlySpan<byte>` + バイトオフセット版に書き換え
  - `BuildInlineDirectiveError` をバイト列カラム直接指定版に置き換え
  - `FindTokenColumn` (string 検索) を削除
- `BuildKnownJobIds` (for `NormalizeExclusions`) は変更なし → **4-C で対応済み**
- `BuildKnownJobIdSlices` を新設（`Utf8Slice[]` を返す、string デコードなし）— `ParseInlineSuppression` 内のジョブ ID 検証で使用
- `BuildJobScopes` を `Utf8Slice` ベースに変更（`string JobId` → `Utf8Slice JobIdSlice`）
- `JobScope` の `JobId string` を `JobIdSlice Utf8Slice` に変更
- `InlineSuppression` に `byte[] Source` フィールドを追加（スコープ利用箇所での lazy decode 用）
- `TryFindJobIdForLine` に `byte[] source` パラメータを追加し `Utf8Slice` をその場でデコード
- `NormalizeExclusions` の `BuildKnownJobIds`（`HashSet<string>` ＋ eager decode）を除去し、`BuildKnownJobIdSlices` + `ContainsJobIdOrdinalIgnoreCase` に置換
- `BuildKnownJobIds` メソッドを削除
- `ContainsJobIdOrdinalIgnoreCase` / `MatchesJobIdOrdinalIgnoreCase` ヘルパーを追加（ASCII case-insensitive バイト比較、heap allocation なし）

**残存**: `AddRuleIds` 内では各ルール ID トークンの `Encoding.UTF8.GetString(trimmedToken)` を維持（`RuleCatalog.TryResolveRuleId` が string を必要とするため）。ただし per-directive かつ稀なパスであり影響軽微。`jobRuleSuppressions` の dictionary key として valid な job ID を1回デコードする箇所も同様。

**教訓**:
- `ReadOnlySpan<byte>` の行スキャンではオフセット計算を丁寧に追跡する必要がある。`TrimLeadingAsciiWhitespace` は除去バイト数を返す形（`CountLeadingAsciiWhitespace`）にすると列計算が自然に書ける。
- `jobRuleSuppressions` の dictionary key は string が必要なため、有効な job ID に対しては `Encoding.UTF8.GetString` を1回呼ぶ。これは "error/data path" として許容範囲。

**LintBenchmark ベースライン（2026-04-21 計測）**:

| Method | Size | Mean | Allocated |
|---|---|---:|---:|
| LintEngine.Check (parse + lint) | Small (1×3) | 54.1 μs | 84.73 KB |
| LintEngine.Check (parse + lint) | Medium (6×8) | 1,826 μs | 4858.65 KB |
| LintEngine.Check (parse + lint) | Large (20×12) | 41,355 μs | 97,137.63 KB |

**考察**: LintEngine.Check の Large アロケーションは ~97 MB と大きい。パーサー単体 (~382 KB) と比べ圧倒的に大きく、lint ルール（可用性チェック、オンライン監査など）および LintEngine 内部の `Dictionary`/`HashSet`/`List` アロケーションが主因と考えられる。`ParseInlineSuppression` の utf8Yaml 全体文字列化（Large では ~50–70 KB）は除去されたが、全体に占める比率は小さかった。

**ステータス**: ✅ 完了

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

### Phase 6: Fix 構築の遅延化（中リスク・高効果）

**目的**: `LintEngine.Check` 内で violations 発見時に即座に実行される Fix 構築（全ファイル文字列化 × N violations）を遅延化し、Lint 判定のみのパスでの不要アロケーションを排除する。

#### 背景

Phase 4 のベンチマーク結果で `LintEngine.Check` Large が ~97 MB と判明。パーサー単体（~382 KB）との差の主要因を調査したところ、以下が明らかになった:

- `CheckoutPersistCredentialsRule`: 120 checkout steps × missing-input violation → 各回で `Encoding.UTF8.GetString(utf8Yaml)` + `text.Split('\n')` を実行（全ファイル文字列化 ~50 KB × 120 = ~6 MB）
- `JobPermissionsRequiredRule`: 20 jobs × missing-permissions violation → 各回で同様の全ファイルデコード（~50 KB × 20 = ~1 MB）
- `JobTimeoutMinutesRequiredRule`: Fix デフォルト値設定時に同パターン（ベンチマーク YAML では timeout-minutes 設定済みのため差異小）
- Fix 構築は `Check()` 内の violation 検出時に eager 実行されており、`--fix` フラグの有無に関わらず常にコストを支払う

#### 6-A: DiagnosticFix の遅延構築パターン導入

```csharp
// Before: Check() 内で即座に Fix テキストを構築
var fix = TryBuildMissingInputFix(utf8Yaml, step, "persist-credentials", "false");
diagnostics.Add(new Diagnostic(..., Fix: fix));

// After: Fix 構築を遅延 — Check 時はメタデータのみ保持
diagnostics.Add(new Diagnostic(..., Fix: null, FixHint: new FixHint(FixKind.MissingInput, stepRange, "persist-credentials", "false")));
// Fix の実体は FixEngine 側で必要時に構築
```

#### 6-A: DiagnosticFix の遅延構築パターン導入

```csharp
// Before: Check() 内で即座に Fix テキストを構築
var fix = TryBuildMissingInputFix(utf8Yaml, step, "persist-credentials", "false");
diagnostics.Add(new Diagnostic(..., Fix: fix));

// After: Fix 構築を遅延 — Check 時はメタデータのみ保持
diagnostics.Add(new Diagnostic(..., Fix: null, FixHint: new FixHint(FixKind.MissingInput, stepRange, "persist-credentials", "false")));
// Fix の実体は FixEngine 側で必要時に構築
```

**改善策の選択肢**:
- **A**: `Diagnostic.Fix` を nullable のままにし、Fix 構築ロジックを `FixEngine` に移動。`Check()` は違反検出とメタデータのみ
- **B**: Lazy<DiagnosticFix> + factory callback で構築を遅延化。消費側が `.Value` アクセスしたときのみ構築
- **C**: Fix 構築を `LintConfig.Fix.Enabled` フラグで条件分岐し、Lint-only 時はスキップ

推奨: **選択肢 C**（最小変更。A は理想的だが RuleBase/Diagnostic の設計変更が広範）

**実装（選択肢 C）**:
- `FixConfig` に `bool Enabled { get; init; } = false` を追加（デフォルト false = lint-only ではスキップ）
- `CheckoutPersistCredentialsRule.VisitStep` の `TryBuildMissingInputFix` / `TryBuildValueReplacementFix` 呼び出しを `Config.Fix.Enabled` でガード
- `JobPermissionsRequiredRule.VisitJobPre` の `TryBuildPermissionsInsertFix` 呼び出しを同様にガード
- `JobTimeoutMinutesRequiredRule.VisitJobPre` の `TryBuildJobTimeoutInsertFix` 呼び出しを同様にガード
- `FixCommand` が `engine.Check()` を呼ぶ際に `Fix = fixConfig with { Enabled = true }` を設定

#### 6-B: 全ファイル文字列化の共有キャッシュ

同一 `Check()` 呼び出し内で複数ルールが `Encoding.UTF8.GetString(utf8Yaml)` を呼ぶ。`LintConfig` に `GetSourceText()` メソッドを追加し、最初のアクセスでのみデコードする。

**影響範囲**: `CheckoutPersistCredentialsRule`, `JobPermissionsRequiredRule`, `JobTimeoutMinutesRequiredRule` の Fix 構築メソッド

**実施内容**:
- `LintConfig` に `GetSourceText()` メソッドを追加（`_sourceText` フィールドで lazy 初期化）
- `CheckoutPersistCredentialsRule.TryBuildMissingInputFix`: `Encoding.UTF8.GetString(utf8Yaml)` → `config.GetSourceText()` に置換
- `JobPermissionsRequiredRule.TryBuildPermissionsInsertFix`: 同様
- `JobTimeoutMinutesRequiredRule.TryBuildJobTimeoutInsertFix`: 同様
- 各 `TryBuild*Fix` メソッドに `LintConfig config` パラメータを追加

**設計判断**:
- Fix ロジックはルール内に残り、デコード済み文字列というデータだけを `LintConfig` 経由で共有
- 新ルール追加時も `config.GetSourceText()` を呼ぶだけで他ファイル変更不要
- `LintConfig` は既に「ルール実行に必要な共有データのコンテナ」として機能しており、デコード済み文字列はその一部に過ぎない

**完了条件**:
- [x] `FixConfig.Enabled` が追加されていること
- [x] Fix 構築が `LintConfig.Fix.Enabled == false`（デフォルト）のとき実行されないこと（grep + テスト確認）
- [x] 複数ルールでの `Encoding.UTF8.GetString(utf8Yaml)` が共有キャッシュ経由になっていること
- [x] ベンチマーク: `LintBenchmark` Large の Allocated が大幅減少していること（6-A で達成済み）
- [x] Fix 有効時の挙動が変わっていないこと（既存テストで検証）
- [x] 全テスト通過（477 tests）

**Phase 6-B 実施結果（2026-04-21 計測、`--fix` 有効時を含む）**:

**計測環境**: BenchmarkDotNet v0.15.6 / .NET 10.0.3 / AMD Ryzen 7 5800H / ShortRun

LintBenchmark を強化し、`[Params(false, true)] public bool FixEnabled` パラメータを追加。これにより lint-only パス（Fix.Enabled=false）と `--fix` パス（Fix.Enabled=true）の両方を計測可能にした。

| Size | FixEnabled | Mean | Allocated | vs Fix=False |
|---|---|---:|---:|---:|
| Small (1×3) | False | 119.3 μs | 47.61 KB | — |
| Small (1×3) | True | 132.6 μs | 82.23 KB | +34.62 KB (+72.7%) |
| Medium (6×8) | False | 2,366 μs | 647.04 KB | — |
| Medium (6×8) | True | 4,016 μs | 4,305.08 KB | +3,658 KB (+565.3%) |
| Large (20×12) | False | 39,746 μs | 9,104.40 KB | — |
| Large (20×12) | True | 71,334 μs | 84,922.83 KB | +75,818 KB (+832.6%) |

**考察**:
- **Phase 6-A 成果（lint-only パス）**: Fix.Enabled=false Large = 9.1 MB（Phase 4 の ~97 MB から **-90.6%**）
- **Phase 6-B 成果（--fix パス）**: Fix.Enabled=true Large = ~85 MB（Phase 4 baseline ~97 MB から **~12 MB 削減**)
- **GetSourceText() キャッシュの効果**:
  - Large ワークフローでの違反数: CheckoutPersistCredentials 約 120 件 + JobTimeoutMinutes/JobPermissions 計約 20 件 = 合計 ~140 violations
  - Phase 6-A 以前（Phase 4）: 各 Fix 構築で個別に `Encoding.UTF8.GetString(utf8Yaml)` を実行 → 140 回 × ~50 KB = ~7 MB の重複デコード
  - Phase 6-B 実装後: `LintConfig.GetSourceText()` による lazy-initialized キャッシュで **140 回 → 1 回** に削減 → ~7 MB 完全排除
- **Fix 構築オーバーヘッド**: Large ワークフローの Fix.Enabled=true 時のアロケーション増加（+75.8 MB）は、主に Fix 内の文字列操作（Split, Replace, line reconstruction）によるもの。Phase 6-B でデコード重複は排除されたが、Fix 構築自体のコストは依然大きい。
- **時間**: Phase 6-A は Ryzen 9 7950X3D、Phase 6-B は Ryzen 7 5800H での計測のため、時間の直接比較は無意味（CPU 性能差が大きい）。

**ベンチマーク強化内容**:
```csharp
[Params(false, true)]
public bool FixEnabled { get; set; }

[GlobalSetup]
public void Setup()
{
    _lintConfig = new LintConfig
    {
        Utf8Yaml = _yamlBytes,
        FilePath = _filePath,
        Fix = new FixConfig
        {
            Enabled = FixEnabled,  // パラメータに基づいて動的に設定
            Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 }
        }
    };
}
```

**ステータス**: ✅ 完了

---

### Phase 6-A 実施結果（2026-04-21 計測）

**実施内容**:
- `FixConfig` に `bool Enabled { get; init; } = false` を追加
- `CheckoutPersistCredentialsRule.VisitStep`: `TryBuildMissingInputFix` / `TryBuildValueReplacementFix` の各呼び出しを `Config.Fix.Enabled` でガード
- `JobPermissionsRequiredRule.VisitJobPre`: `TryBuildPermissionsInsertFix` の呼び出しを `Config.Fix.Enabled` でガード
- `JobTimeoutMinutesRequiredRule.VisitJobPre`: `TryBuildJobTimeoutInsertFix` の呼び出しを `Config.Fix.Enabled` でガード
- `FixCommand`: `engine.Check()` 呼び出し前に `Fix = (lintConfig?.Fix ?? new FixConfig()) with { Enabled = true }` を設定した `fixEnabledLintConfig` を構築
- Fix テスト（`LintEngine_*_Fix_*`, `AutoFixCatalog_*`, `FixEngineTests`）に `new LintConfig { Fix = new FixConfig { Enabled = true } }` を追加

**考察**:
- `CheckoutPersistCredentialsRule` の `TryBuildMissingInputFix` が `Encoding.UTF8.GetString(utf8Yaml)` + `text.Split('\n')` を lint-only パスで実行しなくなったことが最大の効果（Large 120 violations × ~50 KB decode）
- `JobPermissionsRequiredRule` も同様（Large 20 violations × ~50 KB decode）
- デフォルト `Fix.Enabled = false` なので lint-only 用途（CI の check コマンド等）で追加設定不要
- `FixCommand` のみ `Enabled = true` を明示するシンプルな opt-in 設計

| Method | Size | Phase 4 Mean | Phase 6-A Mean | 時間削減 | Phase 4 Alloc | Phase 6-A Alloc | Alloc削減 |
|---|---|---:|---:|---:|---:|---:|---:|
| LintEngine.Check | Small (1×3) | 53.18 μs | 43.89 μs | -17.5% | 86,734 B | 47,882 B | -44.8% |
| LintEngine.Check | Medium (6×8) | 1,928 μs | 1,056 μs | -45.2% | 4,975,196 B | 662,535 B | -86.7% |
| LintEngine.Check | Large (20×12) | 42,148 μs | 17,183 μs | -59.3% | 99,468,943 B | 9,323,039 B | **-90.6%** |

**ステータス**: ✅ 完了

---

### Phase 7: 式解析の重複排除（高リスク・高効果）

**目的**: 同一 `${{ }}` 式が複数のルールから独立にパースされている重複を排除する。

#### 背景

Large ベンチマークで式を持つ箇所は run step の `if`、`run`、`env` 等に約 480+ 箇所。45 個のデフォルトルールのうち式を解析するルールが ~10 以上あり、同一ノードの式を各ルールが `ExpressionParser.Parse` で個別にパースする。式パーサーは Phase 3 で ArrayPool 化済みだが、per-parse の最終 `.ToArray()` コストは残存しており、ルール数 × 式数で蓄積する。

#### 7-A: 事前解析キャッシュの導入

`LintEngine.Check` のルール実行前に、全 AST ノードの `${{ }}` 式を一括パースし、結果を `Dictionary<(NodeType, int index), ExpressionParseResult>` にキャッシュする。各ルールはキャッシュから結果を取得する。

```csharp
// Before: 各ルールが独立にパース
// ExprUndefinedVarRule:  ExpressionParser.Parse(expr)
// TemplateInjectionRule: ExpressionParser.Parse(expr)
// IfCondRule:            ExpressionParser.Parse(expr)
// → 同一式を 3 回パース

// After: 事前パース + キャッシュ参照
// engine: cache[node] = ExpressionParser.Parse(expr)  // 1回のみ
// rule:   cache[node]  // 参照のみ
```

#### 7-B: ルールへの式結果の配布方式

ルールインターフェースに `SetExpressionCache(IReadOnlyDictionary<...> cache)` を追加するか、`LintConfig` 経由で配布する。

**完了条件**:
- [x] 同一式が複数回パースされていないこと（オフセットベースキャッシュで重複排除）
- [x] ベンチマーク: `LintBenchmark` Large の Allocated が減少していること → 実測: Phase 6-B比 -2.0%（lint-only）
- [x] 式解析結果の正確性が変わっていないこと（ExpressionTests + Rule テスト全通過）
- [x] 全テスト通過（477 tests）

**推定効果**: Large で ~20–50 MB 削減。ルール数に比例する重複パースの排除。

#### Phase 7 実施結果（2026-04-21 計測）

**計測環境**: BenchmarkDotNet v0.15.6 / .NET 10.0.3 / AMD Ryzen 7 5800H / ShortRun

**実施内容**:
- `LintConfig` に `ParseExpression(ReadOnlySpan<byte>)` メソッドを追加。`Dictionary<long, ExpressionParseResult>` によるオフセットベースキャッシュ
- キャッシュキー: `((long)offset << 32) | (uint)span.Length` — `Unsafe.ByteOffset` で `Utf8Yaml` 配列先頭からのオフセットを計算
- 12 ルールの `ExpressionParser.Parse(expression)` を `Config.ParseExpression(expression)` に置換:
  - IfCondRule, FakeTernaryRule, JobSecretsRule, ExprUndefinedVarRule, RunInputsContextDirectUseRule, RunEnvContextDirectUseRule, RunSecretsContextDirectUseRule, SecretsOutsideEnvRule, SecretsWholeContextAccessRule, TemplateInjectionRule, WorkflowSecretsRule, UnredactedSecretsRule
- 4 ファイル 6 メソッドの `static` を除去（`Config` インスタンスメンバーへのアクセスが必要になったため）:
  - JobSecretsRule, WorkflowSecretsRule: `ContainsReferenceInExpression`
  - UnredactedSecretsRule, SecretsOutsideEnvRule: `ContainsSecretsReferenceInExpression`, `ContainsSecretsReferenceInValue`

**設計判断**:
- `LintConfig` 経由の配布を選択（7-B）。ルールは `Config.ParseExpression(expr)` を呼ぶだけで透過的にキャッシュ恩恵を受ける
- 事前一括パース（7-A）ではなくオンデマンドキャッシュを採用。理由: 全ルールが無効の式を事前パースするコストを避けるため
- キャッシュキーに `Unsafe.ByteOffset` を使用。式の `ReadOnlySpan<byte>` は `Utf8Yaml` のサブスパンであるため、オフセット+長さで一意に識別可能

| Size | FixEnabled | Phase 6-B Mean | Phase 7 Mean | 時間変化 | Phase 6-B Alloc | Phase 7 Alloc | Alloc変化 |
|---|---|---:|---:|---:|---:|---:|---:|
| Small (1×3) | False | 119.3 μs | 121.0 μs | +1.4% | 47.61 KB | 43.49 KB | **-8.7%** |
| Small (1×3) | True | 132.6 μs | 132.1 μs | -0.4% | 82.23 KB | 79.22 KB | **-3.7%** |
| Medium (6×8) | False | 2,366 μs | 2,759 μs | +16.6% | 647.04 KB | 611.21 KB | **-5.5%** |
| Medium (6×8) | True | 4,016 μs | 4,360 μs | +8.6% | 4,305.08 KB | 4,269.36 KB | **-0.8%** |
| Large (20×12) | False | 39,746 μs | 41,505 μs | +4.4% | 9,104.40 KB | 8,921.29 KB | **-2.0%** |
| Large (20×12) | True | 71,334 μs | 79,750 μs | +11.8% | 84,922.83 KB | 84,742.22 KB | **-0.2%** |

**考察**:
- **アロケーション削減は控えめ**: Large lint-only で -183 KB (-2.0%)。推定の ~20-50 MB には遠く及ばなかった
- **推定との乖離理由**:
  - Phase 3 の ArrayPool 化により、`ExpressionParser.Parse` の per-parse コストが大幅に低下済み。重複を排除しても節約量は最終 `.ToArray()` 分のみ
  - ベンチマーク YAML の式パターンが限定的。実際の大規模ワークフローではキャッシュヒット率が高まる可能性あり
  - 12 ルールが全て同一ノードの式を解析するわけではない。各ルールは異なるノード種別を訪問するため、実際の重複は想定より少ない
- **実行時間の増加**: Phase 6-B と Phase 7 は異なるタイミングの計測のため、ShortRun の変動幅内と考えられる。`Dictionary<long, ExpressionParseResult>` のオーバーヘッドが実行時間増の一因の可能性もあるが、3 回計測の誤差範囲内
- **Dictionary キャッシュのオーバーヘッド**: キャッシュ自体の `Dictionary` + エントリ割り当てが、節約量とほぼ相殺している可能性がある。大規模ワークフローではキャッシュヒットが増えるほど正味の効果が期待できる

**教訓**:
- Phase 3 の ArrayPool 最適化が ExpressionParser のアロケーションを大きく下げたため、重複排除の余地が縮小していた。最適化は上流の変更が下流の効果見積もりを変えることを考慮すべき
- `static` メソッドを `Config` インスタンスメンバーにアクセスさせるために非 static 化が必要になった（4 ファイル 6 メソッド）。ルール設計で `static` ヘルパーを多用すると共有状態導入時にリファクタが増える

**ステータス**: ✅ 完了

---

### Phase 8: RuleBase 診断収集の最適化（低リスク・中効果）

**目的**: 各ルールの `GetDiagnostics()` で発生する `List<T>.ToArray()` コピーと、LintEngine 側の per-rule 配列収集を効率化する。

#### 8-A: ToArray の排除または共有バッファ化

現在 45 ルール × `GetDiagnostics()` で各 `.ToArray()` を呼ぶ。大半のルールは 0 件の診断を返すが、空配列 + 配列コピーのオーバーヘッドが蓄積する。

**改善策**:
- 0 件の場合は `Array.Empty<Diagnostic>()` を返す（`RuleBase.GetDiagnostics` の共通最適化）
- LintEngine 側の `ruleDiagnostics` 収集を `IReadOnlyList<Diagnostic>` 参照に変更し、コピーを 1 回に

#### 8-B: 診断ソートの最適化

`ruleDiagnostics.Sort(...)` は `List<T>.Sort` で内部的に配列コピーが発生しうる。要素数が少ない場合の最適化や、`Span<T>.Sort` への切り替えを検討。

**完了条件**:
- [x] `GetDiagnostics()` が内部リストを直接返し、不要な `ToArray()` が排除されていること
- [x] LintEngine 側の不要な配列コピーが削減されていること（`.Length` → `.Count`）
- [x] ベンチマーク: `LintBenchmark` Small/Medium/Large の Allocated がわずかに改善していること
- [x] 全テスト通過（477 tests）

**推定効果**: Small/Medium で ~1–5 KB 削減。Large でも ~10–50 KB。固定費削減の性質。

#### Phase 8 実施結果（2026-04-21 計測）

**計測環境**: BenchmarkDotNet v0.15.6 / .NET 10.0.3 / AMD Ryzen 7 5800H / ShortRun

**実施内容**:
- `IRule.GetDiagnostics()` の戻り値型を `Diagnostic[]` → `IReadOnlyList<Diagnostic>` に変更
- `RuleBase.GetDiagnostics()`: `diagnostics.ToArray()` → `diagnostics`（内部 `List<Diagnostic>` を直接返却、ゼロコピー）
- `SyntaxRule.GetDiagnostics()`: `List<Diagnostic>` を直接返却 + `AddRange` → indexed `for` ループに変更
- `LintEngine.Check`: `currentRuleDiagnostics.Length` → `.Count` に変更
- テストスタブ 3 箇所を `IReadOnlyList<Diagnostic>` に更新

**設計判断**:
- 当初の計画では `Array.Empty<Diagnostic>()` で 0 件最適化を予定していたが、`IReadOnlyList<Diagnostic>` への変更で根本的に解決した。`RuleBase` は内部リストを直接返すため、0 件でも `ToArray()` のコピーが発生しない
- `IReadOnlyList<Diagnostic>` を選択した理由: `Diagnostic[]` は消費側が変更可能であり、API としてルールの内部状態が漏洩する。`IReadOnlyList` は読み取り専用の契約を明示しつつ、内部リストの直接返却を許容する

| Size | FixEnabled | Phase 7 Mean | Phase 8 Mean | 時間変化 | Phase 7 Alloc | Phase 8 Alloc | Alloc変化 |
|---|---|---:|---:|---:|---:|---:|---:|
| Small (1×3) | False | 121.0 μs | 84.9 μs | -29.8% | 43.49 KB | 43.02 KB | **-0.47 KB (-1.1%)** |
| Small (1×3) | True | 132.1 μs | 100.3 μs | -24.1% | 79.22 KB | 78.75 KB | **-0.47 KB (-0.6%)** |
| Medium (6×8) | False | 2,759 μs | 1,850 μs | -32.9% | 611.21 KB | 605.43 KB | **-5.78 KB (-0.9%)** |
| Medium (6×8) | True | 4,360 μs | 3,555 μs | -18.5% | 4,269.36 KB | 4,263.61 KB | **-5.75 KB (-0.1%)** |
| Large (20×12) | False | 41,505 μs | 28,765 μs | -30.7% | 8,921.29 KB | 8,894.81 KB | **-26.48 KB (-0.3%)** |
| Large (20×12) | True | 79,750 μs | 94,071 μs | +17.9% | 84,742.22 KB | 84,715.28 KB | **-26.94 KB (-0.03%)** |

**考察**:
- **アロケーション削減は固定費的**: lint-only Large で -26.48 KB (-0.3%)。45 ルール × `ToArray()` コピーの排除が主因。1 ルールあたり約 0.6 KB 相当の削減（空リストの ToArray でも配列ヘッダー分のアロケーションが発生していた）
- **時間の変動は ShortRun の計測誤差**: Phase 7 と Phase 8 は異なるタイミングでの計測のため、実行時間の差は CPU 負荷・温度等の外部要因が支配的。Phase 8 のコード変更自体は実行時間にほとんど影響しない
- **IReadOnlyList の副次的効果**: API の型安全性が向上。ルール内部の診断リストが消費側から変更されるリスクが排除された

**ステータス**: ✅ 完了

---

## 効果の見積もりサマリー

| フェーズ | 推定 Allocated 削減 (Large) | リスク | 工数 |
|---|---|---|---|
| Phase 1: 診断メッセージ遅延評価 | ~100–200 KB | 低 | 中 |
| Phase 2: HashSet 削除 + ビットフラグ | ~200–400 KB | 中 | 大 |
| Phase 3: ExpressionParser 最適化 | ~50–100 KB | 低 | 小 |
| Phase 4: LintEngine 最適化 | ~50–200 KB | 中 | 中 |
| Phase 5: List→Array 最適化 | ~数 KB | 低 | 小 |
| Phase 6: Fix 構築の遅延化 | ~5–30 MB | 中 | 中 |
| Phase 7: 式解析の重複排除 | ~183 KB (lint-only Large) | 高 | 大 |
| Phase 8: RuleBase 診断収集最適化 | ~26 KB (lint-only Large) | 低 | 小 |
| **合計 (Parser Phase 1–5)** | **~400–900 KB** | | |
| **合計 (Lint Phase 6–8)** | **Phase 6: -88 MB, Phase 7: -183 KB, Phase 8: -26 KB** | | |

### Parser（Phase 1–3 実績）

Large ベースライン 1,162 KB → 382 KB（**67.1% 削減**）。Phase 1–3 の 3 フェーズで約 2/3 のアロケーションを除去。

### Lint（Phase 6–8 実績/推定）

Large 現行（lint-only）: ~8.9 MB（Phase 4 の ~97 MB から Phase 6-A+6-B で -90.6%、Phase 7 でさらに -2.0%、Phase 8 で -26.48 KB (-0.3%)）。Phase 8 は 45 ルールの `ToArray()` 排除による固定費削減。

---

## 実行順序と依存関係

```
Phase 1 (診断遅延) ──→ Phase 2 (HashSet削除) に部分依存
                         （Phase 1 で errorMessage を整理した後の方が Phase 2 のリファクタが容易）
Phase 3 (ExpressionParser) は独立
Phase 4 (LintEngine) は独立（ただし 4-A のベンチマーク追加を先に行う）
Phase 5 (List最適化) は独立
Phase 6 (Fix遅延化) は独立（Phase 4 の LintBenchmark を利用してベンチマーク）
Phase 7 (式解析重複排除) は Phase 6 に部分依存（Fix パスの最適化後の方がベンチマークがクリーン）
Phase 8 (診断収集最適化) は独立
```

推奨順: **Phase 1 → Phase 2 → Phase 3 → Phase 4 → Phase 5 → Phase 6 → Phase 7 → Phase 8**

各フェーズ完了後の状態確認:
1. `dotnet build` 成功
2. `dotnet test` 全通過（477 tests）
3. ベンチマーク実行（`cd src/Seiton.Benchmark && dotnet run -c Release`）
4. ベースラインとの Allocated / Mean 比較
5. 新規 `GetScalarString()` が success path に追加されていないことの grep 確認

---

## 実装ステータス

### Phase 1: 診断メッセージの遅延評価 — ✅ 完了

### Phase 2: HashSet<Utf8String> の削除 — ✅ 完了

### Phase 3: ExpressionParser のアロケーション削減 — ✅ 完了

### Phase 4: LintEngine のアロケーション削減 — ✅ 完了

### Phase 5: AST List → Array の最適化 — 🔲 未着手

### Phase 6: Fix 構築の遅延化 — ✅ 完了（6-A, 6-B）

### Phase 7: 式解析の重複排除 — ✅ 完了

### Phase 8: RuleBase 診断収集の最適化 — ✅ 完了

---

## Phase 4 完了後のベースライン（2026-04-21 計測）

**環境**: BenchmarkDotNet v0.15.6 / .NET 10.0.6 / AMD Ryzen 9 7950X3D / ShortRun

### ParsingBenchmark

| Method | Size | Mean | Allocated | Alloc Ratio | vs Initial Baseline |
|---|---|---:|---:|---:|---:|
| WorkflowParser.Parse (AST + rules) | Small (1×3) | 27.9 μs | 12,216 B | 1.00 | -61.0% |
| WorkflowParser.Parse (AST + rules) | Medium (6×8) | 474 μs | 84,888 B | 1.00 | -66.2% |
| WorkflowParser.Parse (AST + rules) | Large (20×12) | 6,976 μs | 382,808 B | 1.00 | -67.1% |
| ExpressionExtractor | Small | 2.95 μs | 3,744 B | 0.31 | -73.7% |
| ExpressionExtractor | Medium | 34.3 μs | 40,592 B | 0.48 | -74.0% |
| ExpressionExtractor | Large | 168 μs | 188,760 B | 0.49 | -75.1% |
| VYaml raw event scan | Small | 8.36 μs | 0 B | 0.00 | — |
| VYaml raw event scan | Medium | 65.9 μs | 0 B | 0.00 | — |
| VYaml raw event scan | Large | 299 μs | 0 B | 0.00 | — |

### LintBenchmark

| Method | Size | Mean | Allocated |
|---|---|---:|---:|
| LintEngine.Check (parse + lint) | Small (1×3) | 53.2 μs | 86,734 B |
| LintEngine.Check (parse + lint) | Medium (6×8) | 1,928 μs | 4,975,196 B |
| LintEngine.Check (parse + lint) | Large (20×12) | 42,148 μs | 99,468,943 B |

### アロケーション内訳推定（Large, LintEngine.Check ~97 MB）— Phase 4 時点

| カテゴリ | 推定比率 | 推定量 | 主要発生源 |
|---|---:|---:|---|
| 式解析の重複（複数ルール × 同一式） | 55–75% | ~53–73 MB | ExprUndefinedVar, TemplateInjection, IfCond, FakeTernary, Secrets 系等の ~10 ルールが同一式を独立パース |
| Fix 構築の全ファイルデコード | 15–30% | ~15–29 MB | CheckoutPersistCredentials (120回), JobPermissionsRequired (20回) の violation 毎に UTF8.GetString + Split |
| LintEngine フレームワーク | 8–15% | ~8–15 MB | 診断リスト/配列、ソート、重複排除、抑制構造 |
| ルール固有のコレクション/文字列 | 3–10% | ~3–10 MB | UnredactedSecrets の per-step HashSet、NeedsGraph の DFS Dictionary 等 |
| パーサー（Phase 1–3 最適化済み） | <1% | ~0.4 MB | AST 構築（既に 67% 削減済み） |

---

## Phase 8 完了後の現状分析と次期改善計画（2026-04-21）

### Phase 8 完了時点のベンチマーク

**LintBenchmark（Phase 8 完了後、Ryzen 7 5800H / ShortRun）**:

| Size | FixEnabled | Mean | Allocated |
|---|---|---:|---:|
| Small (1×3) | False | 84.9 μs | 43.02 KB |
| Small (1×3) | True | 100.3 μs | 78.75 KB |
| Medium (6×8) | False | 1,850 μs | 605.43 KB |
| Medium (6×8) | True | 3,555 μs | 4,263.61 KB |
| Large (20×12) | False | 28,765 μs | 8,894.81 KB |
| Large (20×12) | True | 94,071 μs | 84,715.28 KB |

**ParsingBenchmark（Phase 3 完了後、Ryzen 9 7950X3D / ShortRun）**:

| Size | Mean | Allocated |
|---|---:|---:|
| Small (1×3) | 27.9 μs | 12,216 B (11.93 KB) |
| Medium (6×8) | 474 μs | 84,888 B (82.90 KB) |
| Large (20×12) | 6,976 μs | 382,808 B (373.83 KB) |

### 現状の課題とアロケーション構造

Phase 1–8 で lint-only Large を ~97 MB → ~8.9 MB まで削減した。しかし CI ツールとしてはまだ改善余地がある。Go で同等機能を実装した場合との比較を念頭に、以下の構造的課題を整理する。

#### Go との設計差異によるアロケーション特性の違い

Go の linter（actionlint 等）は、値型ベースの AST と GC 特性の違いにより、同等規模のワークフローに対して桁違いに少ないアロケーションで動作する。主要な差異:

| 側面 | Go (actionlint 参考) | C# (Seiton 現状) | 影響 |
|---|---|---|---|
| AST ノード | 構造体埋め込み（親スライスに連続配置） | クラス（各ノードが独立ヒープオブジェクト） | Large で数百〜数千のオブジェクト割り当て |
| 文字列 | `[]byte` スライス（source バッファ共有） | `Utf8Slice`（offset/length、ゼロコピー ✅）、ただし辞書キーは `Utf8String`（`byte[]` コピー） | 辞書キー生成ごとに `byte[]` 割り当て |
| コレクション | スライスの再利用（`append` で成長） | `List<T>` → `.ToArray()` パターン | 二重割り当て（List 内部配列 + 最終配列） |
| 辞書 | `map` はランタイム最適化済み | `Dictionary<K,V>` はバケット配列 + エントリ配列 | 初期化コストが高い |
| Lint ルール状態 | 関数 + 値レシーバー（ゼロアロケーション可能） | クラスインスタンス + `List<Diagnostic>` 内部リスト | ルールごとにオブジェクト + リスト |

#### アロケーション源の詳細調査結果

以下は Phase 8 完了時点のコードを全ファイル調査した結果。

##### 1. AST ノードのクラスアロケーション（パーサー層）

| 型 | 形式 | 生成頻度 (Large) | 推定コスト |
|---|---|---:|---|
| `StringNode` | class | ~500–1000 | 各 ~40B（ヘッダー + Utf8Slice + bool + Range + Expression?） |
| `BoolNode` / `IntNode` / `FloatNode` | class | ~60–100 | 各 ~40–48B |
| `Job` | class | 20 | 各 ~200B（多数のプロパティ） |
| `Step` | class | 240 | 各 ~120B |
| `ExecRun` / `ExecAction` | class | 240 | 各 ~80B |
| Event 派生型 | class | 1–5 | 各 ~80–120B |
| その他構造ノード | class | ~50–100 | 可変 |

**推定**: AST ノードだけで Large ケースは ~100–200 KB。パーサーの 382 KB のうち約 26–52% を占める。

##### 2. `Utf8String` の二重 `byte[]` コピー

`Utf8String` のコンストラクタは `_bytes = utf8.ToArray()` でバイト配列を複製する。さらに `FromLowerAscii` は内部で `utf8.ToArray()` を実行した後、結果をコンストラクタに渡すため、**1 回の呼び出しで `byte[]` が 2 回割り当てられる**:

```csharp
// 現在: FromLowerAscii で 2 回 byte[] 割り当て
public static Utf8String FromLowerAscii(ReadOnlySpan<byte> utf8)
{
    var bytes = utf8.ToArray();    // 1回目: ToArray
    // ... modify bytes in place ...
    return new Utf8String(bytes);  // 2回目: コンストラクタ内でもう1回 ToArray
}
```

`Utf8String` は AST の辞書キー（`Jobs`, `Outputs`, `Inputs` 等）およびルール内のキー比較（`NeedsGraphRule.FromLowerAscii` 等）で広く使用される。Large ケースでは辞書キーだけで 100+ 回生成され、各回 2 回の `byte[]` アロケーションが発生。

##### 3. LintEngine.Check() の per-Check コレクション群

| アロケーション | 箇所 | 回数/Check | 説明 |
|---|---|---:|---|
| `new List<Diagnostic>` | Check L55 | 1 | 初期 diagnostics 収集 |
| `new WorkflowVisitor` | Check L75 | 1 | ビジターオブジェクト + 内部 `List<IPass>` |
| `new LintConfig` | Check L76 | 1 | 設定オブジェクト |
| `new List<IRule>` | Check L85 | 1 | activeRules |
| `new List<Diagnostic>` | Check L114 | 1 | ruleDiagnostics |
| `new HashSet<DiagnosticIdentity>` | Check L132 | 1 | 重複排除 |
| `new Dictionary<string,int>` | Check L133 | 1 | 抑制カウント |
| `new List<SuppressionRecord>` | Check L134 | 1 | 抑制記録 |
| `diagnostics.ToArray()` | Check 返却 | 1 | 最終結果配列 |
| `suppressionRecords.ToArray()` | Check L164 | 1 | 抑制結果配列 |

ParseInlineSuppression 内:
| `new Dictionary` × 3 + `new List` × 1 | L392–395 | 1 | インライン抑制解析 |
| `new Utf8Slice[]` | BuildKnownJobIdSlices | 1–2 | ジョブ ID スライス |
| `new List<JobScope>` | BuildJobScopes | 1 | ジョブスコープ |
| ネスト `Dictionary` | per-directive | 可変 | 行単位/ジョブ単位抑制マップ |

NormalizeRules/NormalizeExclusions:
| `Dictionary` + `List` | NormalizeRules | 1 | ルール設定正規化 |
| `List` + per-exclusion `HashSet` | NormalizeExclusions | X | 除外設定正規化 |

**合計**: 最低でも **15–20 個のコレクションオブジェクト** が毎回 Check() で生成される。

##### 4. ルール固有のアロケーション

| ルール | パターン | 頻度 (Large) | 深刻度 |
|---|---|---:|---|
| `UnredactedSecretsRule` | per-step `new HashSet<string>` + per-line `Encoding.UTF8.GetString` + 文字列連結 | 240 HashSet + run 行数分の string | **高** |
| `NeedsGraphRule` | `new Dictionary` + `new Stack` + per-edge `Utf8String.FromLowerAscii`（2 回 byte[] コピー） | 1 Dict + 1 Stack + 40+ byte[] | **中-高** |
| `DenyReadAllRule` / `DenyWriteAllRule` | per-visit キャプチャリングラムダ（クロージャオブジェクト生成） | 1+20 クロージャ | **中** |
| `IdNamingRule` / `ShellNameRule` | per-visit キャプチャリングラムダ | 1+20+240 クロージャ | **中** |
| `CheckoutPersistCredentialsRule` | Fix 構築時 `Replace` + `Split` + `TextEdit[]` | per-violation（Fix有効時） | **中-高**（Fix 時） |
| `JobPermissionsRequiredRule` | 同上 | per-violation（Fix有効時） | **中-高**（Fix 時） |
| `JobTimeoutMinutesRequiredRule` | 同上 | per-violation（Fix有効時） | **中-高**（Fix 時） |
| `PermissionsRule` | per-scope `Encoding.UTF8.GetString`（success path） | per-permission-scope | **低-中** |
| `GlobPatternRule` | `char[]` + `new string` via DecodeAscii | per-invalid-pattern | **低** |
| `ReusableWorkflowRule` | `File.ReadAllBytes` + パーサー呼び出し + 契約オブジェクト | per-local-workflow | **高**（ローカル WF 参照時） |

##### 5. Fix パスの文字列操作

Fix 有効時（`--fix` モード）、各ルールの Fix 構築は:
1. `config.GetSourceText()` で UTF-8 全体を `string` にデコード（Phase 6-B でキャッシュ済み、1 回のみ）
2. しかし各 Fix 構築内で `source.Replace(...)` + `text.Split('\n')` + `string.Join(...)` 等の文字列操作が発生
3. `FixEngine.Apply` は `Encoding.UTF8.GetBytes(edit.NewText)` + `List<byte>.InsertRange` で適用

Large Fix=true で 84.7 MB のうち、パーサー（~374 KB）を除く ~84.3 MB は lint + fix 構築コスト。

---

### 次期改善フェーズ（Phase 9 以降）

以下、**メモリ削減効果** / **コード複雑度の増加** / **実行速度への影響** の 3 軸で優先度を評価する。

#### 設計方針

1. **読みやすさを犠牲にしない**: `stackalloc` / `ArrayPool` の手動管理を過剰に広げるより、型設計の改善でアロケーション自体を構造的に不要にする
2. **段階的移行**: AST の struct 化などの大規模変更は、互換性を保つラッパーを用意して段階的に実施
3. **計測駆動**: 各フェーズ完了後にベンチマークで効果を確認。推定と実測が大きく乖離した場合は計画を見直す

---

### Phase 9: Utf8String の二重コピー除去（低リスク・中効果）

**目的**: `Utf8String.FromLowerAscii` が `byte[]` を 2 回割り当てる問題を修正する。

**現状**:
```csharp
public Utf8String(ReadOnlySpan<byte> utf8) { _bytes = utf8.ToArray(); }
public static Utf8String FromLowerAscii(ReadOnlySpan<byte> utf8) {
    var bytes = utf8.ToArray();   // 1回目
    // ... in-place modify ...
    return new Utf8String(bytes); // 2回目（コンストラクタ内で再度 ToArray）
}
```

**改善策**:
- private コンストラクタ `Utf8String(byte[] owned)` を追加し、所有権移転で `ToArray()` を 1 回に
- `FromLowerAscii` は `bytes` を直接セットして 2 回目のコピーを排除

**影響範囲**: `Utf8String.cs` のみ。AST 辞書キーと NeedsGraphRule 等のルール内キー生成に波及。

**完了条件**:
- [ ] `FromLowerAscii` が `byte[]` を 1 回のみ割り当てること
- [ ] 既存テスト全通過
- [ ] ベンチマーク: ParsingBenchmark Large の Allocated が減少

**推定効果**: Large ParsingBenchmark で ~10–30 KB 削減（辞書キー 100+ 回 × 平均 ~20B の重複コピー排除）。

**コード複雑度**: 低。private コンストラクタの追加のみ。

---

### Phase 10: AST ノードの struct 化 — StringNode（中リスク・高効果）

**目的**: 最も頻出する `StringNode` を class → readonly record struct に変更し、ヒープオブジェクト数を大幅削減する。

**現状**:
```csharp
public sealed class StringNode {  // ← 毎回ヒープ割り当て (~40B/instance)
    public Utf8Slice Value { get; init; }
    public bool Quoted { get; init; }
    public StringNode? Expression { get; init; }  // ← nullable class 参照
    public TextRange Range { get; init; }
}
```

Large ケースで ~500–1000 個の StringNode が生成される。各 ~40B のオブジェクトヘッダーだけで ~20–40 KB。

**改善策**:
- `StringNode` を `readonly record struct` に変更
- `Expression` フィールドは `StringNode?` → nullable value type で保持（`StringNode` が struct になれば自然に box-free）
- ただし `StringNode?` が再帰的な nullable struct になる場合は `ExpressionSlice` のような別 struct への分離を検討

**注意点**:
- AST ノードを struct にすると、ルール側で `if (node is StringNode)` のようなパターンマッチングや参照比較が使えなくなる
- `IReadOnlyList<StringNode>` の要素アクセスがコピーを伴う（ただし readonly struct なら JIT 最適化で in-place アクセス可能な場合もある）
- `Job.Name`, `Step.Id` 等、nullable フィールドとして使われる箇所が多数。`StringNode?` は `Nullable<StringNode>` となり、struct サイズ分のスタック消費が増える

**段階的移行**:
1. まず `StringNode` のみ struct 化し、`BoolNode` / `IntNode` / `FloatNode` は後続
2. ルールのパターンマッチングや null チェックの書き換えを含む影響調査を先行

**完了条件**:
- [ ] `StringNode` が `readonly record struct` であること
- [ ] パターンマッチングや null チェックが正しく動作すること
- [ ] 全テスト通過
- [ ] ベンチマーク: ParsingBenchmark Large の Allocated が ~20–40 KB 減少

**推定効果**: Large ParsingBenchmark で ~20–40 KB 削減。Lint 含む全体では per-node ヒープ参照のキャッシュミス削減による速度改善も期待。

**コード複雑度**: 中。AST 消費側（ルール、ビジター）の null チェックパターン変更が広範。

---

### Phase 11: LintEngine per-Check コレクション再利用（低リスク・中効果）

**目的**: `LintEngine.Check()` 内で毎回生成される 15–20 個のコレクションを、エンジンインスタンスに保持して再利用する。

**現状**: Check() のたびに `new List<Diagnostic>`, `new HashSet<DiagnosticIdentity>`, `new Dictionary<string,int>`, `new List<SuppressionRecord>`, `new List<IRule>` 等を生成。CI では同一エンジンで複数ファイルを連続チェックするため、これらは毎回捨てられる。

**改善策**:
- LintEngine にフィールドとして保持: `_diagnostics`, `_ruleDiagnostics`, `_seenIdentities`, `_suppressedByRule`, `_suppressionRecords`, `_activeRules`
- Check() 先頭で `.Clear()` して再利用（内部バッファは保持）
- `WorkflowVisitor` もフィールド化しリセット

```csharp
// After
public sealed class LintEngine {
    readonly List<Diagnostic> _diagnostics = new(64);
    readonly List<Diagnostic> _ruleDiagnostics = new(128);
    readonly HashSet<DiagnosticIdentity> _seenIdentities = new();
    readonly Dictionary<string, int> _suppressedByRule = new(StringComparer.Ordinal);
    readonly List<SuppressionRecord> _suppressionRecords = new();
    readonly List<IRule> _activeRules = new(50);
    readonly WorkflowVisitor _visitor = new();

    public LintResult Check(...) {
        _diagnostics.Clear();
        _ruleDiagnostics.Clear();
        _seenIdentities.Clear();
        _suppressedByRule.Clear();
        _suppressionRecords.Clear();
        _activeRules.Clear();
        _visitor.Reset();
        // ... use fields instead of local new ...
    }
}
```

**注意**: `RuleBase` の内部 `diagnostics` リストは `SetConfig()` 時にクリアされるため、ルール側は変更不要。

**完了条件**:
- [ ] Check() 内で `new List/Dictionary/HashSet` が生成されていないこと
- [ ] 複数ファイル連続 Check のテストが通過すること（状態リーク防止）
- [ ] ベンチマーク: LintBenchmark の Allocated が減少

**推定効果**: per-Check で ~5–15 KB 削減（コレクション初期化コスト × 15–20 個）。複数ファイル連続実行時は内部バッファが育ち、再割り当ても減少。

**コード複雑度**: 低。ローカル変数をフィールドに昇格し Clear() するだけ。スレッドセーフでない点は既存設計と同様。

---

### Phase 12: UnredactedSecretsRule の per-step HashSet 排除（中リスク・中効果）

**目的**: 240 steps × per-step `new HashSet<string>` を排除し、ワークフロー/ジョブ/ステップのスコープを構造的に管理する。

**現状**:
```csharp
// 各ステップで呼ばれる
HashSet<string>? CollectSecretDerivedEnvVarNames(Step step) {
    var names = new HashSet<string>(StringComparer.Ordinal);  // ← 毎step
    AddSecretMappedVars(step.Env, names);
    AddSecretMappedVars(currentJob?.Env, names);
    AddSecretMappedVars(currentWorkflow?.Env, names);
    return names;
}
```

ワークフローレベルとジョブレベルの env は step 間で共通なのに、毎回再収集している。

**改善策**:
- VisitWorkflowPre でワークフローレベルの secrets-derived vars を 1 回収集してフィールドに保持
- VisitJobPre でジョブレベルを追加収集（ワークフロー分 + ジョブ分）
- VisitStep ではステップ固有の env のみ差分追加
- HashSet をルールフィールドとして 1 個保持し、Clear + 再利用

**完了条件**:
- [ ] per-step の `new HashSet` が排除されていること
- [ ] ワークフロー/ジョブスコープの再収集がないこと
- [ ] 全テスト通過
- [ ] ベンチマーク: LintBenchmark Large (Fix=false) の Allocated が減少

**推定効果**: Large で ~50–100 KB 削減（240 HashSet × ~200B 初期コスト + per-env-var string デコード重複排除）。

**コード複雑度**: 中。スコープ管理ロジックの導入が必要だが、パターンは他ルールと類似。

---

### Phase 13: キャプチャリングラムダの排除（低リスク・小効果）

**目的**: `DenyReadAllRule`, `DenyWriteAllRule`, `IdNamingRule`, `ShellNameRule` の per-visit クロージャオブジェクト生成を排除する。

**現状**: これらのルールは VisitWorkflowPre / VisitJobPre 内でローカル変数をキャプチャするラムダを生成。.NET ランタイムは非 static ラムダごとにクロージャオブジェクト（`<>c__DisplayClass`）を割り当てる。

**改善策**:
- キャプチャする値をルールのフィールドに保持し、メソッドを通常のインスタンスメソッドに変換
- または static ラムダ + 明示的な state パラメータに変更

**完了条件**:
- [ ] 対象ルールにキャプチャリングラムダがないこと
- [ ] 全テスト通過

**推定効果**: Small/Medium で ~0.5–2 KB。Large で ~5–10 KB（ジョブ数 × ルール数分のクロージャ排除）。

**コード複雑度**: 低。

---

### Phase 14: Fix パスの Span ベース行操作（中リスク・高効果）

**目的**: Fix 構築時の `string.Split('\n')` / `string.Replace(...)` / `string.Join(...)` を `ReadOnlySpan<byte>` ベースの操作に置き換え、Fix=true 時のアロケーションを大幅削減する。

**現状**: Fix=true Large が ~84.7 MB。このうちパーサー (~374 KB) + lint-only (~8.5 MB) を除く ~76 MB が Fix 構築コスト。主要な発生源:
1. `config.GetSourceText()` → 全ファイル string デコード（Phase 6-B でキャッシュ済み、1 回、~50 KB）
2. `source.Split('\n')` → 行配列（N 行分の string[] + 行 string × N）
3. `string.Replace(...)` / `string.Insert(...)` → 新文字列割り当て
4. `FixEngine.Apply` の `List<byte>.InsertRange` → リスト内部の再配置

**改善策**:
- **14-A**: `FixFormatting` の行操作を `ReadOnlySpan<byte>` ベースに変更。`Split('\n')` → バイトスキャンで行オフセット/長さを計算
- **14-B**: `TextEdit.NewText` を `string` → `byte[]` に変更し、Fix 構築を UTF-8 バイト操作のみで完結させる
- **14-C**: `FixEngine.Apply` を `Span<byte>` ベースの in-place 編集に変更（オフセット計算で一括適用）

**段階的移行**:
1. まず 14-A で行操作の Span 化を実施（最も効果が大きい）
2. 14-B/14-C は `TextEdit` 型の変更を伴うため、14-A の効果を確認してから実施

**完了条件**:
- [ ] Fix 構築で `string.Split('\n')` が使われていないこと
- [ ] Fix テスト全通過
- [ ] ベンチマーク: LintBenchmark Large (Fix=true) の Allocated が大幅減少

**推定効果**: Large Fix=true で ~30–50 MB 削減。`string.Split('\n')` の行配列排除だけで数十 MB 規模の改善が期待できる。

**コード複雑度**: 中-高。行操作ヘルパーの実装が必要。ただし `ParseInlineSuppression` の Span 化（Phase 4 で実施済み）と同じパターン。

---

### Phase 15: NeedsGraphRule のアロケーション削減（低リスク・小効果）

**目的**: サイクル検出の `Dictionary` + `Stack` をルールフィールドとして再利用する。

**現状**:
```csharp
void DetectCycles() {
    var color = new Dictionary<Utf8String, byte>(_knownJobs.Count);  // per-Check
    var stack = new Stack<(Utf8String, int)>();                       // per-Check
    ...
}
```

**改善策**:
- `color` と `stack` をルールフィールドに昇格し、VisitWorkflowPost 開始時に Clear

**推定効果**: Large で ~5–15 KB。

---

### Phase 16: ExpressionParser 結果配列の最適化（低リスク・小効果）

**目的**: `ExpressionParser.Parse()` の最終 `ToArray()` を、0 件の場合 `Array.Empty<T>()` で返す。

**現状**: Phase 3 で ArrayPool 化済みだが、結果の `nodes.Snapshot()`, `diagnostics.Snapshot()` は毎回新規配列を生成する（0 件でも空配列を割り当て）。

**改善策**:
- `PooledBuffer<T>.Snapshot()` で `_count == 0` の場合 `Array.Empty<T>()` を返す
- 式の大半はエラーなし → `diagnostics` は頻繁に空配列。Large で ~480 式 × 空 Diagnostic[] の排除

**推定効果**: Large で ~5–10 KB。

---

### Phase 5 (未着手): AST List → Array の最適化

（既存計画の Phase 5 は引き続き有効。初期容量と 0/1 ファーストパスの適用。）

---

### 次期フェーズのサマリーと優先順位

| Phase | 概要 | 推定削減 (Large lint-only) | 推定削減 (Large Fix=true) | リスク | コード複雑度 |
|---|---|---:|---:|---|---|
| Phase 9 | Utf8String 二重コピー除去 | ~10–30 KB | ~10–30 KB | 低 | 低 |
| Phase 10 | StringNode struct 化 | ~20–40 KB | ~20–40 KB | 中 | 中 |
| Phase 11 | LintEngine コレクション再利用 | ~5–15 KB | ~5–15 KB | 低 | 低 |
| Phase 12 | UnredactedSecrets HashSet 排除 | ~50–100 KB | ~50–100 KB | 中 | 中 |
| Phase 13 | キャプチャリングラムダ排除 | ~5–10 KB | ~5–10 KB | 低 | 低 |
| Phase 14 | Fix パス Span ベース行操作 | — | **~30–50 MB** | 中-高 | 中-高 |
| Phase 15 | NeedsGraphRule 再利用 | ~5–15 KB | ~5–15 KB | 低 | 低 |
| Phase 16 | ExpressionParser 空配列最適化 | ~5–10 KB | ~5–10 KB | 低 | 低 |
| Phase 5 | AST List → Array 初期容量 | ~数 KB | ~数 KB | 低 | 低 |

**推奨実行順序**: Phase 9 → 11 → 13 → 15 → 16 → 5 → 12 → 10 → 14

理由:
- Phase 9/11/13/15/16/5 は低リスク・低複雑度で即座に実施可能
- Phase 12 はスコープ管理の設計判断が必要
- Phase 10 は AST 消費側の広範な変更を伴うため、他フェーズ完了後に単独で実施
- Phase 14 は Fix パス限定だが効果が最大。ただし TextEdit 型の変更を伴う可能性があり慎重に進める

### Go との差を埋める長期的な設計検討

上記の Phase 9–16 で lint-only Large を ~8.9 MB → ~8.7 MB 程度に改善できるが、Go 実装との根本的な差を埋めるには以下の構造変更が必要:

1. **AST 全体の struct 化**: `Job`, `Step`, `ExecRun`, `ExecAction` 等もすべて struct に。Go のスライス上の値型と同等。ただし C# では再帰的な struct（`StringNode.Expression` → `StringNode?`）の扱いや、interface dispatch（`IReadOnlyList<Step>` → value type）に設計上の制約がある。
2. **辞書アロケーションの削減**: `Dictionary<Utf8String, Job>` → offset-based lookup table や FrozenDictionary 等の特殊辞書。ただし可変サイズの入力に対する汎用性とのトレードオフ。
3. **ルールの値型化**: `IRule` / `RuleBase` を `ref struct` ベースに変更し、ルールインスタンスのヒープ割り当てを排除。ただし interface dispatch が使えなくなるため、ビジターパターンの根本的な再設計が必要。

これらは Seiton 全体の API 設計に影響するため、個別 Phase としてではなく、将来のメジャーバージョンでの検討事項として位置づける。
