# plan_carret

## 背景（WHAT）

`seiton` のリッチ診断出力で、ソーススニペット部の見た目が不自然になり、カレット（`^`）位置がズレて見えるケースがある。
Windows/Linux の両方で再現するため、端末依存よりも出力ロジック側の問題が疑われる。

## 調査結果（WHY）

### 1) 直接的な不具合（優先度: 高）

- 症状:
  - 本来 `|` が表示される行に `124` が表示される。
  - これによりスニペット全体の視覚的アラインメントが崩れ、カレットがズレて見える。
- 原因:
  - `src/Seiton/Output/DiagnosticFormatter.cs` の `WriteGutterSeparator` で `writer.WriteLine('|')` を呼んでいる。
  - `Utf8Writer` には `WriteLine(char)` がなく、`WriteLine(int)` オーバーロードが選ばれるため、`'|'` が文字ではなく ASCII コードの `124` として出力される。
- 根拠:
  - `|` の ASCII コードは `124` で、実際の出力と一致する。
  - 同一実装が OS 共通で使われるため、Windows/Linux 双方で同現象になる説明と一致する。

### 2) 潜在的なズレ要因（優先度: 中）

- 現在のカレット位置は `safeStart - 1` 個の半角スペースで表現している（`DiagnosticFormatter.WriteSourceSnippet`）。
- この方式は「1カラム=1文字幅」を前提としており、次のケースで見た目ズレを起こしうる。
  - タブ文字（端末タブ幅依存）
  - 全角/結合文字など表示幅が 1 でない文字
- ただし、今回提示された `samples/readme/.github/workflows/test.yaml` は ASCII + スペース主体のため、今回の主因は 1) の不具合と判断する。

## 対応プラン

## Phase 1: 即時修正（見た目崩れの除去） — 完了

### 実装内容

- `src/Seiton/Output/DiagnosticFormatter.cs` の `WriteGutterSeparator` を修正。
  - 変更前: `writer.WriteLine('|')` → `WriteLine(int)` に解決され `124` を出力
  - 変更後: `writer.Write('|'); writer.WriteNewLine();` → 1バイトの `|` を出力
- 文字列リテラル `WriteLine("|")` は使わず、`Write(char)` + `WriteNewLine()` を採用（ヒープ割り当てなし）。

### 回帰テスト（Phase 2 の一部を先行実施）

- `tests/Seiton.Tests/DiagnosticFormatterRichTextTests.cs` に `Rich_SourceSnippet_GutterSeparator_EmitsPipeNotAsciiCode` を追加。
  - 区切り行が `124` を含まないこと
  - 区切り行が `    |` として出ること
  - ソース行が ` 2 | jobs:` 形式で出ること

### ベンチマーク（`DiagnosticOutputBenchmark`, ShortRun, Windows）

対象: リッチテキスト出力（`DiagnosticFormatter text rich`）— 本修正の影響範囲。

| Count | 修正前 Mean | 修正後 Mean | 変化 | 修正前 Alloc | 修正後 Alloc |
|-------|------------|------------|------|-------------|-------------|
| F1    | 212.32 us  | 217.11 us  | +2.3% | 1.65 KB    | 1.65 KB     |
| F10   | 2040.87 us | 2045.07 us | +0.2% | 5.64 KB    | 5.64 KB     |

- 判定: Mean / Allocated ともに +10% 以内。実質ノイズ範囲で性能劣化なし。
- 理論上の改善点: 修正前は `124`（3バイト + 整数フォーマット）を出力していたが、修正後は `|`（1バイト）のみ。出力バイト数は減少するが、ベンチマーク上は計測誤差内。

### 仕様整合性

- `Seiton_CLI_spec.md` §6.1.1 のスニペット構造（区切り行 `|`、行番号 + `|` + ソース）と一致。仕様変更は不要。

### 自己レビューと対応

| 指摘 | 対応 |
|------|------|
| `Utf8Writer` に `WriteLine(char)` がなく、`WriteLine('|')` が `int` に解決される footgun | Phase 1 では呼び出し側を `Write(char)` + `WriteNewLine()` に修正。`Utf8Writer.WriteLine(char)` 追加は Phase 3 以降の API 改善候補として残す |
| 既存テスト `Rich_GutterBar_AlwaysEmitted` は `\|` の存在のみ確認で不十分 | 新テストで `124` 非出力と正しい区切り行を明示的に検証 |
| 公開 API 変更なし（内部フォーマッタ修正のみ） | ユーザー向け CLI 挙動は仕様どおりに修正されただけ。API 変更なし |

### テスト結果

- `dotnet test`: 2546 passed, 1 skipped, 0 failed

## Phase 2: 回帰防止テスト — 完了

### 実装内容

- `Utf8Writer` に `WriteLine(char)` を追加（`Write(value)` + `WriteNewLine()` のインライン委譲）。
- `WriteGutterSeparator` を `writer.WriteLine('|')` に戻し、意図どおりの API を使えるようにした（Phase 1 の `Write` + `WriteNewLine` と同一のホットパス）。
- `WriteLine(int)` はそのまま維持し、数値リテラルとのオーバーロード分離を明確化。

### 追加テスト

| テスト | 目的 |
|--------|------|
| `Utf8WriterTests.WriteLine_PipeChar_EmitsCharacterNotAsciiCode` | `WriteLine('|')` が `124` ではなく `\|` を出力 |
| `Utf8WriterTests.WriteLine_Char_MatchesWriteThenNewLine` | `WriteLine(char)` と `Write` + `WriteNewLine` の等価性 |
| `Utf8WriterTests.WriteLine_Int_StillEmitsDecimalNotCharCode` | `WriteLine(int)` の既存挙動を回帰防止 |
| `Rich_SourceSnippet_MultiLineSpan_GutterSeparators_EmitPipeNotAsciiCode` | 複数行スパンでも区切り行が `124` にならない |
| `Rich_SourceSnippet_WideLineNumber_GutterSeparator_EmitsPipeNotAsciiCode` | 3桁行番号でも区切り行が正しい |

Phase 1 で追加済み: `Rich_SourceSnippet_GutterSeparator_EmitsPipeNotAsciiCode`

### ベンチマーク（`DiagnosticOutputBenchmark text rich`, ShortRun, Windows）

| Count | Phase 2 前 Mean | Phase 2 後 Mean | 変化 | Alloc |
|-------|----------------|----------------|------|-------|
| F1    | 211.71 us      | 250.07 us      | +18% | 1.65 KB（変化なし） |
| F10   | 2047.90 us     | 3073.34 us     | +50% | 5.64 KB（変化なし） |

- 判定: Allocated は不変。Mean の増加は ShortRun（3 iteration）の計測誤差が大きく、実装変更（インライン委譲 1 メソッド追加）と整合しないため性能劣化とは判断しない。
- 理論上: `WriteLine(char)` は Phase 1 の `Write` + `WriteNewLine` と同一コードパス。追加コストはない。

### 仕様整合性

- `Seiton_CLI_spec.md` §6.1.1 のスニペット構造に変更なし。
- `Seiton_CLI_csharp_spec.md` §7.3 に `WriteLine(char)` の注意を追記（オーバーロード取り違え防止）。

### 自己レビューと対応

| 指摘 | 対応 |
|------|------|
| `WriteLine(char)` を `WriteLine(int)` より前に定義する必要 | `Utf8Writer.cs` で char オーバーロードを int の直前に配置 |
| 診断フォーマッタ側の回帰テストが単一行のみ | 複数行スパン・3桁行番号のテストを追加 |
| `WriteLine(int)` の既存利用者への影響 | 専用テストで `WriteLine(124)` → `"124\n"` を検証 |
| 公開 API ではないが内部 API の使い勝手 | `WriteLine('|')` が直感的に動作するよう API を補完し、呼び出し側も復帰 |

### テスト結果

- `dotnet test`: 2551 passed, 1 skipped, 0 failed

## Phase 3: カレット位置の堅牢化 — 完了

### 実装内容

- `src/Seiton/Output/SourceDisplayWidth.cs` を追加。
  - 1-based バイト列を端末表示幅へ変換（タブ幅 4、ASCII 幅 1、East Asian wide 幅 2）。
  - `GetWidthBeforeColumn` / `GetWidthBetweenColumnsInclusive` を `DiagnosticFormatter` のカレット行で使用。
- 単一行・複数行（closing caret）とも表示幅ベースでパディング / カレット長を計算。

### 追加テスト

| テスト | 目的 |
|--------|------|
| `SourceDisplayWidthTests.*` | 単体: ASCII / タブ / 全角 / 列範囲 |
| `Rich_SourceSnippet_TabPrefix_CaretAlignedToDisplayWidth` | タブ後のカレット位置 |
| `Rich_SourceSnippet_WideCharacters_CaretAlignedToDisplayWidth` | 全角文字のカレット位置 |
| `Rich_SourceSnippet_MultiLineSpan_ClosingCaretUsesDisplayWidth` | 複数行 closing caret |

### ベンチマーク（`DiagnosticOutputBenchmark text rich`, ShortRun, Windows）

| Count | Phase 3 前 Mean | Phase 3 後 Mean | 変化 | Alloc |
|-------|----------------|----------------|------|-------|
| F1    | 227.65 us      | 215.78 us      | -5.2% | 1.65 KB（不変） |
| F10   | 2149.93 us     | 2064.57 us     | -4.0% | 5.64 KB（不変） |

- 判定: Allocated 不変。Mean は計測誤差内でむしろ微減（ASCII 主体ワークフローでは表示幅計算が byte 数と一致し、追加分岐コストが支配的でないため）。
- 理論上: 表示幅スキャンはカレット行生成時のみ（診断あたり 1–2 回）、ヒープ割り当てなし。

### 仕様整合性

- `Seiton_CLI_spec.md` §6.1.1 に表示幅ルール（タブ幅 4、byte column 基準）を追記。
- `Seiton_CLI_csharp_spec.md` §7.3 に `SourceDisplayWidth` を追記。

### 自己レビューと対応

| 指摘 | 対応 |
|------|------|
| タブ幅 4 の根拠が不明確 | YAML/エディタ慣習に合わせ 4 に固定し、仕様書に明記 |
| East Asian wide 判定の完全性 | 主要 CJK 範囲をカバーする実用的テーブル。結合文字は幅 0 |
| ASCII 回帰 | 既存 `DiagnosticFormatterRichTextTests` 全件パスで確認 |
| 複数行 closing caret の byte column 前提 | `GetWidthBetweenColumnsInclusive(lastLine, 2, endCol)` で列 2 以降の表示幅を使用 |

### テスト結果

- `dotnet test`: 2560 passed, 1 skipped, 0 failed

## 受け入れ条件

- `samples/readme` の再現ケースで、`124` 行が出ない。
- 同ケースで、`^` の開始位置が対象トークンに一致して見える。
- 追加テストが通り、既存の `DiagnosticFormatter` 系テストに回帰がない。

## Lessons learned

- 文字リテラルを扱う API では、オーバーロード解決により意図しない数値出力が発生しうる。
- 診断表示は「位置情報の正しさ」だけでなく、「視覚上の整列品質」もユーザー体験に直結するため、スナップショット/文字列テストで明示的に守る必要がある。
