# seiton 出力パス相対化の検討メモ

この文書は、`seiton` の診断出力に含まれるファイルパス（`sarif` / `github-actions` / `text` / `json`）を絶対パスから相対パスへ変更する提案の妥当性と実装可能性を整理した計画メモである。

## 結論（判断）

- **相対パス化の方向性は妥当**。Issue 共有・ログ可読性・情報露出低減の観点でメリットが大きい。
- **実装は可能**。現状は入力解決時に絶対化しているため、出力直前で表示パスを相対化するレイヤを追加すれば対応できる。
- ただし、`sarif` は連携先（GitHub Code Scanning 等）のパス解決互換性を壊さないよう、**`uriBaseId`/`originalUriBaseIds` を併用した設計**で進めるのが安全。

## 背景と問題意識

現行の `seiton.sarif` では `runs[].results[].locations[].physicalLocation.artifactLocation.uri` が `file:///C:/...` の絶対 URI として出力される。  
また `--format github-actions` / `--format json` / `--format text --oneline` でも、入力がファイルパスの場合は絶対パスがそのまま表示される。

絶対パスには以下の課題がある。

- ローカルユーザー名・ディレクトリ構成など環境依存情報が露出する
- Issue/PR コメントで共有した際にノイズが大きい
- 実行環境が異なると再利用しにくい（再現者の手元で対応づけづらい）

## 実装調査結果（コード）

### 1) 絶対パス化される起点

- `src/Seiton/Commands/InputDiscovery.cs`
  - `ExpandFileArgs` で明示引数を `Path.GetFullPath(arg)` に変換
  - `CollectYamlFiles` でも収集ファイルを `Path.GetFullPath(file)` に変換
- これにより、`LintEngine` に渡る `filePath` はほぼ常に絶対パスになる

### 2) 出力フォーマッタ側の現状

- `src/Seiton/Output/DiagnosticFormatter.cs`
  - `text` / `github-actions` / `json` は `Diagnostic.FilePath` をそのまま表示
  - `sarif` は `ToSarifArtifactUri(filePath)` で絶対パスを `file:///...` に変換

### 3) 実測確認（ローカル実行）

`tests/Seiton.Core.Tests/fixtures/schema/actionlint/testdata/examples/.github/workflows/test.yaml` を対象に確認。

- `--format json`: `"file":"D:\\github\\...\\test.yaml"`（絶対パス）
- `--format github-actions --oneline`: `::group::D:\github\...\test.yaml`（絶対パス）
- `--format sarif`: `"uri":"file:///D:/github/.../test.yaml"`（絶対 URI）

## 仕様・ドキュメント調査結果

### 1) 仕様（source of truth）

- `.github/docs/Seiton_CLI_spec.md` §6.3 に明記:
  - 絶対 filesystem path は `file:///...` で出力
  - 相対パスは relative URI reference で出力
- 現在の実装はこの仕様に整合している。

### 2) C# 実装仕様

- `.github/docs/Seiton_CLI_csharp_spec.md` §7.2（SARIF 出力の実装説明）
- 出力実装変更時はここも同期更新が必要。

### 3) ユーザー向け docs

- `docs/usage.md` は SARIF の生成手順中心で、絶対/相対の明示契約は薄い
- ただし挙動変更時は FAQ/注記追加が望ましい（共有しやすさ、環境情報保護の観点）

## 互換性・リスク評価

### メリット

- Issue/PR 共有時の可読性向上
- ローカル環境情報露出の低減
- CI ログとローカルログの見え方が揃いやすい（repo 基準のパス）

### リスク

- `sarif` 連携先が絶対 URI 前提で解決している場合のマッピング不整合
- 既存ユーザーが absolute path を解析キーとして利用している可能性
- repo 外ファイル（`../` や別ドライブ）をどう表示するかの仕様決めが必要

## 対応案

## 案A: デフォルト相対 + 明示的切替オプション

新規 CLI オプション（例）:

- `--path-style auto|relative|absolute`
  - `auto`（デフォルト）: 可能なら基準ディレクトリから相対化。不可なら絶対
  - `relative`: 相対化必須（不可時は `../` を許容、またはエラー方針を定義）
  - `absolute`: 現行互換

設計ポイント:

- 診断内部（`Diagnostic.FilePath`）は現行どおり絶対を保持してよい
- 出力直前に `PathDisplayResolver`（新規）で表示値/URI 値を作る
- 基準ディレクトリは原則 `Environment.CurrentDirectory`（必要なら将来 `--path-base` 追加）

SARIF:

- 相対化時は `artifactLocation.uri` を相対 URI へ
- `artifactLocation.uriBaseId` と `runs[].originalUriBaseIds` も出力して解決互換性を補強

この案の長所:

- 共有性改善をデフォルトで得つつ、既存運用は `--path-style absolute` で維持可能
- 段階的移行がしやすい

## 案B: 既存デフォルト維持 + opt-in 相対化

- デフォルトは現行（absolute）
- `--path-style relative` を追加して明示利用時のみ相対化

長所:

- 破壊的変更を避けられる

短所:

- ユーザーが求める「共有しやすさ」をデフォルトで改善できない

## 案C: デフォルト相対 + オプションなし（破壊的変更許容）

- すべての出力形式（`text` / `github-actions` / `json` / `sarif`）で、既定のファイルパス表現を相対化する
- `--path-style` のような切替オプションは追加しない
- 既存の absolute path 前提利用は breaking change として受け入れる

長所:

- UX が単純（常に「共有しやすい表示」）
- 仕様と実装の分岐が増えず、保守しやすい
- ユーザー意図（Issue 共有性向上、情報露出低減）を最短で満たせる

短所:

- absolute path を期待する既存連携（スクリプト/ログ解析）への互換性影響がある
- リリースノートとマイグレーション案内が必須

## 採用方針

- **案Cを採用**する。
- 出力パスはデフォルト相対へ統一し、切替オプションは導入しない。
- 互換性影響は breaking change として明示し、リリースノートで移行案を案内する。

## 実装案（案C）

1. `Output` 層に `PathDisplayResolver`（新規）を追加し、`Environment.CurrentDirectory` 基準で表示用パスを相対化する
2. `DiagnosticFormatter.Write(...)` で `Diagnostic.FilePath` の直接表示をやめ、resolver 経由の表示値を使用する
3. `text` / `github-actions` / `json` のパス表現を同一ルールで相対化する
4. `sarif` は `artifactLocation.uri` を相対 URI で出力し、`uriBaseId` と `runs[].originalUriBaseIds` を付与して解決互換性を担保する
5. 相対化不能ケース（例: 無効パス、`<unknown>`、URI入力）は既存フォールバックを維持する（`file:///unknown` 等）
6. CLI インターフェースには変更を入れない（新規オプション追加なし）
7. `.github/docs/Seiton_CLI_spec.md` / `.github/docs/Seiton_CLI_csharp_spec.md` / `docs/usage.md` を「相対が既定」の契約へ更新する
8. 変更告知として、リリースノートに breaking change（絶対パス前提連携への影響）と移行ガイドを記載する

## テスト観点

- 既存テスト更新:
  - `tests/Seiton.Tests/DiagnosticFormatterRichTextTests.cs`
    - `Sarif_Format_WindowsAbsolutePath_EmitsFileUri` など absolute 前提ケースを relative 前提へ改訂
- 追加テスト:
  - `text/json/github-actions` で relative 出力されること
  - repo 外パスが `../` を含む相対、または既定フォールバックで安定すること
  - Windows ドライブ跨ぎ時の挙動
  - `sarif` の `uriBaseId` と `originalUriBaseIds` の整合

## 影響ドキュメント一覧

仕様変更を実施する場合、最低限以下を同一変更で更新する。

- `.github/docs/Seiton_CLI_spec.md`（出力契約）
- `.github/docs/Seiton_CLI_csharp_spec.md`（C# 実装仕様）
- `docs/usage.md`（ユーザー向け挙動説明）

必要に応じて `README.md` の出力例も同期する。

## 未決定事項（要合意）

- 相対化基準を `cwd` 固定にするか（本案では固定前提）
- Windows でドライブ跨ぎ時に `absolute` フォールバックを許可するか
- `sarif` で `uriBaseId` / `originalUriBaseIds` を相対URIがある場合のみ出力するか（実装は相対URIありの場合のみ）

---

以上より、**相対パス化は妥当かつ実装可能**。  
本計画では **案C（デフォルト相対・オプションなし・breaking change許容）を採用**し、仕様・実装・ドキュメントを一括で更新する。

---

## 実装結果（案C）

### 実装内容

| 変更 | 内容 |
|---|---|
| `src/Seiton/Output/PathDisplayResolver.cs` | 新規。`Environment.CurrentDirectory` 基準で表示パスを相対化。SARIF 用に `uriBaseId` / `originalUriBaseIds` を生成。パスごとのキャッシュで同一ファイルの反復 lookup を抑制 |
| `src/Seiton/Output/DiagnosticFormatter.cs` | `text` / `json` / `github-actions` / `sarif` すべて resolver 経由で表示。内部 `FilePath`（絶対）は source map lookup 用キーとして維持 |
| `tests/Seiton.Tests/PathDisplayResolverTests.cs` | 相対化・SARIF base ID・キャッシュの単体テスト |
| `tests/Seiton.Tests/DiagnosticFormatterRichTextTests.cs` | SARIF / JSON / text の相対パス出力テストを追加・更新 |
| `src/Seiton.Benchmark/DiagnosticOutputBenchmark.cs` | 実運用に合わせ診断 `FilePath` を絶対パスに変更 |
| 仕様・ドキュメント | `Seiton_CLI_spec.md` §6.1–6.3、`Seiton_CLI_csharp_spec.md` §7.2、`docs/usage.md` を更新 |

### API / UX レビュー

- **ユーザーファースト**: CLI に新オプションなし。出力は常に repo 相対パス（`/.github/workflows/...`）で、Issue 共有にそのまま使える。
- **内部整合**: lint エンジンは引き続き絶対パスで動作。相対化は出力層のみ。
- **SARIF 互換**: 相対 artifact がある場合は `%WORKING_DIR%` + `originalUriBaseIds` を付与し、Code Scanning 等が絶対 URI を復元可能。
- **フォールバック**: ドライブ跨ぎなど相対化不能時は従来どおり絶対 URI / 絶対パスを出力（壊れない）。
- **スコープ外（意図的）**: `--verbose` の per-file 進捗行は絶対パスのまま（デバッグ用途）。summary テーブルは従来どおりファイル名のみ。

### セルフレビューと対応

| 指摘 | 対応 |
|---|---|
| Windows テストで `Environment.CurrentDirectory` 変更がディレクトリ削除と競合 | `DiagnosticFormatter.Write` に `pathBaseDirectory` テスト用パラメータを追加（CLI 非公開） |
| SARIF 出力の同一ファイル反復解決コスト | `_displayCache` / `_sarifCache` でパス単位キャッシュ |
| `DiagnosticFormatter.Write` の public API がテスト用途引数で肥大化 | public 署名は既存維持し、`pathBaseDirectory` は internal overload に分離 |
| `originalUriBaseIds` が不要ケースでも常時生成される | 相対 artifact が1件以上ある場合のみ生成するよう変更 |
| 診断ループ内で同一ファイルの表示解決を毎回実行していた | `DiagnosticFormatter` 側でも直前ファイルキャッシュ（text/json/sarif/github-actions）を追加し resolver 呼び出しを削減 |
| `\\` を含まないパスでも `Replace('\\','/')` で文字列を再生成していた | `IndexOf('\\')` で分岐し、変換不要なら同一参照を返す |
| 仕様書が旧「絶対 URI 既定」と矛盾 | `Seiton_CLI_spec.md` 等を案C契約に同期 |

### ベンチマーク（DiagnosticOutputBenchmark）

ベンチマークは実装後に診断 `FilePath` を**絶対パス**に変更したため、SARIF/oneline の Before は「もともと相対パス入力」、After は「本番同等の絶対パス入力 + 相対化処理」を計測している。厳密な Before/After 比較は text/github-actions rich（処理構造が近い）と Allocated を主に参照する。

| Benchmark | Count | Before Mean | After Mean | Δ Mean | Before Alloc | After Alloc | Δ Alloc |
|---|---:|---:|---:|---:|---:|---:|---:|
| text rich | F1 | 216.7 µs | 239.3 µs | +10% | 117.5 KB | 118.9 KB | +1% |
| text rich | F10 | 2,130 µs | 2,281 µs | +7% | 1,137 KB | 1,141 KB | +0.4% |
| github-actions rich | F1 | 206.0 µs | 229.9 µs | +12% | 117.5 KB | 118.9 KB | +1% |
| github-actions rich | F10 | 2,079 µs | 2,421 µs | +16% | 1,153 KB | 1,156 KB | +0.3% |
| github-actions oneline | F1 | 9.8 µs | 13.2 µs | +34%* | 84.8 KB | 86.2 KB | +2% |
| github-actions oneline | F10 | 93.5 µs | 128.8 µs | +38%* | 700 KB | 704 KB | +0.6% |
| sarif | F1 | 35.2 µs | 44.1 µs | +25%* | 145 KB | 153 KB | +6% |
| sarif | F10 | 414 µs | 483 µs | +17%* | 1,273 KB | 1,329 KB | +4% |

\* Before 側はベンチマーク入力が相対パスだったため、相対化コストが含まれていない。実運用上は「絶対パス入力 → 相対パス出力」が新規コストであり、Allocated 増分はおおむね +10% 以内。

**性能評価**:

- **Allocated**: 全ケース +10% 以内。相対化・SARIF メタデータ追加に対して許容範囲。
- **Mean**: rich text 形式は +7〜16%（ノイズと絶対パス入力化の影響が混在）。oneline/SARIF の Mean 増は主にベンチマーク入力変更によるもの。キャッシュ導入後 SARIF F10 は 827 µs → 483 µs（直前計測では 470 µs）に改善し、非キャッシュ版より大幅に低い。
- **改善策（将来）**: ベンチマークを「絶対パス入力・相対パス出力」で Before も取り直す。必要なら `Write()` 内で `PathDisplayResolver` を 1 回だけ生成して各 writer に渡す（現状は format ごとに 1 回）。

### Breaking change 告知（リリースノート用）

- `--format text|json|github-actions|sarif` の診断出力に含まれるファイルパスは、プロセス作業ディレクトリからの**相対パス**（`/` 区切り）に変更。
- 絶対パス前提のログパーサーは相対パス対応が必要。
- SARIF は相対 artifact を含む場合に `uriBaseId` / `originalUriBaseIds` が追加。`file:///C:/...` 形式のみを期待する連携は `%WORKING_DIR%` 解決へ移行。

---

## 追加パフォーマンス見直し（2026-06-03）

### ブロッキングポイント評価（戻り値/シリアライズ契約変更）

- **大きなブロッキングはなし**。`PathDisplayResolver` は `Output` 層内で閉じており、外部 API への波及は限定的。
- ただし、`TextWriter` ベース出力という前提があるため、完全ゼロアロケーション（中間文字列ゼロ）には構造的制約がある。
- SARIF の完全ストリーム化（`Utf8JsonWriter` 直書き）も試行可能だが、実測では現行ワークロードで Allocated 改善が得られないケースがあり、採用判断はベンチ優先で行う。

### 実装計画（追加）

1. `PathDisplayResolver` の不要再生成を削減（`Replace` 回避、既存キャッシュ再利用）
2. `DiagnosticFormatter` 側で連続同一 `FilePath` の局所キャッシュを追加し、resolver 呼び出し回数を削減
3. SARIF のストリーム直書きを試験実装し、ベンチ比較で優位性が出るか検証
4. 退行が出る場合は即時ロールバックし、最小アロケーションの安定版を採用

### 実装結果（追加）

- 採用:
  - `PathDisplayResolver.NormalizeToForwardSlashes` は `\\` 非含有時に同一参照を返す
  - `DiagnosticFormatter` で format ごとの直前ファイルキャッシュを導入
  - `originalUriBaseIds` は相対 artifact がある場合のみ生成
- 採用（追加）:
  - SARIF は `IBufferWriter<byte>` + `Utf8JsonWriter` で直列化する経路へ変更
  - `ArrayBufferWriter<byte>` ではなく `ArrayPool<byte>` を使う `PooledByteBufferWriter` を導入し、バッファ再利用を明示化
  - UTF-8 -> `TextWriter` 変換は `stackalloc` / `ArrayPool<char>` で実施し、大きな一時 `string` 生成を回避

### 追加ベンチ（直近2回比較）

比較対象:
- 変更前: `agent-tools/1edee36e-b0f3-4d45-9972-407f64d263ae.txt`
- 変更後: `agent-tools/8104ec26-7e5d-42a7-9445-8e8a0e2de130.txt`
- `IBufferWriter<byte>`/`Utf8JsonWriter` 導入後: `agent-tools/6fd67ca5-de1d-452e-8a04-bc016a363656.txt`

主要差分:
- `github-actions oneline` F10 Mean: `114.03 us -> 109.42 us`（改善）
- `sarif` F10 Mean: `467.87 us -> 448.76 us`（改善）
- `sarif` Allocated: `1329.38 KB -> 1329.42 KB`（誤差範囲で同等）

`IBufferWriter<byte>`/`Utf8JsonWriter` 導入後（`8104...` 比）:
- `sarif` F1 Mean: `41.08 us -> 48.58 us`（+18%）
- `sarif` F1 Allocated: `153.05 KB -> 79.92 KB`（-48%）
- `sarif` F10 Mean: `448.76 us -> 507.77 us`（+13%）
- `sarif` F10 Allocated: `1329.42 KB -> 617.65 KB`（-54%）

結論:
- ユーザー要求どおり `IBufferWriter<byte>`/`Utf8JsonWriter` は導入済み。
- 現状は「**CPU（Mean）を一部犠牲にして、SARIF の割り当て量を大幅削減**」というトレードオフになっている。
- `TextWriter` 契約のままでも allocation は大きく圧縮できたが、さらなる Mean 改善には UTF-8 バイトを直接下流へ流せる出力契約（`Stream`/`IBufferWriter<byte>` 直結）の併設が有効。
