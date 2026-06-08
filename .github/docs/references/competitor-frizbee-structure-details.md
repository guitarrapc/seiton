# frizbee — 競合ツール構造詳細

> 参照: `.references/frizbee/`
> 作者: Stacklok
> 目的: GitHub Actions 参照とコンテナイメージ参照の両方を checksum/SHA にピン留めする統合ツール。

---

## 1. 概要

frizbee は、次の 2 種類の参照を 1 つのインターフェースでピン留めする CLI ツールです。
- GitHub Actions の `uses:` 参照（tag → commit SHA、GitHub API で解決）
- コンテナイメージ参照（tag → OCI digest、レジストリ HEAD リクエストで解決）

pinact（Actions 専用）や dockerfile-pin（image 専用）と異なり、frizbee は 1 バイナリ内の別サブコマンドとして両方を扱います。対象は workflow YAML、docker-compose、任意 YAML ファイルです。

主なユースケース:
- `frizbee actions [dir]` — workflow ファイル内の Actions 参照を一括ピン留め
- `frizbee image [dir]` — YAML ファイル内の image 参照を一括ピン留め
- `frizbee actions <owner/repo@tag>` — 単一参照をその場で解決

---

## 2. アーキテクチャ

```
cmd/
  actions/actions.go     CmdGHActions — Actions サブコマンド
  image/image.go         CmdContainerImage — Image サブコマンド
internal/
  cli/cli.go             共通 CLI ヘルパー、GitHubTokenEnvKey、TokenHelpText
pkg/
  replacer/              中核置換ロジック（GH actions + container images）
  interfaces/            ErrReferenceSkipped
  utils/config/
    config.go            Config 構造体、DefaultConfig()、MergeUserConfig()
```

---

## 3. 解決戦略 — GitHub Actions SHA

### ライブラリ / API
- GitHub REST API（認証付き HTTP クライアント経由）
- トークン注入: `os.Getenv(cli.GitHubTokenEnvKey)` = `os.Getenv("GITHUB_TOKEN")`

### 解決フロー
```go
r := replacer.NewGitHubActionsReplacer(cfg).
    WithUserRegex(cliFlags.Regex).
    WithGitHubClientFromToken(os.Getenv(cli.GitHubTokenEnvKey))
```

1. `uses: owner/repo@tag` または `uses: owner/repo/.github/workflows/file.yml@tag` を解析
2. GitHub API で tag の commit SHA を取得
3. `@tag` を `@<sha>` に置換
4. 元 tag はインラインコメントとして保持: `@sha # tag`

### スキップ挙動
- `ExcludeBranches`（既定: `main`, `master`）に一致する参照はスキップ
- 設定の `Exclude` パターンに一致する参照はスキップ
- 解決失敗参照は `ErrReferenceSkipped` を返す（致命エラーではなく穏当にスキップ）

---

## 4. 解決戦略 — コンテナイメージ Digest

### 使用ライブラリ
`go-containerregistry`（dockerfile-pin と同様、デファクト標準）

### 解決フロー
```go
r := replacer.NewContainerImagesReplacer(cfg).
    WithUserRegex(cliFlags.Regex)
```

- OCI レジストリの `HEAD /v2/{name}/manifests/{reference}` を使用（マニフェスト本体は未取得）
- イメージ解決用の明示トークンはなし。システム credential chain（`authn.DefaultKeychain` による `~/.docker/config.json`）に依存

### スキップ挙動
- `ExcludeImages`（既定: `["scratch"]`）一致イメージをスキップ
- `ExcludeTags`（既定: `["latest"]`）一致タグをスキップ
- `scratch` はユーザー設定に無くても常に `ExcludeImages` へ追加（`MergeUserConfig` で強制）

---

## 5. 認証

### GitHub Actions
- 単一環境変数: `GITHUB_TOKEN`
- ツール専用環境変数なし（pinact の `PINACT_GITHUB_TOKEN` とは対照的）
- OS keyring 連携なし
- GHES サポートなし
- TokenHelpText: `"NOTE: It's recommended to set the GITHUB_TOKEN environment variable given that GitHub has tighter rate limits on anonymous calls."`

### コンテナイメージ
- 明示トークンなし。`go-containerregistry` の `authn.DefaultKeychain` を使用
- `~/.docker/config.json` を自動読み込み
- `docker login` 済みの任意レジストリをサポート

---

## 6. 設定ファイル（`.frizbee.yml`）

```yaml
ghactions:
  exclude:
    - slsa-framework/slsa-github-generator/.github/workflows/generator_generic_slsa3.yml@.*
  exclude_branches:
    - main
    - master
images:
  exclude_images:
    - scratch
  exclude_tags:
    - latest
```

### 既定設定（`DefaultConfig()`）
```go
&Config{
    GHActions: GHActions{
        Filter: Filter{
            ExcludeBranches: []string{"main", "master"},
        },
    },
    Images: Images{
        ImageFilter: ImageFilter{
            ExcludeImages: []string{"scratch"},
            ExcludeTags:   []string{"latest"},
        },
    },
}
```

### `MergeUserConfig` の安全性
- `scratch` は常に `ExcludeImages` へ強制追加され、上書き不能
- それ以外の既定値は強制されない。`exclude_branches`、`exclude_tags` はユーザーが空にできる

---

## 7. 参照スキップ処理

```go
res, err := r.ParseString(cmd.Context(), pathOrRef)
if errors.Is(err, interfaces.ErrReferenceSkipped) {
    fmt.Fprintln(cmd.OutOrStdout(), pathOrRef)  // そのまま出力、エラー扱いしない
    return nil
}
```

スキップはエラーではなく、名前付き sentinel（`ErrReferenceSkipped`）です。これにより呼び出し側は「この参照はピン不可だった」ケースと「実際の解決失敗」を区別できます。

---

## 8. サポート対象ファイル

| サブコマンド | 対象フィールド |
|---|---|
| `actions` | `.github/workflows/*.yml` 内の `uses:` |
| `image` | 任意 YAML ファイル内の `image:`（docker-compose、k8s manifest など） |

frizbee の `image` は dockerfile-pin より広範囲で、Dockerfile や docker-compose.yml に限定せず `image:` フィールドを持つ任意 YAML を対象にします。

---

## 9. 学び / 設計ノート

- **`GITHUB_TOKEN` 単一環境変数** は pinact の多段優先順位より単純。ただしマルチホスト（GHES なし）では柔軟性が低い。OSS には良い既定だが、GHES 前提の企業用途には不足。
- **`scratch` を常時除外** は単なる既定値ではなく、`MergeUserConfig` で実装された安全不変条件。`scratch` はレジストリ実体がないため、ピン留め不能という設計判断が妥当。
- **`latest` 既定除外** は合理的。`latest` は可変であり、ピン留めの意味が薄い。必要ならユーザーが既定を解除できる。
- **`exclude_branches: [main, master]`** は、呼び出し側自身のデフォルトブランチ上 reusable workflow を誤ってピンしないための明示設計。
- **`ErrReferenceSkipped` sentinel** は、解決パイプラインにおけるスキップ通知として boolean よりクリーン。
- **actions + image の単一ツール化** でツール分散を抑えられる一方、GitHub API と OCI 依存を同居させる必要がある。Seiton では resolver インターフェースを分離維持すべき。
- **公開 API に明示的な in-process cache が見えない**。dockerfile-pin の `CachedResolver` と異なり、frizbee のキャッシュ（ある場合）は replacer 内部。linter 統合では注入可能な明示キャッシュの方が望ましい。
- **`min-age` 相当の更新クールダウン機能はない**。`.references/frizbee/internal/cli/cli.go` の共通フラグは `dry-run` / `quiet` / `error` / `regex` / `platform` で、日数ベース更新フィルタは存在しない。制御は `.frizbee.yml` の `exclude_branches` / `exclude_tags` などの除外条件が中心。
