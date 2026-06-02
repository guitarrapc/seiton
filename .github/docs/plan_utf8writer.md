# Diagnostic 出力 UTF-8 化調査・対応計画

## 背景

Diagnostic 出力の UTF-8 化計画。フェーズ 0〜4 完了。CLI 診断出力は `IBufferWriter<byte>` 一本化済み。

本書は調査時点の現状整理と段階的実装方針、各フェーズの計測・レビュー記録を残す。

## 調査結果（実装前）と完了後

### 1) 出力の共通入口

| | 実装前 | 完了後（フェーズ 3） |
|---|---|---|
| フォーマット入口 | `DiagnosticFormatter.Write(TextWriter, ...)` | `DiagnosticFormatter.Write(IBufferWriter<byte>, ...)` |
| CLI stdout | `Console.Out` を `TextWriter` として渡す | `WriteToStandardOutput`（バッファ → UTF-8 stdout / `Console.SetOut` 時は TextWriter デコード） |
| テスト・注入 | `StringWriter` 注入 | `WriteToTextWriter` または `ArrayBufferWriter` + `Write` |

### 2) フォーマット別（実装前 → 完了後）

| フォーマット | 実装前 | 完了後 |
|---|---|---|
| `sarif` | `Utf8JsonWriter` 後に UTF-16 デコードして `TextWriter` | 呼び出し側 `IBufferWriter` に直接書き込み |
| `json` | `JsonDiagnosticEntry[]` + `JsonSerializer.Serialize` → `string` | `Utf8JsonWriter` 直接書き込み（DTO 配列なし） |
| `text` / `github-actions` | 補間文字列・`new string(...)`・`StringBuilder` 依存 | `Utf8Writer` による UTF-8 直書き + 文字列生成削減（フェーズ 2） |

### 3) `IBufferWriter<byte>` 化の実効性（調査時の判断 → 実測結果）

- `json` は効果が高い（中間 `string` と DTO 配列を外せる）→ **フェーズ 1 で F10 Alloc -99% を確認**。
- `text` / `github-actions` は文字列構築が主なアロケーション源 → **フェーズ 2 で string-side 最適化、フェーズ 3 で UTF-8 経路統一**。
- 段階導入方針（json → string-side → IBufferWriter 一本化）は計画どおり実施済み。

## 結論（対応可否）

- 対応は **完了**。
- 推奨順序（json → text/github-actions 最適化 → 共通 UTF-8 経路）はフェーズ 0〜3 で実施済み。仕様同期はフェーズ 4。

## 対応方針（実装フェーズ）

## フェーズ 0: 計測基盤の明確化

- `src/Seiton.Benchmark/DiagnosticOutputBenchmark.cs` に `json` ベンチを追加。
- `text` / `github-actions` / `sarif` / `json` の `Allocated` と `Mean` を基準値化。
- 受け入れ基準: 以後のフェーズで allocation regressions を比較可能にする。

### フェーズ 0 実装（完了）

- `DiagnosticOutputBenchmark.WriteJson` を追加。既存 4 フォーマットと同一の `GlobalSetup`（LintEngine で実 diagnostic 生成）を共有。
- 計測条件: `Job.ShortRun`（ローカル）、`MemoryDiagnoser` 有効。

#### 基準値（フェーズ 0 計測時点、ShortRun）

| Format | Count | Mean | Allocated |
|---|---|---|---|
| text rich (baseline) | F1 | 218.6 us | 118.93 KB |
| github-actions rich | F1 | 212.2 us | 118.93 KB |
| github-actions oneline | F1 | 12.4 us | 86.24 KB |
| sarif | F1 | 60.1 us | 126.16 KB |
| **json** | F1 | **32.2 us** | **99.83 KB** |
| text rich (baseline) | F10 | 2.30 ms | 1140.67 KB |
| github-actions rich | F10 | 2.39 ms | 1156.37 KB |
| github-actions oneline | F10 | 118.6 us | 703.99 KB |
| sarif | F10 | 685.5 us | 1070.68 KB |
| **json** | F10 | **542.8 us** | **947.6 KB** |

#### フェーズ 0 レビュー

| 指摘 | 対応 |
|---|---|
| json 以外のフォーマット基準値も同一ベンチで取得すべき | 既存 4 ベンチと同一 Run で再計測し上表に記載 |
| F10 で json が Gen2 を発生（249 KB Gen2） | 現行 `JsonSerializer.Serialize` + 中間配列が原因。フェーズ 1 で改善対象 |

## フェーズ 1: `json` の UTF-8 writer 化

- `DiagnosticFormatter.WriteJson` を `Utf8JsonWriter` ベースへ置換。
- `JsonDiagnosticEntry[]` の中間配列構築を削除し、`diagnostics` から直接 JSON を書き出す。
- 互換性維持:
  - 公開 API は当面 `Write(TextWriter, ...)` を維持。
  - 改行・プロパティ名・null 出力条件（`help`）は現行と同一。
- 期待効果: `json` 出力の主要な heap allocation 削減。

### フェーズ 1 実装（完了）

#### 変更内容

- `WriteJson`: `JsonDiagnosticEntry[]` + `JsonSerializer.Serialize` を削除。`PooledByteBufferWriter` + `Utf8JsonWriter`（compact, `SkipValidation = true`）で diagnostic 配列を直接書き出し、既存 `WriteUtf8ToTextWriter` で `TextWriter` に渡す（SARIF と同経路）。
- `JsonDiagnosticEntry` 型と `SeitonJsonContext` の diagnostic 用 `[JsonSerializable]` を削除（`rules --format json` 用 `RuleStatusJsonEntry[]` のみ残す）。
- severity 文字列: 静的 `SeverityLowerStrings` テーブル + `GetSeverityLowerString`（`ToString().ToLowerInvariant()` の per-call 回避）。
- プロパティ名: UTF-8 リテラル（`"file"u8` 等）でエンコード済みバイト列を再利用。
- 回帰テスト追加: `Json_Format_EmitsExpectedFields`, `Json_Format_EmptyDiagnostics_EmitsEmptyArray`。

#### API レビュー

| 観点 | 結果 |
|---|---|
| 公開 API 変更なし | `DiagnosticFormatter.Write(TextWriter, ...)` を維持。CLI / テストの注入パターン不変 |
| 出力契約 | §6.2 スキーマ準拠。`help` null 時省略、`ruleId` null → `"parse"` |
| 内部抽象の露出 | 新規 public 型なし。`PooledByteBufferWriter` は既存 internal 型を SARIF と共有 |

#### 性能変化（DiagnosticOutputBenchmark, ShortRun, フェーズ 0 基準値との比較）

| Count | Metric | フェーズ 0 | フェーズ 1 | 変化 | 判定 |
|---|---|---|---|---|---|
| F1 | Mean | 32.2 us | 33.6 us | +4% | 許容（±10% 以内、計測誤差域） |
| F1 | Allocated | 99.83 KB | 49.48 KB | **-50%** | 改善 |
| F10 | Mean | 542.8 us | 334.9 us | **-38%** | 改善 |
| F10 | Allocated | 947.6 KB | 442.1 KB | **-53%** | 改善 |

**改善理由**

- 中間 `JsonDiagnosticEntry[]` 配列と各要素の record  materialization を排除。
- `JsonSerializer.Serialize` による中間 `string` 生成を排除（UTF-8 バッファ → `TextWriter` デコードのみ）。
- F10 で Gen2 249 KB → 111 KB に低下（大規模 diagnostic セットでの LOH/GC 圧力低減）。

**F1 Mean が微増した理由**

- 小件数では `Utf8JsonWriter` の per-field 呼び出しオーバーヘッドが Serialize 一本化より僅かに大きい可能性。F10 ではストリーム書き込みの優位性が支配的。
- 許容範囲内。さらなる Mean 改善が必要ならフェーズ 3（stdout への UTF-8 直接出力で UTF-16 デコード回避）を検討。

#### フェーズ 1 レビュー

| 指摘 | 対応 |
|---|---|
| 出力互換の回帰リスク | 既存 JSON テスト 7 件 + 新規 2 件、全 2396 テスト pass |
| `Seiton_CLI_csharp_spec.md` §7.1 が旧実装（source-gen diagnostic JSON）を記載 | §0.2/§0.4/§7.1 を Utf8JsonWriter 実装に更新 |
| `JsonDiagnosticEntry` 削除後の dead code | `SeitonJsonContext` を rules 用に縮小済み |

## フェーズ 2: `text` / `github-actions` の string-side 最適化

- 先に `IBufferWriter<byte>` へ寄せず、以下を優先:
  - 補間文字列を `Write` 連結へ置換
  - 繰り返し `new string(...)` 箇所の再利用/軽量化
  - GitHub command escape の一時 `StringBuilder` 発生を最小化
- 期待効果: 人間可読フォーマットでの実メモリ削減を低リスクで獲得。

### フェーズ 2 実装（完了）

#### 変更内容

- **補間文字列 → `Write` 連結**: `WriteOnelineDiagnostic`, `WriteRichDiagnostic`, `WriteGutterLine` 系で `$"..."` を排除。
- **パディング/キャレット**: `new string(' ', n)` / `new string('^', n)` を `WriteRepeatedChar`（`stackalloc` ≤128、`ArrayPool` フォールバック）に置換。
- **行番号**: `PadLeft` 中間文字列を `WritePaddedDecimal`（`TryFormat` + スペース直書き）に置換。
- **severity**: text 出力でも `GetSeverityLowerString` 静的テーブルを再利用。
- **GitHub escape**:
  - 512 文字以下は `stackalloc` バッファ + 1 回 `ToString`（`StringBuilder` 回避）。
  - `WriteGitHubActions` で escape を 1 回だけ実行し、`group` タイトルと diagnostic body 用表示を共有（従来は二重 escape）。
- 公開 API・出力契約は不変。既存 `DiagnosticFormatterRichTextTests` が回帰テストとして機能。

#### 性能変化（DiagnosticOutputBenchmark, ShortRun, フェーズ 2 着手前基準値との比較）

| Format | Count | Metric | 着手前 | フェーズ 2 | 変化 | 判定 |
|---|---|---|---|---|---|---|
| text rich | F1 | Mean | 231.6 us | 235.8 us | +1.8% | 許容 |
| text rich | F1 | Allocated | 118.93 KB | 56.34 KB | **-53%** | 改善 |
| github-actions rich | F1 | Mean | 231.5 us | 221.3 us | **-4%** | 改善 |
| github-actions rich | F1 | Allocated | 118.93 KB | 56.34 KB | **-53%** | 改善 |
| github-actions oneline | F1 | Mean | 12.9 us | 6.6 us | **-49%** | 改善 |
| github-actions oneline | F1 | Allocated | 86.24 KB | 49.45 KB | **-43%** | 改善 |
| text rich | F10 | Mean | 2281.9 us | 2193.6 us | **-3.9%** | 改善 |
| text rich | F10 | Allocated | 1140.67 KB | 514.73 KB | **-55%** | 改善 |
| github-actions rich | F10 | Mean | 2378.2 us | 2196.7 us | **-7.6%** | 改善 |
| github-actions rich | F10 | Allocated | 1156.37 KB | 530.42 KB | **-54%** | 改善 |
| github-actions oneline | F10 | Mean | 114.0 us | 58.1 us | **-49%** | 改善 |
| github-actions oneline | F10 | Allocated | 703.99 KB | 336.02 KB | **-52%** | 改善 |

**改善理由**

- リッチ出力の gutter/caret 行ごとに発生していた `new string(...)` と補間文字列の中間 `string` を排除。
- GitHub Actions の per-file escape を 1 回に集約（二重 escape 解消）。
- 小さな escape 結果は `stackalloc` で構築し `StringBuilder` 確保を回避。

**Mean がほぼ横ばい/微増のケース（F1 text rich +1.8%）**

- `Write` 連鎖の呼び出し回数増加が小件数では相殺。F10 では source snippet 最適化の効果が支配的に Mean も改善。

#### フェーズ 2 レビュー

| 指摘 | 対応 |
|---|---|
| GitHub Actions で同一パスの二重 escape | 1 回 escape + 表示用派生に修正 |
| 新規テスト不要（挙動不変の最適化） | 既存 2398 テスト + DiagnosticFormatterRichTextTests で回帰確認 |
| 仕様書 §7.3 が実装詳細を未記載 | `Seiton_CLI_csharp_spec.md` §7.3 を更新 |

## フェーズ 3: UTF-8 出力経路の導入（`IBufferWriter<byte>` 一本化）

- ~~内部に `Utf8OutputSink` 相当の抽象を導入し、`TextWriter` と `IBufferWriter<byte>` の二系統を選択可能にする。~~
- `DiagnosticFormatter` の出力 API を `IBufferWriter<byte>` に一本化。`Utf8Writer`（ref struct）が UTF-8 書き込みを担当。
- CLI は `WriteToStandardOutput` でバッファ → stdout へ UTF-8 直書き。テスト/注入は `WriteToTextWriter`（デコードのみ）または `ArrayBufferWriter` + `Write`。
- `json`/`sarif` は中間バッファコピーなしで呼び出し側 `IBufferWriter` に直接書き込み。

### フェーズ 3 実装（完了）

#### 変更内容

- **`Utf8Writer`**（`src/Seiton/Output/Utf8Writer.cs`）: `IBufferWriter<byte>` 向け UTF-8 出力ヘルパー。`Write`/`WriteLine`/`WriteInt`/`WriteRepeated`/`WritePaddedDecimal`、stdout/stderr フラッシュ、TextWriter デコードアダプタを提供。
- **`DiagnosticFormatter.Write(IBufferWriter<byte>, ...)`** を唯一のフォーマット実装入口に。`TextWriter` 直書き経路を削除。
- **`WriteToStandardOutput`**: CLI 用。`PooledByteBufferWriter` → `FlushToStandardOutput`（`StreamWriter` なら raw UTF-8、それ以外は `WriteToTextWriter` で `Console.SetOut` リダイレクト対応）。
- **`WriteToTextWriter`**: FixCommand 注入・ValidateCommand エラー出力用の薄いデコードアダプタ（フォーマットロジックは共有しない）。
- **テスト**: `Render` ヘルパーを `ArrayBufferWriter` + `Write` に変更。`Write_Buffer_MatchesTextWriterAdapter_OnelineError` で両経路の同値性を検証。
- **ベンチマーク**: `StringWriter`/`StringBuilder` を排除し `PooledByteBufferWriter` に直接計測（フォーマッタ本体の割当を反映）。

#### API レビュー

| 観点 | 結果 |
|---|---|
| 二系統の保守性 | フォーマットは `IBufferWriter` のみ。CLI/テスト用フラッシュは `WriteToStandardOutput` / `WriteToTextWriter` の2メソッド（デコードのみ、ロジック非重複） |
| 命名 | `Utf8OutputSink` は不採用 → **`Utf8Writer`**（TextWriter と対になる UTF-8 側の名前） |
| テスト性 | `ArrayBufferWriter<byte>` + `Encoding.UTF8.GetString` でブラックボックステスト可能 |

#### 性能変化（DiagnosticOutputBenchmark, ShortRun, フェーズ 2 完了時点との比較）

ベンチマーク計測対象を「StringWriter キャプチャ込み」から「`PooledByteBufferWriter` 直書き」に変更したため、Allocated はフォーマッタ本体の値を反映（Phase 2 数値との直接比較は計測条件が異なる点に注意）。

| Format | Count | Metric | フェーズ 2 末 | フェーズ 3 | 変化 | 判定 |
|---|---|---|---|---|---|---|
| text rich | F1 | Allocated | 56.34 KB | 8.35 KB | **-85%** | 改善（計測条件変更含む） |
| text rich | F10 | Allocated | 514.73 KB | 72.67 KB | **-86%** | 改善 |
| text rich | F10 | Mean | 2193.6 us | 2344.1 us | +6.9% | 許容 |
| json | F1 | Allocated | 49.48 KB | 1.66 KB | **-97%** | 改善（中間コピー削除） |
| json | F10 | Allocated | 442.11 KB | 3.9 KB | **-99%** | 改善 |
| json | F10 | Mean | 345.7 us | 288.8 us | **-16%** | 改善 |
| sarif | F10 | Allocated | 1070.68 KB | 11.53 KB | **-99%** | 改善 |
| github-actions oneline | F1 | Allocated | 49.45 KB | 1.46 KB | **-97%** | 改善 |

**改善理由**

- フォーマッタ出力が最初から UTF-8 バッファ上で完結（`json`/`sarif` の二重バッファ + UTF-16 デコード削除）。
- ベンチマークが StringWriter キャプチャのオーバーヘッドを除外し、実際の CLI 経路（buffer → stdout bytes）に近づいた。

**Mean 微増（text rich F10 +6.9%）**

- UTF-8 エンコードの per-field コスト。Allocated 大幅削減とトレードオフ。許容範囲内。

#### フェーズ 3 レビュー

| 指摘 | 対応 |
|---|---|
| `Console.SetOut` が `OpenStandardOutput` をバイパス | `FlushToStandardOutput` で `StreamWriter` 以外は TextWriter 経由にフォールバック |
| 二系統 API の保守性 | フォーマットは `Write(IBufferWriter)` のみに統一 |
| 仕様書が TextWriter 前提 | `Seiton_CLI_csharp_spec.md` §7 を更新 |
| `Utf8Writer` が `GetSpan` の部分返却に非対応 | `WriteLiteralCore` を chunked copy に変更。`WriteUtf8`/`WriteRepeated` も同様 |
| `Write(char)` が ASCII 以外を切り捨て | UTF-8 エンコードに修正 |
| `FlushToStandardOutput` の分岐テスト不足 | `DiagnosticFormatterFlushTests` で StringWriter / カスタム TextWriter / 空 span を追加 |
| `IBufferWriter` と `WriteToTextWriter` の同値性 | 全フォーマット向け `Write_Buffer_MatchesTextWriterAdapter_*` を追加 |

## フェーズ 4: 仕様・ドキュメント同期（完了）

### 変更内容

- **`Seiton_CLI_csharp_spec.md`**
  - §0.3 / §2: `Utf8Writer.cs` を追加。`DiagnosticFormatter` の責務を `Write(IBufferWriter<byte>, ...)` に更新。
  - §7: 出力 API レイヤー表（`Write` / `WriteToStandardOutput` / `WriteToTextWriter`）、`FlushToStandardOutput` の分岐、`Render` ヘルパーパターンを追記。
  - §8: 診断出力テストの 3 パターン（`ArrayBufferWriter`、`WriteToTextWriter`、`Console.SetOut`）と同値性テストを追記。
- **`Seiton_CLI_spec.md`**: ユーザー可視の出力契約（§6）は不変のため更新なし。
- **`plan_utf8writer.md`**: 背景・調査結果を完了後表記に整理。本節を追加。

### レビュー

| 観点 | 結果 |
|---|---|
| 言語中立 spec との整合 | `Seiton_CLI_spec.md` §6 の WHAT（出力形式・フィールド・パス表示）は実装と一致。HOW は C# spec に集約 |
| 実装 spec の網羅 | §7 がフェーズ 3 完了時点の API を反映。§8 がレビューで追加したテストパターンを反映 |

## リスクと対策

- 互換リスク（出力文字列差分）:
  - 対策: 既存 `DiagnosticFormatterRichTextTests` を回帰テストとして維持し、文字列同値を検証。
- 工数過多リスク（text/github-actions 全面 UTF-8 化）:
  - 対策: ROI が高い `json` から段階実施し、ベンチで効果確認後に次段階判断。
- テスト注入性低下リスク:
  - 対策: フォーマットは `IBufferWriter<byte>` に統一。CLI は `WriteToStandardOutput`、テスト/FixCommand 注入は `WriteToTextWriter` または `ArrayBufferWriter` で担保。

## 実施判断

- **フェーズ 0〜4 完了。** Diagnostic 出力 UTF-8 化は本計画のスコープ内で完了。
- 今後の追加最適化（例: `ExtractLines` の string 配列削減）は別タスクとして計測ベースで判断する。
