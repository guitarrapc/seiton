# seiton アーキテクチャ検討

## 1. 結論

本プロジェクトの初期実装は **Go を第一候補** とする。

理由は以下の通り。

1. GitHub Actions ワークフロー lint では、**構文と意味ルールをコードで表現する設計** が現実的であり、Go はその実装コストと保守性のバランスが最もよい。
2. `gopkg.in/yaml.v3` の `yaml.Node` から **行・列情報を素直に保持できる** ため、診断の位置精度を出しやすい。
3. 先行事例として `actionlint` が非常に強く、**設計上の勝ち筋が既に確認されている**。
4. 単一バイナリの CLI 配布、CI への組み込み、クロスプラットフォーム配布が容易。
5. Rust は長期的には魅力があるが、初期段階で必要な「YAML の形の厳密検証」「文脈依存ルール」「位置情報付き診断」を最短で成立させるには、Go のほうが立ち上がりが速い。

**推奨方針は、actionlint 型のハイブリッド構成** とする。

1. ワークフロー YAML の構造検証は、JSON Schema を主にせず **コード化したパーサ** で行う。
2. GitHub Docs / SchemaStore 由来の変化しやすいデータは **生成コードまたは vendored JSON** として管理する。
3. セキュリティルール、ベストプラクティスルール、記法ルールは **Visitor ベースの Rule Engine** で AST を巡回して評価する。

## 2. 言語選定

### 2.1 比較結果

| 観点 | C# | Go | Rust |
|---|---|---|---|
| CLI 配布 | 良い | 非常に良い | 非常に良い |
| YAML の位置情報 | 可能 | 良い | 工夫が必要 |
| GitHub Actions 先行事例 | 少ない | 非常に強い | 強い |
| 初期実装速度 | 良い | 非常に良い | 中程度 |
| 型安全性 | 良い | 中程度 | 非常に高い |
| 低レベル制御 | 中程度 | 良い | 非常に高い |
| 学習・保守コスト | 中程度 | 低い | 高い |
| セキュリティ解析の拡張性 | 良い | 良い | 非常に高い |

### 2.2 Go を推奨する理由

#### Go が適する点

1. **YAML ノードを直接読む実装** がしやすい。
2. エラー回復しながら複数診断を返すパーサを書きやすい。
3. AST と Rule Engine をシンプルに保てる。
4. 将来 `actionlint` の設計や知見を取り込みやすい。
5. GitHub Actions lint ツール利用者は Go 製 CLI を受け入れやすい。

#### Rust を第一候補にしない理由

Rust は、深い静的解析、オンライン監査、tree-sitter を使ったシェル解析、自動修正まで見据えると強い。一方で、初期フェーズで重要な以下の論点ではコストが上がりやすい。

1. YAML の元ソースとの位置対応。
2. 構文検証と意味検証をまたぐ実装の複雑さ。
3. チームがまだ設計を固めていない段階での抽象化過剰。

つまり Rust は「最終到達点」としては魅力があるが、「最初の正しい一歩」としては過剰投資になりやすい。

#### C# を第一候補にしない理由

C# でも十分実装可能であり、`YamlDotNet` や .NET の CLI 体験も悪くない。ただし本テーマでは、GitHub Actions lint の先行知見、周辺実装、CLI 文化、手実装パーサの前例が Go に偏っている。チームに .NET の強い資産がない限り、優位性は薄い。

### 2.3 最終判断

**開始言語は Go** とする。

ただし、将来以下の条件が揃ったら Rust 再評価は合理的。

1. オンライン監査や大規模並列解析を本格導入する。
2. 自動修正を高度化し、YAML パッチ精度を強く求める。
3. Shell AST 解析やデータフロー解析を中核機能にする。

## 3. 設計原則

### 3.1 基本方針

本プロジェクトは **単一の JSON Schema で GitHub Actions 全体を表現しようとしない**。

理由は以下の通り。

1. GitHub Actions の妥当性は、単純な構造だけでなく **文脈依存ルール** に強く依存する。
2. `jobs.<id>.uses` と `steps` の排他、イベントごとの `types`、式コンテキストの可用性などは、Schema だけでは表現しにくい。
3. 先行事例でも、成功している実装は「構文をコードで持ち、変化しやすい表データのみ自動生成」である。

### 3.2 採用するアーキテクチャ

以下の 4 層構成を採用する。

1. **Source / YAML Layer**
   元ソース、YAML ノード、位置情報、ソース断片の管理。
2. **Parse / Model Layer**
   YAML ノードから typed AST を構築しつつ、構文スキーマを検証。
3. **Semantic Rule Layer**
   AST を巡回してセキュリティ・記法・ベストプラクティスを評価。
4. **Diagnostic Layer**
   行・列、関連位置、ヘルプ、修正候補、機械可読出力を生成。

## 4. スキーマ検証戦略

### 4.1 推奨方針

スキーマ検証は **ハイブリッド方式** とする。

#### A. 主たる検証

ワークフロー YAML の主要な妥当性は、`yaml.Node` を直接走査する **コード化パーサ** で検証する。

検証対象の例:

1. 許可キー、必須キー。
2. 値型の制約。
3. 相互排他、条件付き必須。
4. reusable workflow 呼び出し時の制約。
5. step と job の形の違い。

#### B. 補助的検証

SchemaStore 由来の以下は vendoring する。

1. `github-workflow.json`
2. `github-action.json`
3. 必要なら `dependabot-2.0.json`

ただし、これらは **補助的な互換性確認** として扱う。主たる仕様源にはしない。

### 4.2 なぜ JSON Schema 主体にしないか

`zizmor` 型の JSON Schema 検証は、入力の粗い妥当性確認としては有効だが、以下の問題がある。

1. Rust / Go / C# のモデル更新がスキーマ追随と別問題になる。
2. スキーマが更新されても、意味ルールまではカバーできない。
3. GitHub Actions 固有の「実行文脈」までは表現しづらい。

したがって本プロジェクトでは、**構造はコード、可変データは生成物** という分割がよい。

### 4.3 更新対象の分類

更新対象は次の 3 種類に分けて扱う。

#### 1. 手実装の構文ルール

例:

1. workflow 直下キー。
2. `jobs.<id>` の許可キー。
3. `uses` / `steps` の排他。

これは **手実装で保守** する。

#### 2. 自動更新しやすい表データ

例:

1. webhook event 名と activity types。
2. expression context の可用性。
3. 組み込み関数一覧。
4. popular actions の metadata。

これは **スクリプトで生成し、コードとして commit** する。

### 4.4 generated data の定義

ここでいう generated data は、Go の `go generate` を必須にするという意味ではない。意味としては、**外部仕様から取得した可変データを、lint 実行時にネットワーク参照せず使える形に事前固定化する** ことである。

Go では以下のどれでもよい。

1. `go generate` から更新スクリプトを呼ぶ。
2. `cmd/update-*` のような更新専用コマンドを実行する。
3. Makefile や CI から更新スクリプトを直接実行する。

重要なのは手段ではなく、**生成物を commit し、通常の lint 実行時は生成済みデータだけを読む** 運用である。

生成対象は次のように分ける。

#### A. 生成コード化するもの

1. webhook event/activity types 一覧。
2. expression context availability table。
3. special function names。
4. popular actions の input/output metadata。

これらは `map[string]...` や定数配列として埋め込めるため、実行時パースを避けられ、性能面でも有利である。

#### B. vendored JSON のまま持つもの

1. SchemaStore の workflow/action/dependabot schema。

これらは主ロジックではなく補助検証用途のため、無理に Go コードへ変換せず JSON のまま保持するほうが保守しやすい。

#### C. 生成しないもの

1. パーサ本体の許可キー。
2. AST モデル。
3. semantic rules と policy rules。

ここは仕様差分の意味解釈が必要なため、コードとして人間が保守する。

#### 3. 補助的な外部スキーマ

例:

1. SchemaStore の workflow / action schema。

これは **vendored JSON として定期同期** する。

## 5. YAML 解析方針

### 5.1 使うべき API

Go では `gopkg.in/yaml.v3` の `yaml.Node` を中核に置く。

直接 struct へデコードするだけではなく、**Node ベースで AST を構築する**。

理由は以下の通り。

1. 行・列を失わない。
2. 未知キーを精密に検出できる。
3. key と value の両方に対して診断位置を持てる。
4. エラー回復しながら解析継続しやすい。

### 5.2 推奨パースパイプライン

```text
raw YAML
  -> yaml.Decoder / yaml.Node
  -> SourceIndex で行・列・断片取得可能にする
  -> custom parser が Workflow AST を構築
  -> parser が syntax diagnostics を収集
  -> semantic rules が AST を巡回
  -> final diagnostics を出力
```

### 5.3 AST をどう持つか

各ノードは次を持つ。

1. ドメイン値。
2. 対応する YAML node 参照または Span。
3. 子要素へのリンク。
4. 元の文字列表現が必要なら raw text。

例:

```go
type Span struct {
	Line      int
	Column    int
	EndLine   int
	EndColumn int
}

type Workflow struct {
	Name        *StringNode
	RunName     *StringNode
	On          *EventSpec
	Jobs        map[string]*Job
	Span        Span
}
```

`StringNode` や `ExprNode` のような小ノードに Span を持たせると、ルール実装時に再計算が不要になる。

### 5.4 YAML の寛容性への対応

GitHub Actions YAML は同じキーが複数形を取り得るため、以下のユーティリティを最初から用意する。

1. scalar or sequence
2. string or mapping
3. bool-ish string
4. explicit expression or literal

ただし、`ghalint` のように「必要なフィールドだけ struct で受ける」戦略は、初期の簡易 lint には向くが、本プロジェクトのような **スキーマ検証付き lint** には不足する。ここは `actionlint` 側に寄せるべき。

## 6. Expression 解析方針

### 6.1 必要性

セキュリティルールとベストプラクティスルールの多くは `${{ }}` を正しく解釈できないと成立しない。

例:

1. attacker-controlled context の判定。
2. `secrets.*` や `github.token` の伝播。
3. `contains()` や条件式の unsound pattern 検出。

### 6.2 段階的戦略

初期版は 2 段階で進める。

#### Phase 1

1. `${{ ... }}` の fenced expression を抽出。
2. 最低限の式パーサを実装。
3. context path、関数呼び出し、比較、論理演算を扱う。

#### Phase 2

1. 型検証。
2. context availability 検証。
3. dataflow に近い taint 解析。

### 6.3 推奨判断

初期段階では **式パーサを独立モジュールとして自前実装** する。理由は以下。

1. ルール実装が式 AST に強く依存する。
2. 外部ライブラリ依存で GitHub Actions 独自文法の穴を引き継ぎたくない。
3. 将来の context validation や taint metadata 付与を制御しやすい。

## 7. ポリシー定義方式

### 7.1 結論

ポリシーは **コード実装を基本** とし、設定ファイルでは有効・無効、閾値、deny/allow リストなどのパラメータだけを与える。

DSL 主体にはしない。

### 7.2 ルールの分類

ルールは次の 3 系統に分ける。

1. **Syntax Rules**
   YAML の構造、キー、型、排他条件。
2. **Semantic Rules**
   GitHub Actions の文脈依存仕様、式の可用性、イベントごとの制約。
3. **Policy Rules**
   セキュリティ、組織規約、ベストプラクティス。

### 7.3 Rule Interface

推奨する最小形は以下。

```go
type Rule interface {
	ID() string
	Metadata() RuleMetadata
}

type WorkflowRule interface {
	Rule
	VisitWorkflow(*RuleContext, *Workflow)
}

type JobRule interface {
	Rule
	VisitJob(*RuleContext, *Workflow, *Job)
}

type StepRule interface {
	Rule
	VisitStep(*RuleContext, *Workflow, *Job, *Step)
}
```

`actionlint` の Visitor 発想を踏襲しつつ、ルール単位の責務を小さく保つ。

### 7.4 Rule Metadata

全ルールは次のメタデータを持つ。

1. ID
2. Title
3. Category
4. Severity
5. Default enabled
6. Docs URL
7. Supports autofix
8. Required capabilities

これにより CLI 出力、SARIF、設定ファイル、ドキュメント生成を統一できる。

### 7.5 設定ファイルの役割

設定ファイルは次だけに限定する。

1. ルールの enable / disable
2. severity 上書き
3. ignore 指定
4. ルール個別設定

つまり **ルール自体はコード、運用差分は設定** に分離する。

## 8. 行・列・診断位置の扱い

### 8.1 結論

行・列は `yaml.Node.Line` と `yaml.Node.Column` を起点にし、独自 `Span` を全 AST ノードに保持する。

### 8.2 必要な設計

診断は単一位置だけでなく、以下を持てるようにする。

1. **Primary span**
2. **Related spans**
3. YAML path
4. Snippet 生成用の source reference

推奨構造:

```go
type Diagnostic struct {
	RuleID      string
	Severity    Severity
	Message     string
	Primary     Span
	Related     []RelatedLocation
	Path        string
	Help        string
	Suggestion  *Fix
}
```

### 8.3 end position の扱い

`yaml.v3` は終了位置を直接持たないため、以下のどちらかを採用する。

1. 初期版は **開始位置のみを保証** する。
2. 後続で `SourceIndex` を使い、Node 内容から end column を推定する。

初期版では 1 で十分。重要なのは、**誤った詳細位置を出すより、正しい開始位置を安定して出すこと**。

### 8.4 どこを指すか

診断位置の原則は次。

1. 未知キーは key を指す。
2. 値型エラーは value を指す。
3. 排他条件違反は両方を related で出す。
4. ルール違反は、利用者が最初に直すべき場所を primary にする。

これは IDE 表示と CLI 表示の両方で効く。

## 9. 更新戦略

### 9.1 自動更新するもの

以下は CI で週次更新する。

1. vendored JSON schema
2. webhook event/activity types
3. context availability table
4. popular actions metadata

### 9.2 自動更新の流れ

```text
schedule / workflow_dispatch
  -> update script を実行
  -> generated data / vendored schema を更新
  -> tests 実行
  -> diff があれば自動 PR 作成
```

更新スクリプトは、生成コードを書き出すだけでなく、必要なら JSON をそのまま vendoring する責務も持つ。

### 9.3 手動更新するもの

以下は自動化しない。

1. パーサ本体の許可キー。
2. AST モデル。
3. 文脈依存の意味ルール。
4. セキュリティポリシー本体。

理由は、ここは仕様変更を **人が意味解釈して取り込むべき領域** だからである。

## 10. 推奨モジュール構成

```text
cmd/seiton/
	main.go

internal/source/
	source.go          // ファイル読込、行テーブル、snippet 抽出
	span.go            // Span, RelatedLocation

internal/diag/
	diagnostic.go      // Diagnostic モデル
	renderer_plain.go
	renderer_sarif.go
	renderer_json.go

internal/yamlast/
	nodeutil.go        // yaml.Node helper
	decode.go          // scalar/sequence/mapping helper

internal/gha/ast/
	workflow.go
	job.go
	step.go
	event.go
	expression.go

internal/gha/parser/
	parser.go          // entrypoint
	parse_workflow.go
	parse_job.go
	parse_step.go
	parse_event.go

internal/gha/schema/
	vendored/          // github-workflow.json など
	validate.go        // 補助 schema validation

internal/expr/
	lexer.go
	parser.go
	ast.go
	semantics.go

internal/rules/
	registry.go
	visitor.go
	syntax/
	semantics/
	policy/

internal/config/
	config.go

internal/update/
	schema_sync.go
	webhook_sync.go
	availability_sync.go

testdata/
	valid/
	invalid/
	rules/
```

## 11. パフォーマンス・アロケーション方針

### 11.1 結論

本プロジェクトでは、**高スループットと低アロケーションを最優先要件** として扱うべきである。

ただし、Go で「完全に 0 アロケーション」をエンドツーエンドで保証するのは現実的ではない。特に YAML パース、文字列生成、診断出力の全体ではアロケーションは発生する。

したがって目標は次のように定義する。

1. **ホットパスでは実質ゼロに近づける**。
2. パース後の Visitor 実行、ルール評価、lookup を極小アロケーションで実装する。
3. ファイル全体での総アロケーション量を継続計測する。

### 11.2 Go で可能なこと

Go でもかなり低アロケーションにはできるが、前提条件がある。

1. 入力は `[]byte` のまま扱い、不要な `string` 化を避ける。
2. lookup 用データは生成済み map や配列に固定する。
3. AST ノード数を抑え、必要最小限の構造だけ持つ。
4. Visitor 中は一時 slice と builder を再利用する。
5. 正規表現や汎用 interface の多用を避ける。

ただし `yaml.v3` 自体が内部でアロケーションを行うため、**パーサを含めた完全 0 アロケーションは難しい**。

### 11.3 Go での実務的な目標

1. YAML 解析部分は「少ないがゼロではない」アロケーションを許容する。
2. ルール評価部分はベンチマーク上ほぼゼロに近づける。
3. 出力整形は最後にまとめて行い、中間文字列を極力作らない。

### 11.4 設計上の禁止事項

1. ルールごとに AST を再走査しながら大量の中間オブジェクトを作ること。
2. 文字列 split / replace / regex をホットパスで多用すること。
3. 外部仕様データを毎回 JSON パースしてロードすること。
4. `map[string]any` や `interface{}` 中心の動的モデルで処理すること。

## 12. 実装優先順位

### Phase 1: 最小成立系

1. CLI 骨格
2. YAML 読み込み
3. workflow parser
4. syntax diagnostics
5. basic rule engine
6. plain text 出力

この段階では、まず `actionlint` 的な「正しく壊れるパーサ」を成立させる。

### Phase 2: 価値のある lint

1. expression parser 最小版
2. security / best practice rules 追加
3. 設定ファイル
4. SARIF / JSON 出力 / GitHub Actions ログ出力

### Phase 3: 追随性強化

1. schema vendoring 自動更新
2. webhook / availability 生成
3. popular actions metadata 連携

### Phase 4: 高度化

1. autofix
2. shell script 解析
3. taint 解析
4. LSP 対応

## 13. 本プロジェクト向けの最終提案

### 12.1 採用案

以下を採用する。

1. **言語: Go**
2. **構文検証: `yaml.Node` ベースの手実装パーサ**
3. **意味検証: Visitor ベース Rule Engine**
4. **補助スキーマ: SchemaStore を vendoring して定期同期**
5. **可変データ: GitHub Docs 由来データを生成コード化**
6. **位置情報: 全 AST ノードに Span を持たせる**
7. **設定: ルールの有効化とパラメータ調整に限定**

### 12.2 採用しない案

以下は初期設計としては採用しない。

1. JSON Schema 主体で workflow の妥当性を表現する方式。
2. YAML を全面的に struct 直デコードして lint する方式。
3. ルールを DSL 主体で定義する方式。
4. 早い段階から自動修正を前提にした複雑なパッチ基盤を入れる方式。

### 12.3 意思決定の要約

このプロジェクトの本質は「GitHub Actions の YAML を読む」ことではなく、**GitHub Actions の仕様と実務上の安全な使い方を継続的にコード化すること** にある。

そのため、最も重要なのは次の 2 点である。

1. **構文を自分たちで制御できること**
2. **変化しやすい外部知識だけを自動更新できること**

この条件を最も無理なく満たすのが、Go + hand-written parser + generated metadata という構成である。
