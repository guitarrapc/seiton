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
- `sarif` で `uriBaseId` / `originalUriBaseIds` を常時出力するか（推奨は常時）

---

以上より、**相対パス化は妥当かつ実装可能**。  
本計画では **案C（デフォルト相対・オプションなし・breaking change許容）を採用**し、仕様・実装・ドキュメントを一括で更新する。
