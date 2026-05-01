# Seiton Config 設計仕様

本ドキュメントは Seiton の設定ファイル（`.github/seiton.yaml` / `seiton.yaml`）の設計仕様を定義する。ユーザー向けリファレンスは [`docs/configuration.md`](../../docs/configuration.md) を参照。

---

## 1. 設計の動機

### 1.1 旧設計の課題

初期の設定設計では以下の課題があった。

| # | 課題 | 具体例 |
|---|---|---|
| **U-1** | ユーザーの思考単位と config の単位がズレている | ユーザーは rule-id 単位で考えるが、`additiveCustomization`、`exprContext`、`pin_resolution`、`online_audit` のように内部実装の都合で切られた概念が表面に出ていた |
| **U-2** | 設定名が「何をしたいか」ではなく「どう実装しているか」になっている | `additiveCustomization`（加算的カスタマイズ）、`exprContext`（式コンテキスト）など内部モジュール名が前面に出ていた |
| **U-3** | rule-id と設定が直接結びついていない | `dangerous-triggers` と `additionalDangerousEvents`、`runner-label` と `additionalKnownHostedLabels` など、関連する設定が離れた場所にあった |
| **U-4** | 同種の設定が複数箇所に分散していた | ネットワーク系設定（timeout、concurrency、fail-open）が `pin_resolution` と `online_audit` に重複 |
| **U-5** | 重要度の違う設定が同じレベルに並んでいた | 日常的な `rules` と低レベルな `token_env_vars`、`request_timeout_sec` が同列 |
| **U-6** | 追加専用の思想が UI に出すぎていた | `additionalDangerousEvents` — ユーザーは最終集合を知りたいだけ |
| **U-7** | 命名規則と抽象度が揃っていない | kebab-case と snake_case の混在、`additional...` の冗長な名前 |

### 1.2 解決方針

上記課題に対して、以下の設計原則を適用して現行スキーマを設計した。

| 原則 | 内容 | 対応する課題 |
|---|---|---|
| **ユーザーの思考単位で切る** | rule-id、exclusions、fix、network を軸にする | U-1 |
| **ルールに効く設定はルールの近くに** | rule-specific options を `rules.<rule-id>` 配下に配置 | U-3 |
| **日常設定と高度設定を分離** | `rules` / `exclusions` が主、`network` は詳細設定 | U-5 |
| **「何をしたいか」で命名** | `events.extend`、`known-hosted-labels.extend` など目的語 | U-2, U-6 |
| **同種の設定はまとめる** | ネットワーク系は `network` に集約 | U-4 |
| **命名規則を統一** | 全キー kebab-case | U-7 |
| **内部概念は隠す** | `analysis`、`audit` は独立キーにせず既存構造に統合 | U-2 |

### 1.3 設計判断のトレードオフ

| 項目 | 判断 | 理由 |
|---|---|---|
| `analysis` トップレベルキー | **不採用** | `assume-events` は `rules.expr-undefined-var.assume-events` として rule 配下に収まる。独立セクションにする必然性がない |
| `audit` トップレベルキー | **不採用** | online rule の有効化は `rules.<rule-id>.enabled: true` で統一。別セクションは二重管理になる |
| `network.fail-open` | **`network.on-error: skip \| fail` を採用** | fail-open/fail-closed はセキュリティ用語として曖昧。明示的な列挙値のほうが意図が伝わる |
| `exclusions[].files` → `exclusions[].file` | **スカラー（単一 glob）を採用** | 単数形が型と一致し誤解を防ぐ。複数パターンは複数エントリで表現 |
| `extend` キーワード | **採用** | built-in 値との関係が明確。最終集合宣言より実用的 |

---

## 2. 現行スキーマ

### 2.1 トップレベル構造

```yaml
rules:        # ルール個別の enable / severity / rule-specific options
exclusions:   # ファイル・ジョブ単位の診断抑制
fix:          # auto-fix の挙動制御
network:      # ネットワーク系の共通設定
output:       # 診断出力の制御
```

すべてのセクションは省略可能。空ファイルはデフォルト設定と同等。未知のトップレベルキーは設定エラーとなる。

### 2.2 `rules`

ルール個別の設定。キーは rule-id（kebab-case）。未知の rule-id は設定エラー。

```yaml
rules:
  # 無効化
  runner-no-latest:
    enabled: false

  # severity 上書き
  checkout-persist-credentials:
    severity: warning    # error | warning | info

  # opt-in online rule の有効化
  known-vulnerable-actions:
    enabled: true

  # rule-specific: イベント拡張
  dangerous-triggers:
    severity: error
    events:
      extend:
        - issue_comment

  # rule-specific: ランナーラベル拡張
  runner-label:
    known-hosted-labels:
      extend:
        - ubuntu-24.04-arm

  # rule-specific: 公開レジストリ拡張
  credentials:
    public-registries:
      extend:
        - registry.example.com

  # rule-specific: untrusted trigger 拡張
  cache-poisoning:
    untrusted-triggers:
      extend:
        - issue_comment

  # rule-specific: 出力コマンド拡張
  unredacted-secrets:
    output-commands:
      extend:
        - tee

  # rule-specific: uses 参照の deny/allow
  forbidden-uses:
    deny:
      - "deprecated-org/*"
    allow:
      - "approved-org/*"

  # rule-specific: 式の未定義変数チェック用イベント仮定
  expr-undefined-var:
    assume-events:
      - workflow_dispatch

  # rule-specific: シークレット過剰供給の閾値
  overprovisioned-secrets:
    max-step-env-secrets: 5
    max-job-secrets: 10
```

#### rule-specific options 一覧

| Rule | Key | 型 | 説明 |
|---|---|---|---|
| `dangerous-triggers` | `events.extend` | `string[]` | 危険トリガーイベントの追加 |
| `runner-label` | `known-hosted-labels.extend` | `string[]` | 既知ランナーラベルの追加 |
| `credentials` | `public-registries.extend` | `string[]` | 公開レジストリの追加 |
| `cache-poisoning` | `untrusted-triggers.extend` | `string[]` | 信頼できないトリガーの追加 |
| `unredacted-secrets` | `output-commands.extend` | `string[]` | 監視対象出力コマンドの追加 |
| `forbidden-uses` | `deny` / `allow` | `string[]` | `uses:` 参照の拒否/許可 wildcard パターン |
| `expr-undefined-var` | `assume-events` | `string[]` | 式評価時に仮定するイベント |
| `overprovisioned-secrets` | `max-step-env-secrets` | `int` | ステップ単位のシークレット数上限 |
| `overprovisioned-secrets` | `max-job-secrets` | `int` | ジョブ単位のシークレット数上限 |

`extend` リストは **built-in セットに追加**する。置換はしない。

### 2.3 `exclusions`

ファイル・ジョブ単位でルール診断を抑制する。

```yaml
exclusions:
  - file: ".github/workflows/legacy-*.yml"
    rules:
      - runner-no-latest
      - job-permissions-required

  - file: ".github/workflows/release.yml"
    jobs:
      - publish
    rules:
      - credentials
```

| Key | 型 | 必須 | 説明 |
|---|---|---|---|
| `file` | `string`（スカラー） | Yes | glob パターン（`*` / `**`、パス区切り `/`、大小文字区別） |
| `rules` | `string[]` | No | 抑制対象の rule-id リスト。省略時はファイル全体（全ルール）を除外 |
| `jobs` | `string[]` | No | 対象ジョブ ID（`job.id`）。省略時はファイル全体に適用 |

**加算方式（progressive narrowing）**:
- `file` のみ → ファイル全体を検査から除外
- `file` + `jobs` → 指定ジョブを全ルールから除外
- `file` + `rules` → ファイル全体で指定ルールのみ除外
- `file` + `jobs` + `rules` → 指定ジョブで指定ルールのみ除外

`rules: []`（明示的空リスト）は no-op（除外効果なし）。省略と空リストは意味が異なる。

**注意**: `file` はスカラー値（単一パターン）。複数パターンが必要な場合は複数エントリで記述する。

### 2.4 `fix`

auto-fix（`seiton fix`）の挙動を制御する。

```yaml
fix:
  defaults:
    job-timeout-minutes: 15

  pinning:
    enable-network: true
    min-age-days: 14
    exclude-branches:
      - main
      - master
    ignore-actions:
      - uses: "slsa-framework/*"
        ref: "*"

  images:
    enable-network: true
    exclude-images:
      - scratch
    exclude-tags:
      - latest
    ignore-images:
      - "mcr.microsoft.com/**"
```

| Key | 型 | デフォルト | 説明 |
|---|---|---|---|
| `defaults.job-timeout-minutes` | `int?` | `null` | `job-timeout-minutes-required` の auto-fix が挿入する値。`null` で auto-fix 無効 |
| `pinning.enable-network` | `bool` | `false` | ネットワーク経由の SHA 解決を有効化 |
| `pinning.min-age-days` | `int` | `14` | コミットの最低経過日数 |
| `pinning.exclude-branches` | `string[]` | `["main", "master"]` | ピン留めをスキップするブランチ名（**完全一致**、ordinal） |
| `pinning.ignore-actions` | `IgnoreActionEntry[]` | `[]` | ピン留めをスキップするアクション（**wildcard マッチング**: `*` = 任意列、`?` = 任意 1 文字）。Regex 不使用、ReDoS リスクなし |
| `images.enable-network` | `bool` | `false` | ネットワーク経由のダイジェスト解決を有効化 |
| `images.exclude-images` | `string[]` | `["scratch"]` | ピン留めをスキップするイメージ名 |
| `images.exclude-tags` | `string[]` | `["latest"]` | ピン留めをスキップするタグ名 |
| `images.ignore-images` | `string[]` | `[]` | ピン留めをスキップするイメージ（glob パターン） |

### 2.5 `network`

ネットワーク系の共通設定。online rule および network-assisted fix の両方に適用。

```yaml
network:
  on-error: skip
  timeout-seconds: 30
  max-concurrency: 4
  github:
    ghes-api-url: ""
    ghes-fallback: false
```

| Key | 型 | デフォルト | 制約 | 説明 |
|---|---|---|---|---|
| `on-error` | `string` | `skip` | `skip` \| `fail` | ネットワークエラー時の挙動 |
| `timeout-seconds` | `int` | `30` | `0`–`300`（超過はエラー＋クランプ） | リクエスト単位のタイムアウト |
| `max-concurrency` | `int` | `min(4, CPU数)` | `1`–`CPU数`（超過はエラー＋クランプ） | 並列リクエスト数 |
| `github.ghes-api-url` | `string` | `""` | 空 = github.com のみ。HTTPS 必須、userinfo 禁止 | GHES API ベース URL |
| `github.ghes-fallback` | `bool` | `false` | — | GHES 失敗時に github.com にフォールバック |

HTTP クライアントは **`AllowAutoRedirect` 無効**で、**同一オリジンのリダイレクトのみ**追従する。異なるオリジンへの `3xx` はトークン漏えい防止のためリクエストを発行しない。

### 2.6 `output`

```yaml
output:
  sort-order: location    # location | rule
```

| Key | 型 | デフォルト | 説明 |
|---|---|---|---|
| `sort-order` | `string` | `location` | `location`: ソース位置順。`rule`: ルール優先度順 |

---

## 3. パターンマッチングの種別

設定値で使用されるパターンマッチングは用途ごとに異なる。

| 設定箇所 | アルゴリズム | 詳細 |
|---|---|---|
| `exclusions[].file` | `GlobMatch` | セグメント区切り `*` / `**`、大小文字区別 |
| `fix.pinning.ignore-actions` | `WildcardMatch` (char) | `*` = 任意列、`?` = 任意 1 文字。Regex 不使用 |
| `fix.images.ignore-images` | `GlobMatch` | `exclusions[].file` と同一 |
| `rules.forbidden-uses.deny/allow` | `WildcardMatchUsesPolicy` (byte) | パス区切り `/` を跨ぐ `*`、`?` = 任意 1 文字 |
| CLI `--ignore` | `string.Contains` | 部分文字列一致、大小文字無視 |
| `fix.pinning.exclude-branches` | `string.Equals` | 完全一致、ordinal |

`WildcardMatch` と `WildcardMatchUsesPolicy` は同一アルゴリズム（2 ポインタ＋ star-index バックトラック）の `char` / `byte` オーバーロードで、`ActionRefHelpers` に共通実装として配置されている。決定的で指数爆発しないため ReDoS リスクはない。

---

## 4. ローダーのリソース制限

悪意ある設定入力に対する防御。

| 制限 | 値 | 説明 |
|---|---|---|
| UTF-8 ペイロード上限 | `1 048 576` bytes | `--config` / `ValidateFile` / `Validate` 共通 |
| YAML DOM 最大深度 | `64` | マッピング/シーケンスのネスト |
| DOM 構造ユニット上限 | `50 000` | スカラーキー＋スカラーリーフ＋コンパウンドの合計 |

超過時は検証エラーとなり、設定は読み込まれない。

---

## 5. 設定ファイルの発見

1. `--config` オプション / `SEITON_CONFIG` 環境変数（明示指定）
2. カレントディレクトリから親方向に走査:
   - `.github/seiton.yaml`
   - `.github/seiton.yml`
   - `seiton.yaml`
   - `seiton.yml`
3. 見つからなければ built-in デフォルト

### 信頼境界

- 設定ファイルはレビュー対象のコミット済みファイルを推奨
- `SEITON_CONFIG` / `--config` は任意パスを受け入れるため、共有ランナーでは信頼できるパスのみを指定する
- Fork PR のマージ ref から設定を読む場合は注意が必要
- `seiton check --verbose` / `seiton fix --verbose` で `config:` の解決パスを stderr に出力

---

## 6. デフォルト値一覧

| 設定 | デフォルト |
|---|---|
| `rules.<rule-id>.enabled` | `true`（ローカルルール）/ `false`（online ルール） |
| `rules.<rule-id>.severity` | ルール固有のデフォルト |
| `exclusions` | `[]` |
| `fix.defaults.job-timeout-minutes` | `null`（auto-fix 無効） |
| `fix.pinning.enable-network` | `false` |
| `fix.pinning.min-age-days` | `14` |
| `fix.pinning.exclude-branches` | `["main", "master"]` |
| `fix.pinning.ignore-actions` | `[]` |
| `fix.images.enable-network` | `false` |
| `fix.images.exclude-images` | `["scratch"]` |
| `fix.images.exclude-tags` | `["latest"]` |
| `fix.images.ignore-images` | `[]` |
| `network.on-error` | `skip` |
| `network.timeout-seconds` | `30` |
| `network.max-concurrency` | `min(4, 論理プロセッサ数)` |
| `network.github.ghes-api-url` | `""`（github.com のみ） |
| `network.github.ghes-fallback` | `false` |
| `output.sort-order` | `location` |
