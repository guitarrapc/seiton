# Homebrew（メンテナ向け）

エンドユーザー向け手順は [docs/installation.md](../../docs/installation.md) の Homebrew 節を参照。

## 方針

- **専用の `homebrew-tap` リポジトリは使わない。**
- 本リポジトリ直下に `Formula/seiton.rb` を置き、ユーザーは **`brew tap guitarrapc/seiton https://github.com/guitarrapc/seiton`** でこのリポを明示的に tap する。
- **リリースごと**に `version` と各アーキテクチャの `url` / `sha256` を直す必要がある。GitHub Release が **Published** になったとき [.github/workflows/homebrew-formula.yaml](../../.github/workflows/homebrew-formula.yaml) が `checksums-sha256.txt` から Formula を再生成し、**デフォルトブランチへコミット**する。

## 権限

- 既定は **`GITHUB_TOKEN`（`contents: write`）** で push。

## スクリプト

| ファイル | 役割 |
|---------|------|
| [scripts/render-homebrew-seiton-formula.sh](../../scripts/render-homebrew-seiton-formula.sh) | `checksums-sha256.txt` から `Formula/seiton.rb` を標準出力へ |
| [scripts/commit-homebrew-formula.sh](../../scripts/commit-homebrew-formula.sh) | リポジトリルートで実行し、`Formula/seiton.rb` を書いて commit / push |
| [scripts/test-homebrew-formula-render.sh](../../scripts/test-homebrew-formula-render.sh) | フィクスチャでレンダリング検証（CI） |

## 手元で実行（push する場合は認証済み git または `SKIP_PUSH=1` で検証のみ）

```bash
cd /path/to/seiton
export GITHUB_REPOSITORY=guitarrapc/seiton
export SEITON_TAG=v0.9.19
export SEITON_VERSION=0.9.19
export CHECKSUMS_FILE=./checksums-sha256.txt
# export SKIP_PUSH=1   # コミットまでで止める
bash scripts/commit-homebrew-formula.sh
```

## Draft

Release が **Draft のまま**では `release: published` が飛ばないため、Formula は更新されない。公開後にダウンロード URL も有効になる。
