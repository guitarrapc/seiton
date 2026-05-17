# 競合ツール比較判定表（Seiton）

> 目的: actionlint / ghalint / zizmor / pinact / dockerfile-pin / frizbee と Seiton の機能差分を明確化し、採用検討の優先度を決める。
> 更新日: 2026-05-13

---

## 1. 判定基準

- ✅ 満たす: 現行仕様・実装で同等以上
- 🟡 部分的: 一部は満たすが対象範囲・運用機能・網羅性に差がある
- ❌ 不足: 現行スコープ外、または未実装

---

## 2. ツール別総合判定

| 比較対象 | Lint機能 | Auto-fix / Remediation | 仕様追従データ更新 | 対象ファイル範囲 | 認証/GHES運用 | 総合判定 |
|---|---|---|---|---|---|---|
| actionlint | 🟡 | ❌ | ✅ | 🟡 | 🟡 | 🟡 |
| ghalint | ✅ | ❌ | ❌ | 🟡 | ❌ | ✅ |
| zizmor | 🟡 | 🟡 | ✅ | ✅ | 🟡 | 🟡 |
| pinact | 🟡 | ✅ | N/A | 🟡 | ✅ | ✅ |
| dockerfile-pin | N/A | 🟡 | N/A | ✅ | 🟡 | 🟡 |
| frizbee | N/A | 🟡 | N/A | ✅ | ❌ | 🟡 |
| Seiton（現状） | ✅ | ✅ | ✅ | 🟡 | ✅ | ✅ |

補足:
- Seiton の Lint/Remediation は GitHub Actions 中心に強い。
- ルール総数は 56（default local 52 + online audit 4、`RuleCatalog` 基準）まで拡張済み。
- ghalint 全 13 ポリシーを完全カバー（✅ 昇格）。
- Dockerfile/compose/任意YAML全般まで広げると、dockerfile-pin/frizbee に対して現状は部分的。

### 2.1 現在の実装済みルール一覧（最新）

default local rules（52）:

- `job-structure`
- `reusable-workflow`
- `permissions`
- `popular-action-inputs`
- `unpinned-uses`
- `unpinned-image`
- `dangerous-triggers`
- `job-permissions-required`
- `needs-graph`
- `shell-name`
- `runner-label`
- `id-naming`
- `glob-pattern`
- `dispatch-inputs`
- `schedule-event`
- `deny-write-all`
- `credentials`
- `template-injection`
- `expr-undefined-var`
- `run-env-context-direct-use`
- `runner-no-latest`
- `run-secrets-context-direct-use`
- `run-inputs-context-direct-use`
- `secrets-whole-context-access`
- `checkout-persist-credentials`
- `deny-read-all`
- `deny-inherit-secrets`
- `job-timeout-minutes-required`
- `github-app-token-inputs`
- `cache-poisoning`
- `self-hosted-runner`
- `unredacted-secrets`
- `secrets-outside-env`
- `workflow-secrets`
- `job-secrets`
- `action-shell-is-required`
- `matrix`
- `env-var`
- `deprecated-commands`
- `if-cond`
- `fake-ternary`
- `archived-uses`
- `insecure-commands`
- `overprovisioned-secrets`
- `forbidden-uses`
- `ref-version-mismatch`
- `use-trusted-publishing`
- `local-action-inputs`
- `workflow-call-input-default`
- `outdated-action-runner`
- `if-expr-wrapper`
- `concurrency-limits`

online audit rules（4）:

- `known-vulnerable-actions`
- `impostor-commit`
- `ref-confusion`
- `stale-action-refs`

---

## 3. 機能カテゴリ別判定（採用可否）

| 機能カテゴリ | actionlint | ghalint | zizmor | pinact | dockerfile-pin | frizbee | Seiton現状 | 判定 | 採用優先度 |
|---|---|---|---|---|---|---|---|---|---|
実装済み（56 rules: default local 52 + online audit 4）
| セキュリティ監査ルール網羅 | 中 | 中 | 非常に強い（36 audits） | なし | なし | なし | 実装済み（zizmor 監査 17件対応 + 7件部分対応） | 🟡 | P1 |
| UsesのSHA pin診断 | あり | あり | あり | 主機能 | なし | あり | 実装済み | ✅ | 維持 |
| Image digest pin診断 | 部分 | 部分 | あり | なし | 主機能 | 主機能 | 実装済み | ✅ | 維持 |
| Network-assisted pin fix | なし | なし | 部分 | 強い | 強い | 強い | 実装済み | ✅ | 維持 |
| pin更新候補の age 制御 | なし | なし | なし | あり | なし | なし | 実装済み（min_age_days） | ✅ | 維持 |
| GHES + フォールバック | 部分 | なし | 部分 | あり | なし | なし | 実装済み | ✅ | 維持 |
| ルール抑制/設定可観測性 | 中 | 中 | 高い | N/A | N/A | N/A | 実装済み | ✅ | 維持 |
| Auto-fix安全性（整形保持/再検証） | なし | 限定 | あり | あり | あり | あり | 実装済み | ✅ | 維持 |
| Dockerfile FROM pin | なし | なし | なし | なし | あり | なし | 未対応 | ❌ | P1 |
| docker-compose image pin | なし | なし | なし | なし | あり | あり | 未対応 | ❌ | P1 |
| 任意YAML image pin | なし | なし | なし | なし | 限定 | あり | 未対応 | ❌ | P1 |
| Online vulnerability / advisory audit | なし | 実験的 | あり | なし | なし | なし | 実装済み（opt-in `rules.<rule-id>.enabled: true`） | ✅ | 維持 |

---

## 4. 不足機能と採用方針

### P0（最優先）

1. pin対象のファイル範囲拡張（P1→P0へ昇格）
- Dockerfile（`FROM`）
- docker-compose（`image`）
- 任意YAML（`image`）
- 理由: `.references/dockerfile-pin` / `.references/frizbee` で実用機能が成熟しており、Actions外の供給網ギャップを早期に埋める効果が大きい

2. 残存 zizmor high-value audits（次段）
- `github-env`
- 理由: exploitability が高い未対応監査を優先。

3. uses policy の運用強化
- `forbidden-uses` の allow/deny ポリシー精緻化
- 理由: 運用設定と判定ロジックの拡張が必要

### P1（次点）

1. pin運用機能の補強
- comment整合チェックモード（version annotation整合）
- PRレビュー向け出力
- 理由: `.references/pinact` の verify/review 導線を吸収し、実運用の継続改善を回しやすくする

2. 監査プロファイル（regular/pedantic/auditor相当）
- 理由: 導入時ノイズ制御、組織内ロール別運用をしやすくする

### P2（中長期）

1. 高度監査ポリシーの拡張
- `forbidden-uses`（allow/deny 許可アクション制御）
- 理由: 組織統制と高度検知には有効だが、初期導入コストが高いため中長期で段階導入する

2. 実験機能系ポリシーの取り込み
- `validate-input` 相当（ghalint experimental）
- 理由: 効果はあるが安定運用観点で優先度は低め

3. zizmor 低優先度監査の取り込み
- `anonymous-definition`（名前未定義）
- `bot-conditions`（スプーフ可能な bot チェック）
- `obfuscation`（難読化検出）
- `misfeature`（非推奨 Actions 機能使用）
- `superfluous-actions`（ランナー標準機能で代替可能なアクション）
- `dependabot-execution` / `dependabot-cooldown`（Dependabot 設定検査）
- 理由: セキュリティ影響が限定的か、運用導入コストが高い

---

## 5. 結論

- Seiton は既に「Lint + 安全なFix + Network-assisted pin remediation + opt-in online audit + 追従更新」の統合基盤を持ち、競合の中核機能の多くを満たしている。
- ghalint は全 13 ポリシーを完全カバーし、完全上位互換を達成。
- actionlint は 17 ルール中 15 ルールを同等以上にカバー（残: shellcheck / pyflakes 外部連携のみ）。
- 競合を完全に上回るには、次の2点が鍵。
  - 残存 zizmor 監査差分の吸収（P0: 7件未対応 → `github-env` などの高価値監査から段階実装）
  - dockerfile-pin/frizbee級の対象ファイル範囲拡張（P0）

この順で実装すれば、Seitonは「競合機能を包括しつつ、より現代的な統合ツール」という目標に最短で近づく。

---

## 6. 競合ルール精査結果（.references 実体ベース）

本節は `.github/docs` 要約ではなく `.references/` 配下の実装コードを起点に確認した。

### 6.1 actionlint ルール対応表（17件）

| actionlint rule | Seiton 対応状況 | 備考 |
|---|---|---|
| matrix | ✅ | `matrix` |
| credentials | ✅ | `credentials` |
| shell-name | ✅ | `shell-name` |
| runner-label | ✅ | `runner-label` |
| events | ✅ | `dangerous-triggers` + `glob-pattern` + `dispatch-inputs` + `schedule-event` で網羅（dispatch input type/options 検証・cron 構文検証を追加） |
| job-needs | ✅ | `needs-graph` |
| action | ✅ | `popular-action-inputs` + `unpinned-uses` + `local-action-inputs` で網羅（local action.yml 起点の inputs 契約検証を追加） |
| env-var | ✅ | `env-var` |
| id | ✅ | `id-naming` |
| glob | ✅ | `glob-pattern` |
| permissions | ✅ | `permissions` + `deny-write-all` |
| workflow-call | ✅ | `reusable-workflow` + `deny-inherit-secrets` + `workflow-call-input-default` で網羅（input default 型整合検証を追加） |
| expression | ✅ | `expr-undefined-var` + `if-expr-wrapper`（+ 式ベース系） |
| deprecated-commands | ✅ | `deprecated-commands` |
| if-cond | ✅ | `if-cond` + `if-expr-wrapper` |
| shellcheck | ❌ | 外部 shellcheck 連携未実装 |
| pyflakes | ❌ | 外部 pyflakes 連携未実装 |

対応率: 15/17（88%）。未対応は外部ツール連携のみ。

### 6.2 ghalint ポリシー対応表（13件）

| ghalint policy | Seiton 対応状況 | 備考 |
|---|---|---|
| job_permissions | ✅ | `job-permissions-required` |
| deny_read_all_permission | ✅ | `deny-read-all` |
| deny_write_all_permission | ✅ | `deny-write-all` |
| deny_inherit_secrets | ✅ | `deny-inherit-secrets` |
| workflow_secrets | ✅ | `workflow-secrets` |
| job_secrets | ✅ | `job-secrets` |
| deny_job_container_latest_image | ✅ | `unpinned-image`（digest pinning 要求により `:latest` も検出） |
| action_ref_should_be_full_length_commit_sha | ✅ | `unpinned-uses` |
| github_app_should_limit_repositories | ✅ | `github-app-token-inputs` |
| github_app_should_limit_permissions | ✅ | `github-app-token-inputs` |
| action_shell_is_required | ✅ | `action-shell-is-required` |
| job_timeout_minutes_is_required | ✅ | `job-timeout-minutes-required` |
| checkout_persist_credentials_should_be_false | ✅ | `checkout-persist-credentials` |

対応率: 13/13（100%）。完全カバー達成。

### 6.3 zizmor 監査対応サマリー（36件）

| 区分 | 件数 | Seiton 状況 |
|---|---:|---|
| 直接対応済み | 23 | `artipacked`, `bot-conditions`, `cache-poisoning`, `concurrency-limits`, `dangerous-triggers`, `github-app`, `hardcoded-container-credentials`, `impostor-commit`, `insecure-commands`, `known-vulnerable-actions`, `ref-confusion`, `secrets-inherit`, `secrets-outside-env`, `self-hosted-runner`, `stale-action-refs`, `template-injection`, `unpinned-images`, `unpinned-tools`, `unpinned-uses`, `unredacted-secrets`, `unsound-condition`, `unsound-contains`, `use-trusted-publishing` |
| 部分対応 | 6 | `archived-uses`, `excessive-permissions`, `forbidden-uses`, `overprovisioned-secrets`, `ref-version-mismatch`, `undocumented-permissions` |
| 未対応 | 7 | 高度セキュリティ監査群（残差分） |

zizmor 監査ID別対応表（実装確認ベース）:

| 監査ID | Seiton 対応状況 | 備考 |
|---|---|---|
| `anonymous-definition` | ❌ | 専用監査なし |
| `archived-uses` | 🟡 | `archived-uses`（静的判定の初期実装） |
| `artipacked` | ✅ | `artipacked` — checkout + upload-artifact のステップ間相関分析 |
| `bot-conditions` | ✅ | `bot-conditions` |
| `cache-poisoning` | ✅ | `cache-poisoning` |
| `concurrency-limits` | ✅ | `concurrency-limits` — ワークフロー/ジョブレベルの concurrency 設定と cancel-in-progress を検査 |
| `dangerous-triggers` | ✅ | `dangerous-triggers` |
| `dependabot-cooldown` | ❌ | 専用監査なし |
| `dependabot-execution` | ❌ | 専用監査なし |
| `excessive-permissions` | 🟡 | `deny-write-all` / `deny-read-all` / `job-permissions-required` で部分対応 |
| `forbidden-uses` | 🟡 | `forbidden-uses`（allow/deny wildcard の初期実装） |
| `github-app` | ✅ | `github-app-token-inputs`（repositories/permissions 制限チェック） |
| `github-env` | ❌ | 専用監査なし |
| `hardcoded-container-credentials` | ✅ | `credentials`（`credentials.password` のハードコード検出） |
| `impostor-commit` | ✅ | online 監査（`rules.impostor-commit.enabled: true` で有効化） |
| `insecure-commands` | ✅ | `insecure-commands` |
| `known-vulnerable-actions` | ✅ | online 監査（`rules.known-vulnerable-actions.enabled: true` で有効化） |
| `misfeature` | ❌ | 専用監査なし |
| `obfuscation` | ❌ | 専用監査なし |
| `overprovisioned-secrets` | 🟡 | `overprovisioned-secrets`（step/reusable-call 中心の初期実装） |
| `ref-confusion` | ✅ | online 監査（`rules.ref-confusion.enabled: true` で有効化） |
| `ref-version-mismatch` | 🟡 | `ref-version-mismatch`（ref/path major mismatch の初期実装） |
| `secrets-inherit` | ✅ | `deny-inherit-secrets` |
| `secrets-outside-env` | ✅ | `secrets-outside-env` |
| `self-hosted-runner` | ✅ | `self-hosted-runner` |
| `stale-action-refs` | ✅ | online 監査（`rules.stale-action-refs.enabled: true` で有効化） |
| `superfluous-actions` | ❌ | 専用監査なし |
| `template-injection` | ✅ | `template-injection` |
| `undocumented-permissions` | 🟡 | `permissions` / `job-permissions-required` で部分対応 |
| `unpinned-images` | ✅ | `unpinned-image` |
| `unpinned-tools` | ✅ | `unpinned-tools` |
| `unpinned-uses` | ✅ | `unpinned-uses` |
| `unredacted-secrets` | ✅ | `unredacted-secrets` |
| `unsound-condition` | ✅ | `unsound-condition` |
| `unsound-contains` | ✅ | `unsound-contains` |
| `use-trusted-publishing` | ✅ | `use-trusted-publishing`（publish + `id-token: write` 判定） |

対応率: 29/36（81%）— 残7件未対応（うち2件はスコープ外）。

### 6.4 pinact / dockerfile-pin / frizbee（ルールエンジンではなく変換系）

| ツール | 実装上の判定単位 | Seiton 対応状況 |
|---|---|---|
| pinact | `unpinned-action`, `parse-error`（SARIF rule）+ check/update/review/verify 機能 | 🟡 （pin remediation は対応、comment整合check・review出力は不足） |
| dockerfile-pin | `ok/fail/skip/warn` ステータス + Dockerfile/compose/actions image 書換 | 🟡 （actions内 image は対応、Dockerfile/compose は不足） |
| frizbee | actions/image 置換 + skip sentinel + modified-file gate | 🟡 （actions/image resolverは対応、任意YAML image置換や専用CLI導線は不足） |

---

## 7. 追加採用バックログ（ルール単位）

### P0（競合網羅の最短経路）

1. Dockerfile / compose / 任意YAML image pin 拡張

2. zizmor 残差分（未対応 7件）
- `github-env` — GITHUB_ENV への危険な書き込み検出
- `anonymous-definition` — name 未定義のワークフロー/アクション
- `obfuscation` — 難読化された Actions 機能使用
- `misfeature` — 非推奨/危険な Actions 機能使用
- `superfluous-actions` — ランナー標準機能で代替可能
- `dependabot-execution` / `dependabot-cooldown` — Dependabot 設定検査

### P1（適用範囲拡張）

1. pinact 運用機能
- pin comment 整合チェック
- PR review 向け出力

---

## 8. 参照ドキュメント

- .github/docs/Seiton_Linter_spec.md
- .github/docs/linter_implementation_csharp_plan.md
- .github/docs/competitor-actionlint-structure-details.md
- .github/docs/competitor-ghalint-structure-details.md
- .github/docs/competitor-zizmor-structure-details.md
- .github/docs/competitor-pinact-structure-details.md
- .github/docs/competitor-dockerfile-pin-structure-details.md
- .github/docs/competitor-frizbee-structure-details.md
- .references/actionlint
- .references/ghalint
- .references/zizmor
- .references/pinact
- .references/dockerfile-pin
- .references/frizbee
