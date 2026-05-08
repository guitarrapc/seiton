# Seiton インストール方法 — 実装計画

本書は [docs/installation.md](../../docs/installation.md) に記載されている各インストール方法の実装計画を整理したもの。

## 現状

`docs/installation.md` および `README.md` には以下のインストール方法が記載されているが、**Prebuilt Binaries（手動ダウンロード）と Build from Source 以外はすべて未実装**。

| 方法 | ドキュメント記載 | 実装状態 |
|------|:---:|:---:|
| Prebuilt Binaries（手動 DL） | ✅ | ✅ release.yaml で自動ビルド・公開済み |
| Build from Source | ✅ | ✅ 動作する |
| Homebrew (macOS/Linux) | ✅ | ❌ tap リポジトリ・Formula 未作成 |
| Scoop (Windows) | ✅ | ❌ bucket リポジトリ・マニフェスト未作成 |
| Winget (Windows) | ✅ | ❌ winget-pkgs PR 未提出 |
| Docker (GHCR) | ✅ | ❌ Dockerfile 無し、GHCR push 未実装 |
| install.sh (CI) | ✅ | ❌ スクリプト未作成 |
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

> **注意**: `docs/installation.md` の Manual セクションでは `seiton-windows-x64.zip`, `seiton-macos-arm64.tar.gz`, `seiton-linux-x64.tar.gz` と記載しているが、実際のリリースアセット名は上記の通り異なる。ドキュメント修正が必要。

## ゴールと非ゴール

- **ゴール**: ドキュメントに記載した全チャネルを実装し、ユーザーが実際にインストールできるようにする。
- **非ゴール**: CLI 本体の機能変更。パッケージマネージャの公式リポジトリ（Homebrew core、Scoop Main bucket）への登録（将来検討）。

## 実装フェーズ

優先度とユーザーカバレッジに基づき、以下の順で実装する。

---

### フェーズ 1 — ドキュメント修正 + install.sh

**WHY**: 手動ダウンロードは既に動作するので、まずドキュメントのアセット名不一致を修正する。install.sh は CI ユーザーの即時ニーズを満たす最小実装。

#### 1-1. ドキュメントのアセット名修正

- `docs/installation.md` の Manual セクション内のファイル名を実際の release アセット名に合わせる。
  - `seiton-windows-x64.zip` → `seiton-win-amd64.zip`
  - `seiton-macos-arm64.tar.gz` → `seiton-osx-arm64.tar.gz`
  - `seiton-linux-x64.tar.gz` → `seiton-linux-amd64.tar.gz`

#### 1-2. install.sh の作成

- リポジトリルートまたは `scripts/` に `install.sh` を配置。
- release workflow で release アセットに同梱する。
- 要件:
  - OS/Arch を自動検出し、対応するアセットを GitHub Releases API からダウンロード。
  - `INSTALL_DIR` 環境変数でインストール先を変更可能（デフォルト `/usr/local/bin`）。
  - `VERSION` 環境変数で特定バージョンを指定可能（デフォルト `latest`）。
  - checksum 検証 (`checksums-sha256.txt` を利用)。
  - POSIX sh 互換（bash 非依存）。
- リリースアセット URL パターン: `https://github.com/guitarrapc/seiton/releases/latest/download/{artifact}`
- 参考実装: actionlint の `install.sh`, zizmor の installer

**完了条件**: `curl ... | sh` で Linux/macOS に seiton がインストールできる。

---

### フェーズ 2 — Homebrew tap

**WHY**: macOS/Linux ユーザーの標準インストール手段。tap 方式なら外部審査不要で自律的に運用できる。

#### 2-1. tap リポジトリの作成

- `guitarrapc/homebrew-tap` リポジトリを作成。
- Formula ファイル `Formula/seiton.rb` を配置。
  - `url` は GitHub Releases のアセット URL（OS/Arch で分岐）。
  - `sha256` はリリース時に自動更新。
  - `version` はタグから導出。
  - `def install`: tar 展開 → `bin.install "seiton"`

#### 2-2. リリース自動化

- `release.yaml` の release ジョブ完了後に Formula を自動更新するステップを追加。
- 方法の選択肢:
  - **A**: `mislav/bump-homebrew-formula-action` を利用（シンプル）。
  - **B**: release workflow 内で直接 `homebrew-tap` リポジトリへ commit/push（`GH_TOKEN` + Fine-grained PAT で）。
- macOS / Linux の両アーキテクチャ対応（`on_macos` / `on_linux` で URL 分岐）。

**完了条件**: `brew install guitarrapc/tap/seiton` でインストールできる。新リリース時に Formula が自動更新される。

---

### フェーズ 3 — Scoop bucket

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

- release workflow 完了後に scoop-bucket リポジトリのマニフェストを自動更新。
- Scoop の `autoupdate` + `checkver` が設定されていれば、Excavator bot が自動 PR を出すことも可能。

#### 3-3. docs/installation.md の更新

- Scoop セクションのコマンドを bucket 追加込みに:
  ```powershell
  scoop bucket add guitarrapc https://github.com/guitarrapc/scoop-bucket
  scoop install seiton
  ```

**完了条件**: `scoop install seiton` で Windows にインストールできる。

---

### フェーズ 4 — Docker イメージ (GHCR)

**WHY**: CI/CD パイプラインや Docker ベースのワークフローで利用したいユーザー向け。

#### 4-1. Dockerfile の作成

- NativeAOT のシングルバイナリなので `scratch` or `gcr.io/distroless/static-debian12` ベースで極小イメージ。
  ```dockerfile
  FROM scratch
  COPY seiton /seiton
  ENTRYPOINT ["/seiton"]
  ```
- マルチアーキテクチャ対応 (`linux/amd64`, `linux/arm64`)。

#### 4-2. release workflow への組み込み

- `release.yaml` に Docker ビルド・push ジョブを追加:
  - `docker/login-action` で GHCR 認証。
  - `docker/build-push-action` + `docker/setup-buildx-action` でマルチアーキ manifest push。
  - タグ付け: `ghcr.io/guitarrapc/seiton:latest`, `ghcr.io/guitarrapc/seiton:vX.Y.Z`
- permissions: `packages: write` を追加。

**完了条件**: `docker pull ghcr.io/guitarrapc/seiton:latest` → `docker run --rm -v "$PWD:/repo" ghcr.io/guitarrapc/seiton:latest /repo` で動作する。

---

### フェーズ 5 — Winget

**WHY**: Windows の公式パッケージ管理。ただし `microsoft/winget-pkgs` リポジトリへの PR が必要で審査があるため優先度は低い。

#### 5-1. Winget マニフェスト作成

- `winget-pkgs` リポジトリの規約に従いマニフェスト一式を作成:
  - `manifests/g/guitarrapc/seiton/{version}/` 配下に:
    - `guitarrapc.seiton.installer.yaml`
    - `guitarrapc.seiton.locale.en-US.yaml`
    - `guitarrapc.seiton.yaml` (version manifest)
- InstallerType: `zip` (展開後にパスへ配置) or `portable`。

#### 5-2. 自動化

- `vedantmgoyal9/winget-releaser` Action や `wingetcreate` CLI で release 時にPRを自動作成。

**完了条件**: `winget install guitarrapc.seiton` でインストールできる。

---

### フェーズ 6 — GitHub Action (オプション)

**WHY**: GitHub Actions ワークフロー内で seiton を直接ステップとして使えると利便性が高い。ただし本ツールの主用途が GitHub Actions YAML の lint であることを考えると、CI で走らせるニーズは高い。

#### 6-1. action.yml の作成

- リポジトリルートに `action.yml` を配置。
- Composite action として実装:
  - install.sh を利用してバイナリをセットアップ。
  - `seiton` コマンドを実行。
- 入力パラメータ:
  - `version`: インストールするバージョン（デフォルト `latest`）。
  - `args`: seiton に渡す追加引数。

#### 6-2. ドキュメント

- `docs/usage.md` に GitHub Actions での利用例を追記。

**完了条件**: ワークフロー内で `uses: guitarrapc/seiton@v1` として利用できる。

---

## フェーズ間の依存関係

```
フェーズ 1 (docs修正 + install.sh)
  ├── フェーズ 2 (Homebrew) ← 独立
  ├── フェーズ 3 (Scoop)    ← 独立
  ├── フェーズ 4 (Docker)   ← 独立
  ├── フェーズ 5 (Winget)   ← 独立
  └── フェーズ 6 (GitHub Action) ← install.sh に依存
```

フェーズ 2〜5 は互いに独立しており、並行して進められる。フェーズ 6 は install.sh を利用するためフェーズ 1 完了後に着手。

## リスクと考慮事項

| リスク | 影響 | 対策 |
|--------|------|------|
| Winget 審査の遅延・リジェクト | Windows ユーザーが winget で入れられない | Scoop を代替として先行提供。winget は安定版で再挑戦 |
| Homebrew Formula のアーキテクチャ分岐の複雑さ | macOS x64/arm64、Linux x64/arm64 の 4 パターン | `Hardware::CPU` ガードで分岐する標準パターンを採用 |
| Docker マルチアーキ manifest のビルド時間 | CI 時間増加 | publish ジョブのバイナリを流用し、Docker ビルド自体は COPY のみで高速 |
| install.sh の POSIX 互換性 | Alpine 等の一部環境で動かない | `#!/bin/sh` で書き、bashism を避ける。CI matrix で Alpine/Ubuntu/macOS をテスト |
| リリースアセット名変更の可能性 | 全チャネルの URL が壊れる | アセット名を変更する場合はすべてのチャネル（Formula, bucket, install.sh, docs）を同時更新する |
