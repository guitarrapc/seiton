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

## 案A（推奨）: デフォルト相対 + 明示的切替オプション

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

## 案C: 出力フォーマット別に固定方針

- `text`/`github-actions` は相対、`sarif` は絶対維持 など

短所:

- 学習コストが高く、挙動が直感的でない
- 仕様説明が複雑化

## 推奨方針

- **案A** を推奨。
- 初期リリースではデフォルトを `auto` にし、移行リスクを抑える。
- repo 外や相対化不能ケースは absolute フォールバックを許容し、壊れないことを優先する。

## 実装ステップ（案A）

1. `Output` 層に `PathStyle` 概念と `PathDisplayResolver` を追加
2. `DiagnosticFormatter.Write(...)` に path 解決コンテキスト（style + base directory）を渡す
3. `text` / `github-actions` / `json` の `file` 表示を resolver 経由へ変更
4. `sarif` は relative URI + `uriBaseId` + `originalUriBaseIds` 対応
5. CLI オプション `--path-style` を追加し、`Seiton_CLI_spec.md` / `Seiton_CLI_csharp_spec.md` 更新
6. `docs/usage.md` に「Issue 共有しやすい相対パス出力」説明を追加

## テスト観点

- 既存テスト更新:
  - `tests/Seiton.Tests/DiagnosticFormatterRichTextTests.cs`
    - `Sarif_Format_WindowsAbsolutePath_EmitsFileUri` など absolute 前提ケースの改訂
- 追加テスト:
  - `text/json/github-actions` で relative 出力されること
  - repo 外パスがフォールバックされること
  - Windows ドライブ跨ぎ時の挙動
  - `sarif` の `uriBaseId` と `originalUriBaseIds` の整合

## 影響ドキュメント一覧

仕様変更を実施する場合、最低限以下を同一変更で更新する。

- `.github/docs/Seiton_CLI_spec.md`（出力契約）
- `.github/docs/Seiton_CLI_csharp_spec.md`（C# 実装仕様）
- `docs/usage.md`（ユーザー向け挙動説明）

必要に応じて `README.md` の出力例も同期する。

## 未決定事項（要合意）

- デフォルト値を `auto` にするか `absolute` 維持にするか
- 相対化基準を `cwd` 固定にするか、将来 `--path-base` を導入するか
- `sarif` で `uriBaseId` を必須にするか（推奨は付与）

---

以上より、**相対パス化は妥当かつ実装可能**。  
実運用互換性を重視するなら、`--path-style` で切替可能にした上で `auto` 運用へ寄せるのが最も安全。
