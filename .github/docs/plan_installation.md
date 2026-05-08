# Seiton インストール方法 — 実装計画

本書は [docs/installation.md](../../docs/installation.md) に記載されている各インストール方法の実装計画を整理したもの。

## 現状

`docs/installation.md` および `README.md` には以下のインストール方法が記載されているが、**Prebuilt Binaries（手動ダウンロード）と Build from Source 以外はすべて未実装**。

| 方法 | ドキュメント記載 | 実装状態 |
|------|:---:|:---:|
| Prebuilt Binaries（手動 DL） | ✅ | ✅ release.yaml で自動ビルド・公開済み |
| Build from Source | ✅ | ✅ 動作する |
| Homebrew (macOS/Linux) | ✅ | △ 本体リポ `Formula/seiton.rb`・Release publish 時に CI が更新 |
| Scoop (Windows) | ✅ | ✅ [`guitarrapc/scoop-bucket`](https://github.com/guitarrapc/scoop-bucket)、Excavator 更新 |
| Winget (Windows) | ✅ | ❌ winget-pkgs PR 未提出 |
| Docker (GHCR) | ✅ | ✅ Release ワークフローで linux/amd64・arm64 を push |
| mise | ❌ | ❌ レジストリ登録・ドキュメント未対応 |
| aqua | ❌ | ❌ aqua-registry への登録・ドキュメント未対応 |
| ダウンロード／インストール用スクリプト（curl \| sh 等） | — | **非サポート**（公式には提供しない） |
| GitHub Action | ❌ | ❌ action.yml 未作成 |

### リリースアセット名（現行 release.yaml）

```
seiton-linux-amd64.tar.gz
seiton-linux-arm64.tar.gz
seiton-osx-amd64.tar.gz
seiton-osx-arm64.tar.gz
seiton-win-amd64.zip
seiton-win-arm64.zip
checksums-sha256.txt
```

> **注意（反映済み）**: Manual 記載のアセット名は `docs/installation.md` で実リリース名に合わせてある。

## ゴールと非ゴール

- **ゴール**: ドキュメントに記載した全チャネルを実装し、ユーザーが実際にインストールできるようにする。
- **非ゴール**: CLI 本体の機能変更。パッケージマネージャの公式リポジトリ（Homebrew core、Scoop Main bucket）への登録（将来検討）。**インストール用シェルスクリプトの配布**（セキュリティ・運用の方針により見送り）。

## 実装フェーズ

優先度とユーザーカバレッジに基づき、以下の順で実装する。

---

### フェーズ 1 — ドキュメント（リリースアセット名の整合）

**WHY**: 手動ダウンロードは既に動作する。文書上のファイル名を実リリースと一致させ、CI 例などは **リリースアーカイブを直接取得する**形で示す。

#### 1-1. ドキュメントのアセット名修正

- `docs/installation.md` の Manual セクション内のファイル名を実際の release アセット名に合わせる。
  - `seiton-windows-x64.zip` → `seiton-win-amd64.zip`
  - `seiton-macos-arm64.tar.gz` → `seiton-osx-arm64.tar.gz`
  - `seiton-linux-x64.tar.gz` → `seiton-linux-amd64.tar.gz`

**完了条件**: 利用者が Releases のアセット名とドキュメントを読み違えない。

---

### フェーズ 2 — Homebrew tap

**WHY**: macOS/Linux ユーザーの標準インストール手段。tap 方式なら外部審査不要で自律的に運用できる。

#### 2-1. 同一リポに Formula（メンテナ・運用）

- **専用 `homebrew-tap` は不要。** `Formula/seiton.rb` を **Seiton 本体リポジトリ**に置き、`brew tap owner/repo` でそのまま指す（[rhysd/actionlint](https://github.com/rhysd/actionlint) の Cask と同様の「ツール repo を tap する」パターン）。
- ブランチ保護で `GITHUB_TOKEN` の push が弾かれるときは **PAT `HOMEBREW_FORMULA_PUSH_TOKEN`** を用意（[packaging/homebrew/README.md](../../packaging/homebrew/README.md)）。

#### 2-2. Formula とリリース連動（実装済み）

- リリースのたびに Formula を更新する必要があるため、Release **Published** 時に `checksums-sha256.txt` から `Formula/seiton.rb` を再生成し、**デフォルトブランチへコミット**する。
- 実装:
  - [scripts/render-homebrew-seiton-formula.sh](../../scripts/render-homebrew-seiton-formula.sh) — Linux/macOS × amd64/arm64
  - [scripts/commit-homebrew-formula.sh](../../scripts/commit-homebrew-formula.sh) — 本体リポで commit / push
  - [.github/workflows/homebrew-formula.yaml](../../.github/workflows/homebrew-formula.yaml) — `release: types: [published]`

**完了条件**: `brew tap <owner>/seiton` → `brew install seiton` が動き、**Release を Publish するたび** Formula が本体 `main`（等）に更新される。

---

### フェーズ 3 — Scoop bucket - 完了

**WHY**: Windows ユーザーの標準パッケージ管理の一つ。JSON マニフェスト 1 ファイルで済み実装コストが低い。

#### 3-1. bucket リポジトリの作成

- `guitarrapc/scoop-bucket` リポジトリを作成。
- `bucket/seiton.json` を配置:
  ```json
  {
    "version": "x.y.z",
    "architecture": {
      "64bit": {
        "url": "https://github.com/guitarrapc/seiton/releases/download/vx.y.z/seiton-win-amd64.zip",
        "hash": "sha256:..."
      },
      "arm64": {
        "url": "https://github.com/guitarrapc/seiton/releases/download/vx.y.z/seiton-win-arm64.zip",
        "hash": "sha256:..."
      }
    },
    "bin": "seiton.exe",
    "checkver": { "github": "https://github.com/guitarrapc/seiton" },
    "autoupdate": {
      "architecture": {
        "64bit": { "url": "https://github.com/guitarrapc/seiton/releases/download/v$version/seiton-win-amd64.zip" },
        "arm64": { "url": "https://github.com/guitarrapc/seiton/releases/download/v$version/seiton-win-arm64.zip" }
      }
    }
  }
  ```

#### 3-2. リリース自動化

- Scoop の `autoupdate` + `checkver` が設定されており、Excavator bot が自動更新

#### 3-3. docs/installation.md の更新

- Scoop セクションのコマンドを bucket 追加込みに:
  ```powershell
  scoop bucket add guitarrapc https://github.com/guitarrapc/scoop-bucket
  scoop install seiton
  ```

**完了条件**: `scoop install seiton` で Windows にインストールできる。

---

### フェーズ 4 — Docker イメージ (GHCR) — 完了

**WHY**: CI/CD パイプラインや Docker ベースのワークフローで利用したいユーザー向け。

#### 4-1. Dockerfile（実装済み）

- リポジトリルート [`Dockerfile`](../../Dockerfile): `gcr.io/distroless/base-debian12` + リリースビルド済み Linux AOT バイナリ（glibc）。マルチアーキは Buildx の `TARGETARCH` で `amd64` / `arm64` の `COPY` を切り替え。

#### 4-2. release workflow（実装済み）

- [`.github/workflows/release.yaml`](../../.github/workflows/release.yaml) の **`docker` ジョブ**（`needs: validate, publish`）:
  - `seiton-linux-{amd64,arm64}.tar.gz` を展開して build context を組み立て
  - `docker/setup-qemu-action`, `docker/setup-buildx-action`, `docker/login-action`（GHCR）, `docker/build-push-action`
  - タグ: `:latest`, `:<Version>`, `:<tag>`（例 `v0.9.4`）
  - ジョブ権限: `packages: write`

**完了条件**: `docker pull ghcr.io/<owner>/<repo>:latest` → `docker run --rm -v "$PWD:/repo:ro" ... /repo` で動作する（`<owner>/<repo>` は小文字の GitHub リポジトリパス）。

---

### フェーズ 5 — mise

**WHY**: [mise](https://mise.jdx.dev/)（旧 rtx）は言語ランタイムと CLI を一元管理する用途で普及しており、GitHub Releases 由来のバイナリをバージョン固定しやすい。

#### 5-1. レジストリ／バックエンド

- **推奨**: [mise registry](https://github.com/jdx/mise/tree/main/registry)（または現行の登録フロー）に **Seiton** を追加し、リリース資産名（`seiton-{os}-{arch}.tar.gz` / `seiton-win-*.zip`）と一致する取得 URL・正規表現を定義する。
- 代替・併用: `mise.toml` / `config.toml` で **github backend** や既存の汎用プラグイン（[GitHub Release 型のテンプレート](https://mise.jdx.dev/)に準拠）を文書化する。

#### 5-2. 検証とドキュメント

- macOS / Linux / Windows（利用可能なら）で `mise install seiton`（または `mise use -g seiton@x.y.z`）を確認。
- [docs/installation.md](../../docs/installation.md) に mise セクションを追加。

**完了条件**: ドキュメントどおりに `mise` で Seiton をインストール・バージョン切替できる。

---

### フェーズ 6 — aqua

**WHY**: [aqua](https://aquaproj.github.io/) は CLI を宣言的に固定し、CI とローカルで同じバージョンを再現しやすい（`aqua.yaml` + `aqua install`）。

#### 6-1. aqua-registry

- [aquaproj/aqua-registry](https://github.com/aquaproj/aqua-registry) に **パッケージ定義**を追加する（`github_release` 等）。アセット:
  - Linux: `seiton-linux-amd64.tar.gz` / `seiton-linux-arm64.tar.gz`
  - macOS: `seiton-osx-amd64.tar.gz` / `seiton-osx-arm64.tar.gz`
  - Windows: `seiton-win-amd64.zip` / `seiton-win-arm64.zip`
- 実行ファイル名: Linux/macOS は `seiton`、Windows は `seiton.exe`（registry の `files` / `replacements` で整合）。

#### 6-2. 検証とドキュメント

- `aqua install`（または `aqua i`）で導入確認（パッケージ名・インデックスは registry のマージ後の定義に従う）。

**完了条件**: 公開された aqua-registry のパッケージ名どおり `aqua install` で Seiton を導入できる。

---

### フェーズ 7 — Winget

**WHY**: Windows の公式パッケージ管理。ただし `microsoft/winget-pkgs` リポジトリへの PR が必要で審査があるため優先度は低い。

#### 7-1. Winget マニフェスト作成

- `winget-pkgs` リポジトリの規約に従いマニフェスト一式を作成:
  - `manifests/g/guitarrapc/seiton/{version}/` 配下に:
    - `guitarrapc.seiton.installer.yaml`
    - `guitarrapc.seiton.locale.en-US.yaml`
    - `guitarrapc.seiton.yaml` (version manifest)
- InstallerType: `zip` (展開後にパスへ配置) or `portable`。

#### 7-2. 自動化

- `vedantmgoyal9/winget-releaser` Action や `wingetcreate` CLI で release 時にPRを自動作成。

**完了条件**: `winget install guitarrapc.seiton` でインストールできる。

---

### フェーズ 8 — GitHub Action (オプション)

**WHY**: GitHub Actions ワークフロー内で seiton を直接ステップとして使えると利便性が高い。ただし本ツールの主用途が GitHub Actions YAML の lint であることを考えると、CI で走らせるニーズは高い。

#### 8-1. action.yml の作成

- リポジトリルートに `action.yml` を配置。
- Composite action として実装:
  - リリースから該当 OS のアーカイブを取得（例: `gh release download` または `curl` + tar／zip）。

    推奨はワークフロー内で明示的に取得する手順と同様に、**検証可能なステップ**にすること（公式インストールスクリプトは提供しない）。
  - `seiton` コマンドを実行。
- 入力パラメータ:
  - `version`: インストールするバージョン（デフォルト `latest`）。
  - `args`: seiton に渡す追加引数。

#### 8-2. ドキュメント

- `docs/usage.md` に GitHub Actions での利用例を追記。

**完了条件**: ワークフロー内で `uses: guitarrapc/seiton@v1` として利用できる。

---

## フェーズ間の依存関係

```
フェーズ 1 (docs / アセット名)
  ├── フェーズ 2 (Homebrew) ← 独立
  ├── フェーズ 3 (Scoop)    ← 独立
  ├── フェーズ 4 (Docker)   ← 独立
  ├── フェーズ 5 (mise)     ← 独立（レジストリ／ドキュメント）
  ├── フェーズ 6 (aqua)     ← 独立（aqua-registry）
  ├── フェーズ 7 (Winget)   ← 独立
  └── フェーズ 8 (GitHub Action) ← 独立（専用スクリプト不要）
```

フェーズ 2〜8 は互いに独立しており、並行して進められる。

## リスクと考慮事項

| リスク | 影響 | 対策 |
|--------|------|------|
| Winget 審査の遅延・リジェクト | Windows ユーザーが winget で入れられない | Scoop を代替として先行提供。winget は安定版で再挑戦 |
| Homebrew Formula のアーキテクチャ分岐の複雑さ | macOS x64/arm64、Linux x64/arm64 の 4 パターン | `Hardware::CPU` ガードで分岐する標準パターンを採用 |
| Docker マルチアーキ manifest のビルド時間 | CI 時間増加 | publish ジョブのバイナリを流用し、Docker ビルド自体は COPY のみで高速 |
| mise / aqua レジストリのレビュー・命名規約 | マージまで時間がかかる、却下の可能性 | 事前に既存パッケージ（同様の GitHub Release 配布 CLI）を参照し、PR 説明にアセット名表を添付 |
| リリースアセット名変更の可能性 | 全チャネルの URL が壊れる | アセット名を変更する場合はすべてのチャネル（Formula, bucket, docs, 利用例 YAML、mise / aqua 定義）を同時更新する |
