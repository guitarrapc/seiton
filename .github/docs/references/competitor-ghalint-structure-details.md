# ghalint — リポジトリ詳細解析レポート

---

## 1. プロジェクト概要

**ghalint** はGitHub Actionsワークフロー（`.github/workflows/*.ya?ml` / `action.yaml`）向けのセキュリティ特化CLIリンターです。最小権限・サプライチェインセキュリティ・シークレット管理のベストプラクティスを番号付きポリシーとして表現し、違反があれば構造化ログにエラーを吐いて非0終了します。

> Jsonnet ベースの姉妹プロジェクト[lintnet-modules/ghalint](https://github.com/lintnet-modules/ghalint)も存在します。

---

## 2. アーキテクチャ

```
cmd/ghalint/main.go
    └── pkg/cli/app.go  (urfave/cli v3 コマンドツリー)
            ├── Runner.Run()        → pkg/controller/  (workflow lint)
            ├── Runner.RunAction()  → pkg/controller/act/  (action lint)
            └── experiment/validateinput/  (不安定機能)
```

### 主要パッケージ

| パッケージ | 役割 |
|---|---|
| `pkg/cli` | CLI の配線（フラグ・サブコマンド・環境変数）|
| `pkg/config` | 設定ファイルの探索・YAML パース・バリデーション |
| `pkg/workflow` | YAML データ構造（`Workflow`, `Job`, `Step`, `Action`等）|
| `pkg/policy` | 全ポリシー実装・コンテキスト型・センチネルエラー |
| `pkg/controller` | ファイル探索 → パース → ポリシー評価 → エラー出力のオーケストレーション |
| `pkg/controller/schema` | アクション`inputs:`のランタイム検証（実験的機能）|
| `pkg/action` | `action.yaml`ファイルの Glob ベース探索 |

### 主要外部依存

| ライブラリ | 用途 |
|---|---|
| `github.com/urfave/cli/v3` | CLI フレームワーク |
| `github.com/spf13/afero` | ファイルシステム抽象化（Unit テストでインメモリ FS を使用可能にする）|
| `gopkg.in/yaml.v3` | YAML パース |
| `github.com/suzuki-shunsuke/slog-error/slogerr` | 構造化エラーログ |
| `github.com/google/go-github/v81` + `golang.org/x/oauth2` | GitHub API（experimental 機能）|
| `github.com/zalando/go-keyring` | GitHub API 認証情報のシステムキーリング取得 |

---

## 3. CLI コマンド・フラグ

### グローバルフラグ

| フラグ | 環境変数 | 説明 |
|---|---|---|
| `--log-color` | `GHALINT_LOG_COLOR` | `auto`(デフォルト) / `always` / `never` |
| `--log-level` | `GHALINT_LOG_LEVEL` | `error` / `warn` / `info`(デフォルト) / `debug` |
| `--config`, `-c` | `GHALINT_CONFIG` | 設定ファイルの明示的パス |

### サブコマンド

| コマンド | エイリアス | 説明 |
|---|---|---|
| `ghalint run` | — | `.github/workflows/*.ya?ml`を全件 lint |
| `ghalint run-action` | `act` | `action.ya?ml`を lint（最大 4 階層まで探索、または引数に明示）|
| `ghalint exp validate-input` | — | GitHub API でアクションのメタデータを取得してインプット検証（不安定）|
| `ghalint version` | — | バージョン出力 |
| `ghalint completion` | — | シェル補完スクリプト出力 |

---

## 4. データフロー

### `ghalint run`の処理フロー

```
1. 設定ファイル探索
   ghalint.yaml / .ghalint.yaml / .github/ghalint.yaml (.yml 変種含む）
   → YAML デコード → Config{Excludes} → バリデーション・パス正規化

2. ワークフローファイル探索
   afero.Glob(".github/workflows/*.yml") + ("*.yaml")
   → []string のファイルパス一覧

3. ファイルごとの YAML パース
   yaml.NewDecoder(f).Decode(*Workflow)
   → Workflow{ Jobs, Env, Permissions }
     └── Job{ Permissions, Env, Steps, Secrets, Container, Uses, TimeoutMinutes }
           └── Step{ Uses, ID, Name, Run, Shell, With, TimeoutMinutes }

4. ポリシー評価
   ワークフローポリシー → ApplyWorkflow(wf)
   ジョブポリシー       → ApplyJob(job)     (ジョブごと)
   ステップポリシー     → ApplyStep(step)   (ステップごと)

5. エラー出力
   違反時 → slogerr 構造化ログ（workflow_file_path, job_name, step_id 等を付与）
   → urfave.ErrSilent で非 0 終了
```

### `ghalint run-action`の差分
- ファイル探索は`action.Find(fs)`（3階層サブディレクトリまでの`action.ya?ml`）
- ステップポリシーのみ適用（ワークフロー/ジョブポリシーは対象外）
- `StepContext.Action`がセット（workflowモードでは`StepContext.Job`）

---

## 5. ポリシー一覧

### ワークフローポリシー（ワークフローファイルあたり 1 回）

| ID | ポリシー名 | チェック内容 |
|---|---|---|
| 005 | `workflow_secrets` | ワークフローの`env:`に`${{ secrets.* }}` / `${{ github.token }}`が含まれていないか（2 ジョブ以上の場合）|

### ジョブポリシー（ジョブごと）

| ID | ポリシー名 | チェック内容 |
|---|---|---|
| 001 | `job_permissions` | `permissions:`の宣言が必須（例外: ワークフローが`{}`を指定、または 1 ジョブのみ）|
| 002 | `deny_read_all_permission` | `permissions: read-all`の禁止 |
| 003 | `deny_write_all_permission` | `permissions: write-all`の禁止 |
| 004 | `deny_inherit_secrets` | `secrets: inherit`の禁止（除外設定可）|
| 006 | `job_secrets` | ジョブ`env:`への`${{ secrets.* }}` / `${{ github.token }}`禁止（2 ステップ以上、除外設定可）|
| 007 | `deny_job_container_latest_image` | コンテナイメージに`:latest`タグ禁止 |
| 008 | `action_ref_should_be_full_length_commit_sha` | `uses:`の ref が 40 文字 SHA-1（または Docker は 64 文字 SHA-256）であること（除外設定可）|
| 012 | `job_timeout_minutes_is_required` | ジョブに`timeout-minutes`が必須（または全ステップが個別に設定）|

### ステップポリシー（`run` / `run-action`両方で適用）

| ID | ポリシー名 | チェック内容 |
|---|---|---|
| 008 | `action_ref_should_be_full_length_commit_sha` | ステップの`uses:` ref が完全長 SHA であること |
| 009 | `github_app_should_limit_repositories` | `tibdex/github-app-token` / `actions/create-github-app-token`に`repositories`インプット必須 |
| 010 | `github_app_should_limit_permissions` | 上記アクションに`permissions` / `permission-*`インプット必須 |
| 011 | `action_shell_is_required` | アクションの`run:`があれば`shell:`も必須 |
| 013 | `checkout_persist_credentials_should_be_false` | `actions/checkout@*`に`with.persist-credentials: "false"`必須（除外設定可）|

---

## 6. 設定ファイル構造

### ファイル探索順（先に見つかったものを使用）

```
ghalint.yaml → .ghalint.yaml → .github/ghalint.yaml
ghalint.yml  → .ghalint.yml  → .github/ghalint.yml
```

`-c` / `GHALINT_CONFIG`で上書き可能。

### YAML 構造

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/suzuki-shunsuke/ghalint/main/json-schema/ghalint.json
excludes:
  - policy_name: <string>           # 必須
    workflow_file_path: <string>    # ワークフロースコープの除外
    action_file_path: <string>      # アクションスコープの除外
    job_name: <string>              # ジョブスコープの除外
    action_name: <string|glob>      # ポリシー 008 用（パスグロブ可）
    step_id: <string>               # ステップスコープの除外（ポリシー 009）
```

### 除外設定が利用できるポリシーと必須フィールド

| ポリシー | 必要な除外フィールド |
|---|---|
| `deny_inherit_secrets` (004) | `policy_name`, `workflow_file_path`, `job_name` |
| `job_secrets` (006) | `policy_name`, `workflow_file_path`, `job_name` |
| `action_ref_should_be_full_length_commit_sha` (008) | `policy_name`, `action_name`（グロブ可）|
| `github_app_should_limit_repositories` (009) | `policy_name`, `workflow_file_path`/`action_file_path`, `job_name`, `step_id` |
| `checkout_persist_credentials_should_be_false` (013) | `policy_name`, `workflow_file_path` + `job_name`または`action_file_path` |

### JSON スキーマ
`json-schema/ghalint.json`を`cmd/gen-jsonschema/main.go`が`config.Config` structのリフレクションから自動生成。エディタの補完・バリデーションに使用。GitHub Actionsワークフロー自体の検証には使用しない。

---

## 7. コアデータ構造

```go
// gopkg.in/yaml.v3 で YAML → struct に直接デコード
type Workflow struct {
    FilePath    string            // yaml:"-" (ランタイムのみ)
    Jobs        map[string]*Job
    Env         map[string]string
    Permissions *Permissions
}

type Job struct {
    Permissions    *Permissions
    Env            map[string]string
    Steps          []*Step
    Secrets        *JobSecrets   // "inherit" または map
    Container      *Container    // 文字列または {image: ...}
    Uses           string        // reusable workflow ref
    TimeoutMinutes any           // nil = 未設定
    With           map[string]any
}

type Step struct {
    Uses, ID, Name, Run, Shell string
    With           With         // map[string]string（int/bool を自動キャスト）
    TimeoutMinutes any
}
```

`Permissions`, `Container`, `JobSecrets`はYAMLが文字列にも辞書にもなりうるため、`UnmarshalYAML`でカスタム解析を実装。

---

## 8. GitHub Actions スキーマへの追随方法

### 結論：自動追随の仕組みは存在しない

ghalintはGitHub Actionsの公式YAMLスキーマ（SchemaStore等）に追随する自動化された仕組みを**一切持っていない**。

### 設計思想：必要なフィールドだけ定義する

```go
// pkg/workflow/workflow.go — ポリシーが必要とするフィールドだけ定義
type Workflow struct {
    FilePath    string            `yaml:"-"`
    Jobs        map[string]*Job
    Env         map[string]string
    Permissions *Permissions
}
```

`if:`, `strategy:`, `runs-on:`などlintに不要なフィールドはstructに存在せず、`gopkg.in/yaml.v3`がデコード時に**黙って無視**する。
これによりGitHub側でフィールドが追加されても、lint対象でない限りstructを変更する必要がない。

### `//go:generate`はゼロ件

プロジェクト全体で`//go:generate`ディレクティブは一切使用されていない。Go structの更新は開発者が手動で行う。

### SchemaStore・公式 GitHub Actions スキーマへの参照もゼロ

リポジトリ内に`schemastore.org`や`github-workflow.json`への参照は存在しない。

### 多態的フィールドはすべて手書きカスタム Unmarshaler

GitHub Actions YAMLでは同一キーが文字列にも辞書にもなり得る。これは自動生成でなく**開発者が手書き**している：

| フィールド | YAML 上の型バリエーション | 実装場所 |
|---|---|---|
| `permissions` | `"read-all"`文字列 or `{contents: read}`マップ | `pkg/workflow/permissions.go` |
| `container` | `"node:18"`文字列 or `{image: node:18}`マップ | `pkg/workflow/container.go` |
| `secrets` | `"inherit"`文字列 or `{TOKEN: ...}`マップ | `pkg/workflow/job_secrets.go` |
| `on`（schema サブパッケージ） | `"workflow_call"`文字列 or マップ | `pkg/controller/schema/reusable_workflow.go` |
| `with`値 | `string` / `int` / `float64` / `bool` | `pkg/workflow/workflow.go` |

### CI/CD ワークフローによるスキーマ追随は無し

`.github/workflows/`内の6ファイルの役割：

| ファイル | 役割 |
|---|---|
| `test.yaml` | `go test ./...` + golangci-lint 実行 |
| `workflow_call_test.yaml` | test.yaml から呼ばれる再利用ワークフロー |
| `actionlint.yaml` | `actionlint`でワークフロー構文チェック |
| `autofix.yaml` | PR に`go-autofix-action`を適用 |
| `release.yaml` | タグ時のリリースビルド・公開 |
| `check-commit-signing.yaml` | PR の全コミット署名チェック |

GitHub Actionsのスキーマ変更を検出するCIジョブは存在しない。

### Renovate は依存バージョン更新のみ

`renovate.json5`はGoモジュールバージョンと`aqua`ツールバージョンの自動更新のみ担当。GitHub ActionsのYAMLスキーマ変化は監視しない。

### `pkg/controller/schema/`の実態

パッケージ名から「スキーマ検証」を連想させるが、実際はアクション呼び出し側の **`inputs:`キー検証**（実験的機能）：

```
1. ワークフロー内の step.uses を走査
2. GitHub API から対象アクションの action.yaml をダウンロード
   （またはローカルキャッシュ $GHALINT_ROOT_DIR/actions/<owner>/<repo>/<sha>/ から読む）
3. action.yaml の inputs: セクションをパース
4. 呼び出し側が渡している with: キーが宣言済みか確認
5. required: true のインプットが渡されているか確認
```

これは「YAML構文スキーマの検証」ではなく「アクションのインタフェース適合チェック」。

### まとめ：スキーマ追随の設計方針

| 観点 | 実態 |
|---|---|
| スキーマ追随の自動化 | **なし** — 開発者が手動で struct を更新 |
| 新フィールドへの追随タイミング | 新ポリシーを実装するときに初めて struct に追加 |
| 無関係なフィールドの扱い | yaml.v3 が黙って無視 → 破壊的変更の影響を受けにくい |
| コード生成 | **なし**（`//go:generate`ゼロ件）|
| 公式スキーマ参照 | **なし** |

---

## 9. テスト戦略

### 全テストファイルに共通するパターン

1. **テーブル駆動テスト** — `data := []struct{...}{...}`スライスで全ケースを列挙
2. **`t.Parallel()`** — 外側の関数と各`t.Run()`サブテスト両方で呼び出し
3. **インラインデータ** — testdata/ ディレクトリなし、ゴールデンファイルなし

### `pkg/workflow/` — YAML アンマーシャリングのテスト

カスタム`UnmarshalYAML`の動作を直接テスト。YAML文字列をインラインで定義して`yaml.Unmarshal` → structフィールドを検証するパターン。

#### `permissions_test.go`
```go
func TestPermissions_UnmarshalYAML(t *testing.T) {
    data := []struct {
        name     string
        yaml     string
        readAll  bool
        writeAll bool
    }{
        {name: "not read-all and write-all", yaml: `contents: read`},
        {name: "read-all",  yaml: `read-all`,  readAll: true},
        {name: "write-all", yaml: `write-all`, writeAll: true},
    }
    for _, d := range data {
        t.Run(d.name, func(t *testing.T) {
            t.Parallel()
            p := &workflow.Permissions{}
            yaml.Unmarshal([]byte(d.yaml), p)
            // p.ReadAll() / p.WriteAll() を検証
        })
    }
}
```

同様のパターンで`container_test.go`（文字列vsマップ）と`job_secrets_test.go`（"inherit" vsマップ）もテスト。

### `pkg/policy/` — ポリシーロジックのテスト

ポリシーテストは **Go struct を直接構築**（YAMLパースを経由しない）し、`Apply*`メソッドを呼んでエラーの有無を検証する。

```go
// 典型例: deny_write_all_policy_test.go
data := []struct {
    name   string
    jobCtx *policy.JobContext
    job    *workflow.Job
    isErr  bool
}{
    {
        name:  "don't use write-all",
        job:   &workflow.Job{Permissions: workflow.NewPermissions(false, true, nil)},
        isErr: true,
    },
    {
        name: "job permissions is null and workflow permissions is write-all",
        jobCtx: &policy.JobContext{
            Workflow: &policy.WorkflowContext{
                Workflow: &workflow.Workflow{
                    Permissions: workflow.NewPermissions(false, true, nil),
                },
            },
        },
        job:   &workflow.Job{},
        isErr: true,
    },
    {
        name: "pass",
        job: &workflow.Job{
            Permissions: workflow.NewPermissions(false, false, map[string]string{"contents": "write"}),
        },
    },
}
p := &policy.DenyWriteAllPermissionPolicy{}
logger := slog.New(slog.DiscardHandler)  // 全テスト共通: no-op ロガー
```

**全テストに共通するイディオム：**
- `logger := slog.New(slog.DiscardHandler)` — ログ出力を完全に捨てるno-opロガー
- テストケースで`jobCtx` / `stepCtx`が省略された場合はループ前でデフォルト注入
- エラーチェックパターン: `if err != nil { if !d.isErr { t.Fatal(err) }; return }` / `if d.isErr { t.Fatal("error must be returned") }`
- テストヘルパー共有ファイルは存在しない（`testhelper_test.go`等なし）

### 除外ロジックのテスト

`config.Config{Excludes: []*config.Exclude{{...}}}`をインラインで構築して除外が正しく動作するか検証：

```go
// checkout_persist_credentials_should_be_false_test.go
{
    name: "exclude",
    cfg: &config.Config{
        Excludes: []*config.Exclude{{
            PolicyName:       "checkout_persist_credentials_should_be_false",
            WorkflowFilePath: ".github/workflows/test.yml",
            JobName:          "test",
        }},
    },
    // isErr: false — 除外されるのでエラーなし
},
{
    name: "persist-credentials is not set",
    cfg: &config.Config{
        Excludes: []*config.Exclude{{
            PolicyName:       "checkout_persist_credentials_should_be_false",
            JobName:          "test-2",  // 別のジョブ名 → 除外が効かない
            WorkflowFilePath: ".github/workflows/test.yml",
        }},
    },
    isErr: true,  // 除外されないのでポリシー違反
},
```

### 特殊ケース：`deny_inherit_secrets_test.go`

ポリシーテストで唯一`yaml.Unmarshal`を使用するファイル。`secrets: inherit`は`JobSecrets.UnmarshalYAML`を通さないと正しく表現できないため：

```go
data := []struct {
    name   string
    job    string  // Go struct でなく生 YAML 文字列
    cfg    *config.Config
    isErr  bool
}{
    {name: "secrets: inherit", job: `secrets: inherit`, isErr: true},
    {name: "exclude",          job: `secrets: inherit`, cfg: &config.Config{...}, isErr: false},
}
// t.Run 内で:
job := &workflow.Job{}
yaml.Unmarshal([]byte(d.job), job)
p.ApplyJob(logger, d.cfg, d.jobCtx, job)
```

### `action_ref_should_be_full_length_commit_sha_policy_test.go`の注目ケース

最も複雑なテストファイル。`ApplyJob`と`ApplyStep`の両方をテスト：

```go
// Docker イメージのダイジェスト形式はパス
{name: "docker image with digest",
 step: &workflow.Step{Uses: "docker://rhysd/actionlint:1.7.7@sha256:887a..."}},

// Docker イメージのタグのみはエラー
{name: "docker image with tag",
 isErr: true,
 step: &workflow.Step{Uses: "docker://rhysd/actionlint:latest"}},

// グロブパターンによる除外
{name: "exclude with glob pattern",
 cfg: &config.Config{Excludes: []*config.Exclude{{
     PolicyName: "action_ref_should_be_full_length_commit_sha",
     ActionName: "slsa-framework/*",  // ワイルドカード
 }}},
 step: &workflow.Step{Uses: "slsa-framework/slsa-github-generator@v1.5.0"}},
// isErr: false — グロブ除外が効く
```

### `pkg/config/config_test.go`

`config.Validate()`の無効設定ケースのみをテスト（全ケース`isErr: true`）：
- `policy_name`が未設定
- `action_ref_should_be_full_length_commit_sha`に`action_name`なし
- `job_secrets`に`workflow_file_path`なし
- `job_secrets`に`job_name`なし
- 除外できないポリシー名（例: `deny_read_all_permission`）を指定

### 使用していないテスト技術

| 技術 | 使用状況 |
|---|---|
| `afero.MemMapFs`（インメモリ FS）| テストでは不使用（プロダクションコードの抽象化のみ）|
| ゴールデンファイル / `testdata/` | 不使用 |
| 個別の YAML フィクスチャファイル | 不使用 |
| `httptest`モック | 不使用 |
| `testify`アサーション | 不使用（標準ライブラリの`t.Fatal`のみ）|
| `//go:generate` | 不使用 |
| 共有テストヘルパーファイル | 不使用 |

---

## 10. ビルド・ツールチェイン

### タスクランナー（`cmdx`）

| タスク | コマンド |
|---|---|
| `cmdx test` | `go test ./... -race -covermode=atomic` |
| `cmdx lint` | `golangci-lint run` |
| `cmdx install` | `go install -ldflags "-X main.version=..."`でローカルインストール |
| `cmdx usage` | `scripts/generate-usage.sh`で`docs/usage.md`再生成 |
| `cmdx js` | `go run ./cmd/gen-jsonschema`で JSON スキーマ再生成 |

**バージョン埋め込み**: ビルド時に`-ldflags "-X main.version=..."`で`main.version`に注入。

### ツール管理 (`aqua`)

[aqua](https://aquaproj.github.io/)でバージョン宣言とchecksum検証（`require_checksum: true`）。`aqua/imports/`配下でツールを管理：
- `ghalint`, `golangci-lint`, `goreleaser`, `cosign`, `syft`, `reviewdog`, `go-licenses`, `typos`, `cmdx`

### Renovate / Typos
- `renovate.json5`で依存関係の自動更新
- `_typos.toml` + `typos`ツールでソースコード内のタイポ検出

---

## 11. Experimental: `validate-input`

- GitHub APIでアクションのメタデータを取得し、呼び出し側が宣言済みのインプットキーのみ渡しているか・`required: true`のインプットを漏れなく渡しているかを検証
- 認証は`go-keyring`（システムキーリング）または環境変数
- **不安定** — マイナー/パッチバージョンで変更・削除の可能性あり
- 制限事項: GitHub Actionsランタイムは`required: true`を強制しないため、偽陽性が発生する可能性あり
