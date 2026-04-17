# 競合ツール比較判定表（Seiton）

> 目的: actionlint / ghalint / zizmor / pinact / dockerfile-pin / frizbee と Seiton の機能差分を明確化し、採用検討の優先度を決める。
> 更新日: 2026-04-17

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
| Workflow構文/意味の厳格Lint | 強い | 必要項目中心 | Schema+Audit | なし | なし | なし | 実装済み（24 rules） | ✅ | 継続強化 |
| セキュリティ監査ルール網羅 | 中 | 中 | 非常に強い（30+ audits） | なし | なし | なし | 一部実装 | 🟡 | P0 |
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
| Online vulnerability / advisory audit | なし | 実験的 | あり | なし | なし | なし | 未対応 | ❌ | P0 |

---

## 4. 不足機能と採用方針

### P0（最優先）

1. zizmor系オンライン監査
- 例: known-vulnerable-actions, impostor-commit, ref-confusion, stale-action-refs
- 理由: 現代的なセキュリティツールとしての差別化に直結

2. ghalint未吸収の高価値ルール
- 例: deny-read-all, deny-inherit-secrets, timeout-minutes必須, GitHub App token action入力制約
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

- Seiton は既に「Lint + 安全なFix + Network-assisted pin remediation + 追従更新」の統合基盤を持ち、競合の中核機能の多くを満たしている。
- 競合を完全に上回るには、次の2点が鍵。
  - zizmor級オンライン監査の取り込み（P0）
  - dockerfile-pin/frizbee級の対象ファイル範囲拡張（P1）

この順で実装すれば、Seitonは「競合機能を包括しつつ、より現代的な統合ツール」という目標に最短で近づく。

---

## 6. 参照ドキュメント

- Docs/Seiton_Linter_spec.md
- Docs/linter_implementation_csharp_plan.md
- Docs/competitor-actionlint-structure-details.md
- Docs/competitor-ghalint-structure-details.md
- Docs/competitor-zizmor-structure-details.md
- Docs/competitor-pinact-structure-details.md
- Docs/competitor-dockerfile-pin-structure-details.md
- Docs/competitor-frizbee-structure-details.md
