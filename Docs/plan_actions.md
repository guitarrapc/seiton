# Seiton Actions 対応実装計画

## 1. 目的

Seiton が workflow ファイルと action metadata ファイルの両方を分類し、適切に解析・lint できるようにする。

高速なパスヒント要件:

- ベース名が `action.yml` または `action.yaml` の場合は action-metadata 候補
- `.github/actions/<name>/action.yml` または `.github/actions/<name>/action.yaml` の場合は action-metadata 候補

最終的な文書種別は構造で確定する（パスヒントは候補に留める）。

## 2. スコープ

対象:

- Core の document-kind classifier
- CLI のファイル種別ルーティング挙動
- Parser/Linter エントリポイントのルーティング
- パスヒント + 構造確定のテスト

本計画の対象外:

- 外部ツールとの action metadata ルール完全互換
- デフォルトでの action ファイル再帰自動探索の拡張

## 3. 設計方針

分類ポリシー:

1. パスヒントから候補種別を作る（高速段）
2. YAML ルート構造から種別を確定する（正）
3. パスと構造が不一致なら構造を優先し、不一致診断を出す

構造ディスクリミネータ:

- ルートに `jobs` がある: `workflow`
- ルートに `runs` がある: `action-metadata`
- `jobs` と `runs` の両方がある: `unknown` + 曖昧性診断
- `jobs` と `runs` のどちらもない: 未解決（既存 parser 診断で失敗理由を提示）

種別:

- `workflow`
- `action-metadata`
- `unknown`

## 4. 実装内訳

### Phase A: Core classifier contract ✅ 完了

実施内容:

- `DocumentKind` モデルと classifier API を core に追加
- action metadata パスヒント判定を実装
- 構造確定ロジック（`jobs` / `runs`）を実装
- ルートキー衝突時（`jobs` + `runs`）の曖昧判定を実装

完了条件:

- 既知フィクスチャで classifier が決定的に同じ結果を返す
- パスだけの誤判定を構造段で補正できる
- `jobs`/`runs` 判定と曖昧ケースがテストで担保される

### Phase B: Parser/Linter エントリポイントルーティング ✅ 完了

実施内容:

- `WorkflowParser.ParseClassified` を追加し、最終 `DocumentKind` を返却
- パスヒント不一致・曖昧性の診断を追加
- Linter を最終種別で分岐し、`workflow` 以外では workflow ルールを実行しないように変更

完了条件:

- 既存 workflow の挙動が維持される
- action metadata 入力で `on` / `jobs` 欠落エラーが誤って出ない

### Phase C: CLI 挙動更新 ✅ 完了

実施内容:

- no-arg 既定探索は `.github/workflows/` 優先のまま維持
- 明示指定ファイルは classifier を通して parser/linter ルーティング

完了条件:

- `seiton`（引数なし）の互換挙動を維持
- `seiton .github/actions/foo/action.yml` を action-metadata として受理・ルーティング

### Phase D: テスト拡充 ✅ 完了

実施内容:

- 分類テスト `DocumentKindClassificationTests` を追加
- パスヒント判定、構造確定、曖昧判定、不一致判定をカバー
- Action 入力時に workflow ルールが走らないことを確認
- 既存 `ParserTests` を回して回帰がないことを確認

完了条件:

- workflow 回帰テストがグリーン
- action 分類の正/誤/競合ケースをテストで網羅

### Phase E: ドキュメント/リリース整備 ⏳ 未完了

実施予定:

- parser/linter/CLI 仕様の最終同期確認
- リリースノートへの反映
- workflow-first 既定探索維持の移行注意を明記

完了条件:

- 仕様と実装の完全一致
- CI とテストの最終通過

## 5. リスクと対策

リスク:

- パス判定だけに依存した誤分類

対策:

- 必ず構造で最終確定
- 不一致診断を出して観測可能にする

リスク:

- 既存 workflow 自動探索の破壊

対策:

- no-arg 探索は現行挙動を維持
- 既存デフォルト挙動の回帰テストを維持

## 6. 受け入れ基準

- 要求された action パスヒントが実装されている
- 最終種別がパスのみではなく構造で確定される
- classifier に構造ヒント（`jobs` => workflow、`runs` => action-metadata）が入っている
- CLI で明示 action metadata ファイルを lint できる
- workflow 既定探索が互換維持される
- 仕様更新とテスト更新が同時に行われる
