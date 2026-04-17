# dockerfile-pin — 競合ツール構造詳細

> 参照: `.references/dockerfile-pin/`
> 作者: azu
> 目的: Dockerfile、docker-compose.yml、GitHub Actions ファイル内の Docker イメージ参照に `@sha256:<digest>` を付与する。

---

## 1. 概要

dockerfile-pin は、OCI イメージの digest を解決し、ソースファイルへ直接ピン留めする CLI ツールです。対象は 3 種類のファイルです: Dockerfile（`FROM` 行）、docker-compose.yml（`image:` フィールド）、GitHub Actions（container/service の `image:` フィールドと `uses: docker://` 参照）。GitHub Actions の `uses: owner/repo@ref` はピン留めしません。

主なユースケース:
- `dockerfile-pin run` — 既定は dry-run。適用は `--write`
- `dockerfile-pin check` — digest の存在とレジストリ上の実在を検証
- `dockerfile-pin run --update` — 既にピン済みの digest を更新

---

## 2. アーキテクチャ

```
cmd/
  root.go              Cobra のルートコマンド、グローバルフラグ
internal/
  resolver/
    resolver.go        CraneResolver + CachedResolver (go-containerregistry)
    resolver_test.go
  dockerfile/
    parse.go           Dockerfile パーサー（FROM 行抽出）
    rewrite.go         テキストへの digest 挿入
  compose/             docker-compose.yml のパースと書き換え
  actions/             GitHub Actions YAML の image フィールドのパースと書き換え
```

---

## 3. 解決戦略 — OCI イメージ Digest

### 使用ライブラリ
[`google/go-containerregistry`](https://github.com/google/go-containerregistry)（`crane` ライブラリ系パターン）

### 使用 API
`remote.Head(ref, ...)` — OCI Distribution `HEAD /v2/{name}/manifests/{reference}`

マニフェスト本体を取得せずに、`sha256:<hex>` 形式の manifest digest を返します。

### Resolver インターフェース
```go
type DigestResolver interface {
    Resolve(ctx context.Context, imageRef string) (string, error)
    Exists(ctx context.Context, imageRef string) (bool, error)
}
```

### `CraneResolver` 実装
```go
func (r *CraneResolver) Resolve(ctx context.Context, imageRef string) (string, error) {
    ref, _ := name.ParseReference(imageRef)
    desc, _ := remote.Head(ref,
        remote.WithAuthFromKeychain(authn.DefaultKeychain),
        remote.WithContext(reqCtx))
    return desc.Digest.String(), nil
}
```

- リクエスト単位タイムアウト: **30 秒**（ハードコード定数 `perRequestTimeout`）
- 認証: `authn.DefaultKeychain`（§4 参照）

### `CachedResolver` ラッパー
```go
type CachedResolver struct {
    inner        DigestResolver
    resolveCache map[string]resolveEntry   // imageRef → {digest, err}
    existsCache  map[string]existsEntry    // imageRef → {exists, err}
    mu           sync.RWMutex
}
```

- CLI 実行単位のプロセス内インメモリキャッシュ
- `Resolve` と `Exists` の結果を別キャッシュで保持
- `sync.RWMutex` により並行安全
- エラー結果は **キャッシュしない**（成功した解決のみキャッシュ）
- TTL やサイズ上限はなし。キャッシュ有効期間は実行中のみ

---

## 4. 認証 — OCI レジストリ

### 仕組み
`go-containerregistry` の `authn.DefaultKeychain`:
1. `~/.docker/config.json` を読み込む
2. Docker credential helper（`credHelpers`, `credsStore`）をサポート
3. Docker Hub、GHCR（`ghcr.io`）、GCR（`gcr.io`）、ECR、ACR、その他 OCI 準拠レジストリをサポート
4. 明示的なトークン注入はなし。システムの Docker 認証チェーンに全面依存

### トークン環境変数なし
dockerfile-pin には、レジストリ認証専用の環境変数が **ありません**。事前に `docker login` を実行するか、credential helper を設定する必要があります。

### プライベートレジストリアクセス
- `docker login <registry>` 実行済み、または credential helper 設定済みの任意レジストリで動作
- `~/.docker/config.json` をネイティブに利用し、資格情報解決は `go-containerregistry` が担う

---

## 5. スキップ / 無視挙動

### 常にスキップ（ハードコード）
- `FROM scratch` — レジストリがないため黙ってスキップ
- マルチステージ参照（`FROM <stage>`）
- デフォルトなしの `ARG BASE` + `FROM ${BASE}` — 警告付きスキップ

### 既にピン済み
- `FROM image:tag@sha256:...` — `--update` 指定時を除きスキップ

### ユーザー設定による無視
設定ファイル（`.dockerfile-pin.yaml`）または CLI の `--ignore-images`:
```yaml
ignore-images:
  - "ghcr.io/myorg/*"               # glob: myorg 配下をすべて無視
  - "!ghcr.io/myorg/public-*"        # 否定: public-* は除外対象から外す
  - "*.dkr.ecr.*.amazonaws.com/**"   # ECR イメージ
  - "scratch"                         # 完全一致
```

パターン構文は doublestar glob（`**` は複数セグメント一致）。否定パターン（`!`）は以前の一致を上書き（後勝ち）。CLI フラグは設定ファイルパターンより後に評価されます。

---

## 6. エラーハンドリング

- `resolve.Exists()` が HTTP 404 で `false` を返した場合: イメージ未存在。ログ出力し、キャッシュしない
- それ以外のエラー（タイムアウト、認証失敗、ネットワーク）: 呼び出し元へ伝播。誤った失敗を残さないため **キャッシュしない**
- `check` サブコマンド: いずれかのチェック失敗で終了コード 1（`--exit-code` で調整可）

---

## 7. サポート対象ファイル

| ファイル種別 | ピン対象フィールド |
|---|---|
| Dockerfile | `FROM image:tag` |
| docker-compose.yml | `image: image:tag`（`build:` 指定イメージはスキップ） |
| GitHub Actions YAML | `container.image:`, `services.*.image:`, `uses: docker://image:tag` |

**非対応**: `uses: owner/repo@ref`（GitHub Actions SHA）。これは pinact の担当領域です。

---

## 8. 出力形式

- stdout へ diff 出力（既定の dry-run）
- インプレース変更は `--write`
- 機械可読出力は `--format json`
- `check` サブコマンドは `FAIL / OK / SKIP` テーブルを出力

---

## 9. 学び / 設計ノート

- **`authn.DefaultKeychain` は OCI 認証で最もクリーンなパターン**。アプリ側でトークン配線が不要で、Docker の credential chain に委譲できる。CI（多くは `docker login` 済み）でも開発環境でも適合する。
- **`Resolve` / `Exists` のキャッシュ分離** は微妙な不具合を回避する。`Resolve` の成功キャッシュが後続の `Exists` チェックを抑止してはいけないし、その逆も同様。
- **30 秒のリクエスト単位タイムアウト** により、遅いレジストリでハングしにくい。バッチ実行向けにはユーザー設定化が望ましい。
- **HEAD リクエストのみ**。digest 解決では GET でマニフェスト本体を取るより大幅に効率的。
- **GHES/GHCR 固有ロジックなし**。OCI は OCI として扱い、認証は credential chain で汎用的に処理している。
- **`min-age` 相当の更新クールダウン機能はない**。`.references/dockerfile-pin/cmd/pin.go` の CLI オプションは `--write` / `--update` / `--ignore-images` が中心で、経過日数ベースの候補フィルタは提供されない。
