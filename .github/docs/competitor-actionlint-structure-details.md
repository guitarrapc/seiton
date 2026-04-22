# actionlint における GitHub Actions スキーマ解析・検証の構造

## 1. 全体像（結論）
actionlintは、GitHub Actionsの「スキーマ」を単一のJSON Schemaで読む設計ではなく、以下の2層で検証しています。

1. **構文スキーマ（workflow YAML の形）**
   `parse.go`がYAMLノードを直接走査し、許可キー・必須キー・値型・排他条件などを実装コードとして検証しながらAST (`Workflow`, `Job`, `Step`など) を組み立てる。
2. **意味スキーマ（文脈依存ルール）**
   `linter.go` + `pass.go` + 各`rule_*.go`がASTをVisitorで巡回し、イベント仕様・式型・コンテキスト可用性・アクション入力/出力整合性などを検証する。

つまり、actionlintのスキーマは「コード化されたパーサ規則 + ルール + 生成データ（可用性表・Webhook型一覧・popular actions）」として分散管理されています。

---

## 2. 解析（Parse）フェーズの仕組み

### 2.1 エントリポイント
- `Parse(b []byte)` (`parse.go`) が`go.yaml.in/yaml/v4`でYAMLを読み込み、`p.parse(&n)`を呼ぶ。
- YAMLパース時の型/構文エラーは`handleYAMLUnmarshalError`でactionlintのエラー形式に変換。

### 2.2 トップレベルスキーマ検証
`p.parse()`ではworkflow直下キーを`switch`で明示的に制限。
- 許可: `name`, `run-name`, `on`, `permissions`, `env`, `defaults`, `concurrency`, `jobs`
- 未知キーは`unexpectedKey(...)`で`syntax-check`エラー化。
- `on` / `jobs`が無ければ必須エラー。

### 2.3 セクションごとの細粒度検証
`parse.go`内の各`parse*`関数がセクション単位のスキーマを実装。
例:
- `parseEvents`: `on:`がscalar / mapping / sequenceかを分岐しイベント種別ごとに解析
- `parseJob`: `runs-on`, `steps`, `uses`などの相互制約（再利用workflow呼び出し時の許可キー制限含む）
- `parseStrategy`, `parseMatrix`, `parseContainer`, `parseRunsOn`など値型・必須キー・範囲チェック

### 2.4 エラー収集戦略
- 1エラーで停止せず、可能な限り解析継続して複数エラーを返す設計。
- これによりIDE/CIで一度に多くの問題を提示できる。

---

## 3. 検証（Rule）フェーズの仕組み

### 3.1 実行フロー
- `linter.go`の`check(...)`で`Parse`実行後、`rules := []Rule{...}`を構築。
- `Visitor` (`pass.go`) が`VisitWorkflowPre -> VisitJobPre -> VisitStep -> VisitJobPost -> VisitWorkflowPost`順に巡回。
- 各ルール (`rule_events.go`, `rule_expression.go`, `rule_action.go`など) が独立にエラーを追加。

### 3.2 代表ルール
- `RuleEvents`:
  - `AllWebhookTypes`を参照してイベント名・`types`妥当性を検証
  - `paths`と`paths-ignore`など排他フィルタ制約を検証
- `RuleExpression`:
  - 式の構文/型/未定義プロパティを検証
  - `WorkflowKeyAvailability`から文脈ごとの利用可能context / special functionを取得して制約適用
- `RuleAction`:
  - `uses:`形式、Docker URL、ローカルaction解決、popular actionsデータセットとの照合

---

## 4. 「スキーマ変更」をどう検知・反映しているか

このリポジトリでは、変更源ごとに手段が分かれます。

## 4.1 自動生成対象（半自動追従）
`//go:generate`は次の3つ。

1. `action_metadata.go` -> `popular_actions.go`生成
   (`scripts/generate-popular-actions`)
2. `rule_events.go` -> `all_webhooks.go`生成
   (`scripts/generate-webhook-events`)
3. `rule_expression.go` -> `availability.go`生成
   (`scripts/generate-availability`)

### a) Context availability（`availability.go`）
- 生成元: GitHub Docsのcontexts Markdown
- `generate-availability`が表を抽出して`WorkflowKeyAvailability`, `SpecialFunctionNames`, `AllContexts`を生成
- 反映先: `RuleExpression` / `ExprSemanticsChecker`の可用性検証

### b) Webhook activity types（`all_webhooks.go`）
- 生成元: GitHub Docsのevents HTML
- `generate-webhook-events`がイベント名とactivity typeをスクレイプしてmap生成
- 反映先: `RuleEvents.checkWebhookEvent`

### c) Popular actions metadata（`popular_actions.go`）
- 生成元: `scripts/generate-popular-actions/popular_actions.json` + 各actionの`action.yml`取得結果
- `generate-popular-actions`が`PopularActions` / `OutdatedPopularActionSpecs`を再生成
- `-d`オプションで「次メジャー版が出たか」をHEADリクエストで検知
- 反映先: `RuleAction` / `RuleExpression`のinput/outputチェック

## 4.2 CI による検知・反映自動化
`.github/workflows/generate.yaml`が更新検知と反映を担う。

トリガ:
- `schedule`（週次）
- `workflow_dispatch`（手動）
- `push`（`scripts/generate-popular-actions/main.go` / `scripts/generate-webhook-events/main.go`変更時）

処理:
1. `go run ./scripts/generate-popular-actions -d`で新リリース有無を確認
2. `go generate`で3生成物を更新
3. `git diff-files --quiet`で差分検知
4. 差分があれば`peter-evans/create-pull-request`で自動PR作成

=> GitHub側ドキュメント/人気action変化があれば、定期的にPRとして反映される設計。

## 4.3 手実装スキーマの変更検知
`parse.go`のキー許可リストや相互制約は手実装のため、ここは基本的に
- issue/PRでの追従
- テスト失敗検知
- 定期メンテ
で更新される。

補助的なガード:
- `all_webhooks_test.go`, `availability_test.go`, `popular_actions_test.go`
- ルール/パーサユニットテスト群 (`*_test.go`)
- `Makefile`の`go generate`依存とCI lint/test

---

## 5. 変更反映の実務フロー（要約）

1. **外部仕様差分の検知**
   - 週次`generate` workflow
   - または`generate-popular-actions -d`
2. **生成物の更新**
   - `go generate`（`popular_actions.go`, `all_webhooks.go`, `availability.go`）
3. **差分判定**
   - Git diff
4. **反映**
   - CIが自動PR作成（差分あり時）
5. **品質担保**
   - 既存テスト/CIで回帰確認

---

## 6. 調査上の補足

- actionlintはJSON Schema駆動ではなく、Go実装で厳密に文法と意味を定義する方式。
- ただし「変化しやすい外部表データ」はスクレイピング + 生成コード化でメンテコストを下げている。
- このハイブリッドにより、静的保証（実装ルール）と追従性（自動生成）のバランスを取っている。
