# 競合ツール比較判定表（Seiton）

> 目的: actionlint / ghalint / zizmor / pinact / dockerfile-pin / frizbee と Seiton の機能差分を明確化し、採用検討の優先度を決める。
> 更新日: 2026-04-18

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
| ghalint | 🟡 | ❌ | ❌ | 🟡 | ❌ | 🟡 |
| zizmor | 🟡 | 🟡 | ✅ | ✅ | 🟡 | 🟡 |
| pinact | 🟡 | ✅ | N/A | 🟡 | ✅ | ✅ |
| dockerfile-pin | N/A | 🟡 | N/A | ✅ | 🟡 | 🟡 |
| frizbee | N/A | 🟡 | N/A | ✅ | ❌ | 🟡 |
| Seiton（現状） | ✅ | ✅ | ✅ | 🟡 | ✅ | ✅ |

補足:
- Seiton の Lint/Remediation は GitHub Actions 中心に強い。
- Dockerfile/compose/任意YAML全般まで広げると、dockerfile-pin/frizbee に対して現状は部分的。

---

## 3. 機能カテゴリ別判定（採用可否）

| 機能カテゴリ | actionlint | ghalint | zizmor | pinact | dockerfile-pin | frizbee | Seiton現状 | 判定 | 採用優先度 |
|---|---|---|---|---|---|---|---|---|---|
| Workflow構文/意味の厳格Lint | 強い | 必要項目中心 | Schema+Audit | なし | なし | なし | 実装済み（31 rules: default local 27 + online audit 4） | ✅ | 継続強化 |
| セキュリティ監査ルール網羅 | 中 | 中 | 非常に強い（30+ audits） | なし | なし | なし | 実装済み（高優先度 online 4 + local hardening） | 🟡 | P1 |
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
| Online vulnerability / advisory audit | なし | 実験的 | あり | なし | なし | なし | 実装済み（opt-in online_audit） | ✅ | 維持 |

---

## 4. 不足機能と採用方針

### P0（最優先）

1. 残存 zizmor high-value audits
- 例: unredacted-secrets, cache-poisoning
- 理由: online_audit 実装後の残差分を埋め、監査網羅性を引き上げる

2. ghalint未吸収の高価値ルール
- 例: workflow_secrets, job_secrets, action_shell_is_required
- 理由: 実運用での事故予防効果が高い

### P1（次点）

1. pin対象のファイル範囲拡張
- Dockerfile（FROM）
- docker-compose（image）
- 任意YAML（image）
- 理由: dockerfile-pin/frizbee の適用領域を吸収

2. pin運用機能の補強
- comment整合チェックモード
- PRレビュー向け出力
- 理由: pinact の運用導線を吸収

### P2（中長期）

1. 監査プロファイル（regular/pedantic/auditor相当）
- 理由: 導入時ノイズ制御、組織内ロール別運用をしやすくする

---

## 5. 結論

- Seiton は既に「Lint + 安全なFix + Network-assisted pin remediation + opt-in online audit + 追従更新」の統合基盤を持ち、競合の中核機能の多くを満たしている。
- 競合を完全に上回るには、次の2点が鍵。
  - 残存 zizmor/ghalint 監査差分の吸収（P0）
  - dockerfile-pin/frizbee級の対象ファイル範囲拡張（P1）

この順で実装すれば、Seitonは「競合機能を包括しつつ、より現代的な統合ツール」という目標に最短で近づく。

---

## 6. 競合ルール精査結果（.references 実体ベース）

本節は `Docs/` 要約ではなく `.references/` 配下の実装コードを起点に確認した。

### 6.1 actionlint ルール対応表（17件）

| actionlint rule | Seiton 対応状況 | 備考 |
|---|---|---|
| matrix | ❌ | matrix 専用ルール未実装 |
| credentials | ✅ | `credentials` |
| shell-name | ✅ | `shell-name` |
| runner-label | ✅ | `runner-label` |
| events | 🟡 | `dangerous-triggers` + `glob-pattern` で一部吸収 |
| job-needs | ✅ | `needs-graph` |
| action | 🟡 | `popular-action-inputs` + `unpinned-uses` 等で一部吸収 |
| env-var | ❌ | env key 命名専用ルール未実装 |
| id | ✅ | `id-naming` |
| glob | ✅ | `glob-pattern` |
| permissions | ✅ | `permissions` + `deny-write-all` |
| workflow-call | 🟡 | `reusable-workflow` + `deny-inherit-secrets` で一部吸収 |
| expression | ✅ | `expr-undefined-var`（+式ベース系） |
| deprecated-commands | ❌ | `::set-output` など deprecated command 検出未実装 |
| if-cond | ❌ | if 条件の定数判定・不正判定専用ルール未実装 |
| shellcheck | ❌ | 外部 shellcheck 連携未実装 |
| pyflakes | ❌ | 外部 pyflakes 連携未実装 |

### 6.2 ghalint ポリシー対応表（13件）

| ghalint policy | Seiton 対応状況 | 備考 |
|---|---|---|
| job_permissions | ✅ | `job-permissions-required` |
| deny_read_all_permission | ✅ | `deny-read-all` |
| deny_write_all_permission | ✅ | `deny-write-all` |
| deny_inherit_secrets | ✅ | `deny-inherit-secrets` |
| workflow_secrets | ❌ | workflow env の secrets/github.token 禁止未実装 |
| job_secrets | ❌ | job env の secrets/github.token 禁止未実装 |
| deny_job_container_latest_image | ❌ | `:latest` 専用禁止は未実装（`unpinned-image` はより広いが同等ではない） |
| action_ref_should_be_full_length_commit_sha | ✅ | `unpinned-uses` + `unpinned-image` |
| github_app_should_limit_repositories | ✅ | `github-app-token-inputs` |
| github_app_should_limit_permissions | ✅ | `github-app-token-inputs` |
| action_shell_is_required | ❌ | composite action 向け shell 必須未実装 |
| job_timeout_minutes_is_required | ✅ | `job-timeout-minutes-required` |
| checkout_persist_credentials_should_be_false | ✅ | `checkout-persist-credentials` |

### 6.3 zizmor 監査対応サマリー（34件）

| 区分 | 件数 | Seiton 状況 |
|---|---:|---|
| 直接対応済み | 10 | `dangerous-triggers`, `template-injection`, `unpinned-uses`, `unpinned-images` 相当, `secrets-inherit` 相当, `excessive-permissions` 部分, `known-vulnerable-actions`, `impostor-commit`, `ref-confusion`, `stale-action-refs` |
| 部分対応 | 4 | `excessive-permissions`（`deny-write-all`中心）, `ref-version-mismatch`（pin comment check 未実装）, `concurrency-limits`（部分）, `forbidden-uses`（config deny list 未実装） |
| 未対応 | 20 | 高度セキュリティ監査群（残差分） |

zizmor 監査ID一覧（実装確認ベース）:

- anonymous-definition
- archived-uses
- artipacked
- bot-conditions
- cache-poisoning
- concurrency-limits
- dangerous-triggers
- dependabot-cooldown
- dependabot-execution
- excessive-permissions
- forbidden-uses
- github-env
- hardcoded-container-credentials
- impostor-commit
- insecure-commands
- known-vulnerable-actions
- misfeature
- obfuscation
- overprovisioned-secrets
- ref-confusion
- ref-version-mismatch
- secrets-inherit
- secrets-outside-env
- self-hosted-runner
- stale-action-refs
- superfluous-actions
- template-injection
- undocumented-permissions
- unpinned-images
- unpinned-uses
- unredacted-secrets
- unsound-condition
- unsound-contains
- use-trusted-publishing

### 6.4 pinact / dockerfile-pin / frizbee（ルールエンジンではなく変換系）

| ツール | 実装上の判定単位 | Seiton 対応状況 |
|---|---|---|
| pinact | `unpinned-action`, `parse-error`（SARIF rule）+ check/update/review 機能 | 🟡 （pin remediation は対応、comment整合check・review出力は不足） |
| dockerfile-pin | `ok/fail/skip/warn` ステータス + Dockerfile/compose/actions image 書換 | 🟡 （actions内 image は対応、Dockerfile/compose は不足） |
| frizbee | actions/image 置換 + skip sentinel + modified-file gate | 🟡 （actions/image resolverは対応、任意YAML image置換や専用CLI導線は不足） |

---

## 7. 追加採用バックログ（ルール単位）

### P0（競合網羅の最短経路）

1. actionlint 未対応ルール
- `matrix`
- `env-var`
- `deprecated-commands`
- `if-cond`

2. ghalint 未対応ポリシー
- `workflow_secrets`
- `job_secrets`
- `action_shell_is_required`

3. zizmor online/high-value audits
- `known-vulnerable-actions`
- `impostor-commit`
- `ref-confusion`
- `stale-action-refs`
- `unredacted-secrets`
- `cache-poisoning`

### P1（適用範囲拡張）

1. dockerfile-pin/frizbee 領域
- Dockerfile `FROM` digest pin
- docker-compose `image:` digest pin
- 任意 YAML `image:` digest pin

2. pinact 運用機能
- pin comment 整合チェック
- PR review 向け出力

---

## 8. 参照ドキュメント

- Docs/Seiton_Linter_spec.md
- Docs/linter_implementation_csharp_plan.md
- Docs/competitor-actionlint-structure-details.md
- Docs/competitor-ghalint-structure-details.md
- Docs/competitor-zizmor-structure-details.md
- Docs/competitor-pinact-structure-details.md
- Docs/competitor-dockerfile-pin-structure-details.md
- Docs/competitor-frizbee-structure-details.md
- .references/actionlint
- .references/ghalint
- .references/zizmor
- .references/pinact
- .references/dockerfile-pin
- .references/frizbee
