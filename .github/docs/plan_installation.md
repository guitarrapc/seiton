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
| Winget (Windows) | ❌ | ❌ 見送り（実績不足で winget-pkgs 審査不可） |
| Docker (GHCR) | ✅ | ✅ Release ワークフローで linux/amd64・arm64 を push |
| mise | ❌ | ❌ 見送り（実績不足でレジストリ登録不可） |
| aqua | ❌ | ❌ 見送り（実績不足で aqua-registry 登録不可） |
| ダウンロード用スクリプト（curl \| sh 等） | ✅ | ✅ `scripts/download.sh`（main ブランチ） |
| GitHub CLI (`gh release download`) | ✅ | ✅ ドキュメントのみ（release workflow の SLSA attestation をそのまま利用） |
| GitHub Action | ❌ | ❌ `guitarrapc/seiton-action` リポ未作成 |
| dotnet tool (NuGet) | ❌ | ❌ NuGet パッケージ未公開 |

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
- **非ゴール**: CLI 本体の機能変更。パッケージマネージャの公式リポジトリ（Homebrew core、Scoop Main bucket）への登録（将来検討）。

## 実装フェーズ

優先度とユーザーカバレッジに基づき、以下の順で実装する。

---

### フェーズ 0 — ダウンロードスクリプト — 完了

**WHY**: `curl | bash` ワンライナーは CI やローカルセットアップで最も手軽。勝手にシステム領域へ配置せず、チェックサム検証を内蔵した download-only スクリプトにする。

#### 実装

- [`scripts/download.sh`](../../scripts/download.sh) を main ブランチに配置。
- 機能:
  - プラットフォーム自動判別（`uname -s` → `linux`/`osx`/`win`、`uname -m` → `amd64`/`arm64`）
  - デフォルトで最新リリースを取得（`--version` でバージョン指定可）
  - デフォルトでカレントディレクトリに展開（`--dir` で既存ディレクトリ指定可）
  - `checksums-sha256.txt` による SHA-256 検証
  - `gh` CLI が利用可能な場合は SLSA build provenance 検証も実行（ベストエフォート）
  - `sudo` を使わず、PATH 変更も自動で行わない
- セキュリティ考慮:
  - `set -euo pipefail` で安全に失敗
  - `curl --proto '=https' --tlsv1.2` で TLS を強制
  - tmpdir + EXIT trap で一時ファイルを確実に削除
  - JSON パース不要（`/releases/latest` エンドポイントを利用）

#### 利用方法

```sh
# 最新版をカレントディレクトリへダウンロード
curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/download.sh | bash

# バージョン指定
curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/download.sh | bash -s -- --version 1.0.0

# 既存ディレクトリ指定
mkdir -p ./bin
curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/download.sh | bash -s -- --dir ./bin
```

**完了条件**: 上記ワンライナーで Linux/macOS に seiton バイナリをダウンロードでき、チェックサム検証が通る。

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

- リポジトリルート [`Dockerfile`](../../Dockerfile): `gcr.io/distroless/base-debian13:nonroot` + リリースビルド済み Linux AOT バイナリ（glibc）。マルチアーキは Buildx の `TARGETARCH` で `amd64` / `arm64` の `COPY` を切り替え。

#### 4-2. release workflow（実装済み）

- [`.github/workflows/release.yaml`](../../.github/workflows/release.yaml) の **`docker` ジョブ**（`needs: validate, publish`）:
  - `seiton-linux-{amd64,arm64}.tar.gz` を展開して build context を組み立て
  - `docker/setup-qemu-action`, `docker/setup-buildx-action`, `docker/login-action`（GHCR）, `docker/build-push-action`
  - タグ: `:latest`, `:<Version>`, `:<tag>`（例 `v0.9.18`）
  - ジョブ権限: `packages: write`

**完了条件**: `docker pull ghcr.io/<owner>/<repo>:latest` → `docker run --rm -v "$PWD:/repo:ro" ... /repo` で動作する（`<owner>/<repo>` は小文字の GitHub リポジトリパス）。

---

### フェーズ 5 — mise — 見送り

**見送り理由**: mise registry への登録にはツールとしての実績が必要であり、現時点では登録できない。十分な利用実績が得られた段階で再検討する。

---

### フェーズ 6 — aqua — 見送り

**見送り理由**: aqua-registry への登録にはツールとしての実績が必要であり、現時点では登録できない。十分な利用実績が得られた段階で再検討する。

---

### フェーズ 7 — Winget — 見送り

**見送り理由**: `microsoft/winget-pkgs` リポジトリへの PR には審査があり、ツールとしての実績が不足している現時点では通らない可能性が高い。Scoop で Windows ユーザーをカバーできるため、十分な利用実績が得られた段階で再検討する。

---

### フェーズ 8 — GitHub Action (オプション)

**WHY**: GitHub Actions ワークフロー内で seiton を直接ステップとして使えると利便性が高い。ただし本ツールの主用途が GitHub Actions YAML の lint であることを考えると、CI で走らせるニーズは高い。

**方針: 別リポジトリ (`guitarrapc/seiton-action`) で管理する。**

同一リポジトリに `action.yml` を置く案もあるが、以下の理由から別リポとする:

- **タグ衝突の回避**: CLI は固定 semver タグ (`v1.0.0`) でリリースするが、Action の慣例は floating major タグ (`v1`) を最新 `v1.x.x` に追従させる。同一リポでは両タグ体系が干渉しリリースフローが複雑化する。
- **チェックアウトの軽量化**: `uses:` で参照するとリポジトリ全体がチェックアウトされる。CLI ソースコードを含むリポは不必要に大きい。
- **seiton 自身との混乱回避**: seiton は `action.yml` を lint するツールなので、ルートに実際の `action.yml` があると開発時に紛らわしい。
- **業界標準**: ツール系 Action の一般的なパターン（`reviewdog/action-*` 等）と合致する。

#### 8-1. seiton-action リポジトリの作成

- `guitarrapc/seiton-action` リポジトリを作成。
- `action.yml` を配置。Composite action として実装:
  - `scripts/download.sh` を利用して該当 OS の seiton バイナリを取得・検証。
  - `seiton` コマンドを実行。
- 入力パラメータ:
  - `version`: インストールするバージョン（デフォルト `latest`）。
  - `args`: seiton に渡す追加引数。
- タグ運用: リリース時に `v1.0.0` タグを打ち、`v1` floating tag を追従させる。

#### 8-2. リリース連動

- seiton 本体のリリース時に seiton-action 側のデフォルトバージョンを更新する（手動 or workflow_dispatch）。

#### 8-3. ドキュメント

- `docs/usage.md` に GitHub Actions での利用例を追記。

**完了条件**: ワークフロー内で `uses: guitarrapc/seiton-action@v1` として利用できる。

---

### フェーズ 9 — dotnet tool (NuGet)

**WHY**: .NET SDK を持つ開発者や CI 環境では `dotnet tool install -g seiton` が最も手軽。NuGet は自己申請で審査不要のため、mise/aqua/winget と異なり即座に公開できる。

**注意**: dotnet tool は framework-dependent 実行のため NativeAOT バイナリより起動が遅い。また利用者に .NET SDK 10.0+ が必要。NativeAOT バイナリが不要な環境（CI で .NET SDK が既にある、.NET 開発者のローカル環境）向け。

#### 9-1. csproj の変更

- `src/Seiton/Seiton.csproj` に以下を追加:
  ```xml
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>seiton</ToolCommandName>
  <IsPackable>true</IsPackable>
  <PackageId>seiton</PackageId>
  ```
- `Directory.Build.props` の `<IsPackable>false</IsPackable>` はソリューション全体のデフォルトなので、Seiton.csproj 側で上書きする。
- `PublishAot=true` は `dotnet pack` 時には影響しない（tool は framework-dependent）。

#### 9-2. NuGet パッケージメタデータ

- `Seiton.csproj` または `Directory.Build.props` にメタデータを追加:
  - `PackageDescription`, `PackageTags`, `PackageLicenseExpression` (MIT), `PackageProjectUrl`, `RepositoryUrl`, `PackageReadmeFile`

#### 9-3. リリース自動化

- release workflow に `dotnet pack` + `dotnet nuget push` ステップを追加（publish ジョブ後または release ジョブ内）。
- NuGet API キーを GitHub Secrets (`NUGET_API_KEY`) に登録。

#### 9-4. ドキュメント

- `docs/installation.md` に dotnet tool セクションを追加:
  ```sh
  dotnet tool install -g seiton
  ```
- `README.md` の Quick Start にも追記。

**完了条件**: `dotnet tool install -g seiton` でインストールでき、`seiton version` が動作する。

---

## フェーズ間の依存関係

```
フェーズ 0 (download.sh) ← 独立（完了）
フェーズ 1 (docs / アセット名)
  ├── フェーズ 2 (Homebrew) ← 独立
  ├── フェーズ 3 (Scoop)    ← 独立（完了）
  ├── フェーズ 4 (Docker)   ← 独立（完了）
  ├── フェーズ 5 (mise)     ← 見送り
  ├── フェーズ 6 (aqua)     ← 見送り
  ├── フェーズ 7 (Winget)   ← 見送り
  ├── フェーズ 8 (GitHub Action) ← 独立（別リポ seiton-action）
  └── フェーズ 9 (dotnet tool) ← 独立（NuGet、審査不要）
```

フェーズ 2〜8 は互いに独立しており、並行して進められる。

## リスクと考慮事項

| リスク | 影響 | 対策 |
|--------|------|------|
| Homebrew Formula のアーキテクチャ分岐の複雑さ | macOS x64/arm64、Linux x64/arm64 の 4 パターン | `Hardware::CPU` ガードで分岐する標準パターンを採用 |
| Docker マルチアーキ manifest のビルド時間 | CI 時間増加 | publish ジョブのバイナリを流用し、Docker ビルド自体は COPY のみで高速 |
| リリースアセット名変更の可能性 | 全チャネルの URL が壊れる | アセット名を変更する場合はすべてのチャネル（Formula, bucket, docs, 利用例 YAML）を同時更新する |
| mise / aqua / Winget 登録の前提条件 | 利用実績がないと審査・登録が通らない | 十分な実績を得た段階で再検討。それまでは download.sh / Homebrew / Scoop / Docker でカバー |
