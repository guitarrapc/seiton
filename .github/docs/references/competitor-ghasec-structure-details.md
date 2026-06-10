# ghasec における GitHub Actions スキーマ解析・検証の構造

## 1. 全体像（結論）
ghasec は、同様に単一 JSON Schema を実行時に直接評価するのではなく、次の2層を組み合わせる構成です。

1. **構文スキーマ（workflow/action YAML の形）**
   `goccy/go-yaml` AST を入力に、`invalid-workflow` / `invalid-action` の required ルールが構造妥当性を担保する。
   これらのコア検証ロジックは `cmd/gen` で SchemaStore 由来スキーマから生成したコード（`generated.go`）と、手書き拡張チェックの合成。
2. **意味スキーマ（セキュリティルール）**
   `analyzer` がルール群を並列実行し、`unpinned-action`、`dangerous-checkout`、`script-injection` などのポリシー違反を診断する。

つまり ghasec のスキーマは「SchemaStore 由来の生成バリデータ + 手書き拡張 + ルールエンジン + 一部オンライン検証（GitHub API）」として構成されています。

---

## 2. 解析（Parse）フェーズの仕組み

### 2.1 エントリポイント
- `cmd/root.go` の `RunE` が対象ファイルを解決し、各ファイルを `parser.Parse(path)` で AST 化する。
- `parser/parser.go` は `yamlparser.ParseFile(path, 0)` の薄いラッパーで、YAML パースは `goccy/go-yaml` に委譲。

### 2.2 ファイル発見と分類
- 引数なし実行時は `discover.Discover(".")` が自動探索。
  - workflow: `.github/workflows/*.yml|yaml`
  - action metadata: `**/action.yml|action.yaml`
- 引数あり実行時は `classifyFile()` で workflow/action を判定して別レーンで処理。

### 2.3 構文スキーマ検証（required ルール）
- `invalid-workflow` と `invalid-action` は `Required() == true`。
- これらが失敗すると、`analyzer.runRules()` が残りの non-required ルール実行をスキップするゲートとして機能する。
- required ルール本体は
  - 生成コード（`rules/invalid-*/generated.go`）
  - 手書き拡張（排他条件、依存条件、式配置、重複IDなど）
  の2段で構成。

### 2.4 エラー収集戦略
- 1件で停止せず、ファイル単位でパースエラー・ルールエラーを集約して最終出力。
- 並列化は2段:
  - ファイル並列（`concurrency=4`）
  - ルール並列（required 以外）

---

## 3. 検証（Rule）フェーズの仕組み

### 3.1 実行フロー
- `buildRules()` が全ルールを構築し、`Online() == true` のルールは `--online` 無しではスキップ。
- `analyzer.New(...rules)` が WorkflowRule / ActionRule を分離保持。
- `AnalyzeWorkflow` / `AnalyzeAction` で top-level mapping を抽出し、`runRules` へ。

### 3.2 代表ルール群
- 構文/妥当性: `invalid-workflow`, `invalid-action`, `invalid-expression`
- ピン留め/供給網: `unpinned-action`, `unpinned-reusable-workflow`, `unpinned-container`, `unpinned-transitive-action`（online）
- 実運用安全性: `dangerous-checkout`, `checkout-persist-credentials`, `default-permissions`, `job-timeout-minutes`, `deprecated-commands`
- API依存検証: `archived-action`, `impostor-commit`, `mismatched-sha-tag`

### 3.3 ignore ディレクティブ設計
- `# ghasec-ignore` をトークン位置ベースで収集し、ルール別または全ルール抑止を適用。
- `unused-ignore` ルールで、未使用・未知ルールID・required ルールの無効化試行を検出する。

---

## 4. 「スキーマ変更」をどう検知・反映しているか

## 4.1 生成対象（SchemaStore 連動）
- `rules/invalid-workflow/doc.go`:
  - `//go:generate go run ../../cmd/gen/ -root=../.. -schema=workflow`
- `rules/invalid-action/doc.go`:
  - `//go:generate go run ../../cmd/gen/ -root=../.. -schema=action`
- `cmd/gen/main.go` が `schemastore/src/schemas/json/github-workflow.json` と `github-action.json` を読み、`generated.go` を再生成。

### a) スキーマソース
- `schemastore` は git submodule (`.gitmodules`)。
- つまり ghasec は「SchemaStore を参照実装として取り込み、Goバリデータへコンパイルして使用」する方式。

### b) 生成コード + 手書き拡張の分離
- 生成コードは構造制約の基盤。
- JSON Schema だけでは表現しづらい運用制約（相互排他、式禁止位置、cron 妥当性など）は手書き拡張で補完。

## 4.2 変更検知・自動化の実態
- CI (`.github/workflows/ci.yml`) はテスト/ビルド/lint中心で、`go generate` を定期実行して自動PR化する専用フローは確認できない。
- 一方で `renovate.json` で `git-submodules` を有効化しており、SchemaStore submodule 更新の検知は Renovate 側に委ねる設計。

## 4.3 オンライン仕様追従（実行時）
- 一部ルールは GitHub API を実行時参照（タグ解決、アーカイブ判定、コミット到達性検証、transitive action 取得）。
- `github/github.go` は in-process cache + singleflight で重複 API 呼び出しを削減しつつ、`GHASEC_GITHUB_TOKEN` / `GITHUB_TOKEN` を使用。

---

## 5. 変更反映の実務フロー（要約）

1. **SchemaStore 更新の取り込み**
   - submodule 更新（主に Renovate で検知）
2. **生成物更新**
   - `go generate ./rules/invalid-workflow ./rules/invalid-action`（または `go generate ./...`）
3. **差分確認**
   - `rules/invalid-*/generated.go` 差分 + 関連テスト
4. **補完実装**
   - スキーマで表現困難な要件は手書き拡張に追加
5. **品質担保**
   - `go test ./...` と e2e テストで回帰確認

---

## 6. 調査上の補足

- ghasec は「AST 直接パース + 生成スキーマバリデータ + セキュリティルール」のハイブリッドでSchemaStore 依存度が高い。
- required ルールをゲートにして以降のルール実行を止めるため、誤検知耐性とエラーメッセージ安定性を優先した設計。
- オンラインルールは明示 opt-in（`--online`）で、オフライン運用時の再現性を維持しつつ、必要時のみ外部整合性チェックを強化できる。
