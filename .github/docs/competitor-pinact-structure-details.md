# pinact — 競合ツール構造詳細

> 参照: `.references/pinact/`
> 作者: suzuki-shunsuke
> 目的: GitHub Actions と Reusable Workflows を完全な commit SHA にピン留めし、必要に応じてバージョン更新と注釈検証を行う。

---

## 1. 概要

pinact は、GitHub workflow と composite action ファイルを書き換え、すべての `uses:` 参照を 40 文字の commit SHA にピン留めする CLI ツールです。SHA の横には人間可読のタグコメント（例: `# v4.0.0`）も追加します。さらに、既にピン済みの参照を最新 SHA へ **更新** したり、既存注釈が実 SHA と一致するかを **検証** したりできます。

pinact は `--min-age` / `PINACT_MIN_AGE`（既定 `0` = 無効）による経過日数ベースの更新フィルタもサポートします。このフィルタは、更新先バージョン選択時（`-u` 経路）に適用され、単一入力 ref の解決後ゲートとしては使われません。

主なユースケース:
- `pinact run` — pin/update
- `pinact check` — 検証のみ（書き込みなし）
- `pinact create-review` — 未ピンの actions に対する GitHub PR review コメント投稿

---

## 2. アーキテクチャ

```
cmd/pinact/main.go
  └─ pkg/cli/           アプリ入口、フラグ解析、設定読み込み
  └─ pkg/di/            依存性注入、環境変数配線
  └─ pkg/config/        設定ファイル（.pinact.yaml）読み込みと検証
  └─ pkg/github/        GitHub API クライアント群
  │   ├─ github.go      クライアント生成、OAuth2 設定
  │   ├─ service.go     ClientResolver — GHES と github.com の振り分け
  │   ├─ registry.go    Repositories API を使った commit SHA 解決
  │   └─ keyring.go     OS keyring トークン保存
  └─ pkg/controller/    pin/check/review の中核オーケストレーション
  └─ pkg/sarif/         SARIF 出力フォーマッタ
```

---

## 3. 解決戦略 — GitHub Actions SHA

### 使用 API
- `GET /repos/{owner}/{repo}/git/refs/{ref}`（tags / branches）
- `GET /repos/{owner}/{repo}/releases`
- `GET /repos/{owner}/{repo}/tags`
- `GET /repos/{owner}/{repo}/commits/{sha}` — tag クールダウン（`--min-age`）判定用

### 解決フロー
1. `uses: owner/repo@ref`（または `owner/repo/.github/workflows/file.yml@ref`）を解析
2. GitHub Repositories API で ref を解決
3. ref が annotated tag の場合、`object.sha` を辿って commit object へ到達
4. `@ref` を `@<commit-sha> # ref` に置換

### `--min-age` を用いた更新先選択

`--min-age` は、pinact が新しい tag/version を選ぶ更新フロー（`pinact run -u ...`）にのみ影響します。

1. `min-age > 0` のとき `cutoff = now - minAgeDays` を計算
2. まず releases を問い合わせ、クールダウン条件を満たす候補のみ保持:
  - 現在バージョンが stable の場合、prerelease はスキップ
  - `release.published_at > cutoff` はスキップ
3. 適格 release がなければ tags を問い合わせ、クールダウン条件を満たす候補のみ保持:
  - release に既出の tag はスキップ
  - 現在バージョンが stable の場合、prerelease tag はスキップ
  - 各 tag について `commit.committer.date` を取得し、`date > cutoff` はスキップ
  - commit 日付取得に失敗した tag は保守的にスキップ
4. 残った候補から最高バージョンを選択（semver 優先、次に文字列フォールバック）

これは単一 ref の事後チェックではなく、「release/tag 候補を列挙してフィルタし、最適候補を選ぶ」モデルです。

### キャッシュ
- プロセス内キャッシュが wrapper service に存在:
  - `RepositoriesServiceImpl.Commits` / `Tags` / `Releases`
  - `GitServiceImpl.Commits`
- リポジトリホスト振り分け（GHES vs github.com）も `ClientResolver.repoHosts` でプロセス内キャッシュ

---

## 4. 認証

### トークン優先順位（GitHub.com）
```
PINACT_GITHUB_TOKEN  →  GITHUB_TOKEN  →  OS Keyring  →  ghtkn App Token  →  unauthenticated
```

出典: `pkg/di/env.go`
```go
s.GitHubToken = getEnv("PINACT_GITHUB_TOKEN")
if s.GitHubToken == "" {
    s.GitHubToken = getEnv("GITHUB_TOKEN")
}
```

### GHES トークン優先順位
```
PINACT_GHES_TOKEN  →  GHES_TOKEN  →  GITHUB_TOKEN_ENTERPRISE  →  GITHUB_ENTERPRISE_TOKEN
```

### OS Keyring
- `PINACT_KEYRING_ENABLED=true` で有効化
- Windows Credential Manager / macOS Keychain / GNOME Keyring を利用
- `pinact token set` / `pinact token get` で管理

### ghtkn 連携
- `PINACT_GHTKN=true` で有効化
- `ghtkn` CLI を介して GitHub App User Access Token をオンデマンド生成

### 非認証フォールバック
- トークンが無い場合、GitHub REST API は非認証で呼び出し
- レート制限は低い（認証 5000 req/hour に対し、非認証 60 req/hour）

---

## 5. GitHub Enterprise Server（GHES）対応

`ClientResolver`（`pkg/github/service.go`）は、API 呼び出しを github.com または GHES インスタンスへ振り分けます。

```go
type ClientResolver struct {
    defaultRepoService  RepositoriesService  // github.com
    ghesRepoService     RepositoriesService  // GHES
    repoHosts           map[string]repoHost  // cache
    fallback            bool                 // GHES 非該当時に github.com へフォールバック
}
```

- 設定: `.pinact.yaml` → `ghes.api_url` + `ghes.fallback`
- 環境変数: `GHES_API_URL` は設定を上書き
- `fallback: true` の場合、GHES に見つからないリポジトリは github.com で解決

---

## 6. 設定ファイル（`.pinact.yaml`）

スキーマ version 3（v2 は廃止）:

```yaml
version: 3
files:
  - pattern: ".github/workflows/*.yaml"
ignore_actions:
  - name: slsa-framework/slsa-github-generator/\.github/workflows/generator_generic_slsa3\.yml
    ref: .*
  - name: peaceiris/.*
    ref: .*
ghes:
  api_url: https://ghes.example.com
  fallback: false
separator: " # "
```

- `files` — 対象ファイルの glob パターン（CLI 位置引数で上書き可能）
- `ignore_actions` — name/ref の regex パターン
- `ghes` — GHES 設定
- `separator` — SHA とタグコメント間の区切り文字（既定 ` # `）

`min-age` は `.pinact.yaml` には含まれず、CLI フラグ（`--min-age`）または環境変数（`PINACT_MIN_AGE`）で指定します。

---

## 7. 検証モード（`pinact check`）

- 既存の `uses: owner/repo@sha # tag` 注釈を読み取る
- GitHub API で tag を解決し、SHA と一致するか確認
- 不一致をエラーとして報告（ファイルは書き換えない）
- 不一致が 1 件でもあれば終了コード 1

エラーコード `001`: バージョン注釈不一致（`docs/codes/001.md` に記載）。

---

## 8. Reusable Workflow 対応

pinact は次の両方を扱います:
- Actions: `owner/repo@ref`
- Reusable Workflows: `owner/repo/.github/workflows/file.yml@ref`

どちらも Repositories API で同一方式で解決され、パス接頭辞の有無で SHA 解決メカニズムは変わりません。

---

## 9. 出力形式

- stdout への diff 出力（既定）
- CI 統合向け SARIF: `--format sarif`
- GitHub PR review コメント: `pinact create-review`

---

## 10. 学び / 設計ノート

- **ツール専用環境変数**（`PINACT_GITHUB_TOKEN`）を汎用 `GITHUB_TOKEN` より優先するため、同一環境でツールごとに別トークンを使い分けられる。
- **イメージピン留めなし**。pinact は GitHub Actions 専用であり、Docker イメージ digest 解決はスコープ外。
- **`--min-age` は更新先候補のフィルタ**。release/tag 候補を先に絞り込んでから更新先バージョンを選び、その後で SHA 解決する。
- **GHES フォールバック** は意図的設計。組織内で共通 action を GHES にホストしつつ、公開 github.com action も併用するハイブリッド運用を支える。
- **regex ベース `ignore_actions`** は glob より柔軟だが、マッチング複雑性は上がる。

---

## 11. pinact と Seiton（`min-age-days`）比較

| 観点 | pinact（`--min-age`） | Seiton（`fix.pinning.min-age-days`） |
|---|---|---|
| 既定値 | `0`（無効） | `14`（有効） |
| 設定面 | CLI/環境変数のみ（`--min-age`, `PINACT_MIN_AGE`） | 設定ファイルキー（`fix.pinning.min-age-days`） |
| 発動ポイント | 更新フローのみ（`run -u`） | Pin remediation の解決フロー |
| 選択モデル | release/tag 候補を列挙し、年齢でフィルタして最適を選択 | version 形式 ref（`vN`, `vN.M`, `vN.M.P`）に対して同一バージョン系列の release/tag 候補を列挙し、適格な最適候補を選択。非 version ref は直接解決 |
| 候補/ターゲットが新しすぎる場合 | その候補をスキップして、より古い適格候補を探索継続 | 新しすぎる候補をスキップして継続。適格候補が尽きた場合のみ skip（`null`） |
| `0` の意味 | クールダウンフィルタなし | 年齢制約なし |

補足（競合比較）:
- `.references/dockerfile-pin` と `.references/frizbee` には、pinact の `--min-age` のような「日数ベースで更新候補を絞る」機能は確認できない。
- dockerfile-pin は `--update` と ignore パターン（`--ignore-images` / `.dockerfile-pin.yaml`）中心、frizbee は `exclude_branches` / `exclude_tags` などの除外設定中心で制御する。
