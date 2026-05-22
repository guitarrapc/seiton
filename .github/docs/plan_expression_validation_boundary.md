# Expression Validation の Parser/Linter 境界見直し — 調査結果と対応計画

> 作成日: 2026-05-23
> 対象: expression availability / type validation を parser に置く現行設計を維持するか、linter に寄せ直すかの検討

---

## 1. 問題設定

Seiton の現行仕様では、parser が次を担当している。

- expression の構文解析
- context availability validation
- function availability validation
- type inference の基盤提供
- 一部の property access validation

一方で linter 側にも expression を前提にした rule 群が多数ある。

- `expr-undefined-var`
- `template-injection`
- `run-env-context-direct-use`
- `run-secrets-context-direct-use`
- `run-inputs-context-direct-use`
- `if-cond`, `fake-ternary`, `unsound-contains` など

このため、責務境界がやや重なって見える。

特に `github`, `matrix`, `needs`, `secrets` など GitHub Actions の意味論に近い情報を parser がどこまで持つべきかが論点になる。

---

## 2. 現状整理

### 2.1 現行仕様上の位置づけ

現行 spec では parser 側に expression semantic analysis が明確に含まれている。

- `Seiton_spec.md`: parser が `Expression parsing and semantic typing data` を所有
- `Seiton_Parser_spec.md`: expression parser grammar, built-in functions, context availability matrix, function availability まで規定
- `Seiton_Linter_spec.md`: linter は parser output を消費し、fatal parse なら parser diagnostics を返して打ち切る

したがって、**現行設計は一応一貫している**。

### 2.2 実装上の位置づけ

実装でも parser が inline expression parse/validate を行っている。

- `WorkflowParser.ExpressionIntegration.cs`
- `ExpressionParser.cs`
- `ExpressionSemanticAnalyzer.cs`
- `DynamicContextTypeBuilder.cs`

一方で linter は parser が残した string / expression 情報を使って、rule ごとの semantic/policy 診断を追加している。

### 2.3 現状の利点

- parser 完了時点で expression の構文・基礎意味論がある程度検証済み
- AST 利用者が parser 単体でも expression diagnostics を得られる
- linter の前提が単純になる

### 2.4 現状の違和感

- availability/type は YAML 構文ではなく GitHub Actions の意味論に近い
- parser と linter の双方が expression semantics を扱っており、役割が二重に見えやすい
- parser spec が GitHub Actions domain knowledge を多く抱え込みやすい

---

## 3. 選択肢

### 選択肢 A — 現行維持

parser が expression syntax + semantic validation を引き続き保持する。

利点:

- 既存実装と spec を大きく変えなくてよい
- parser 単体利用でも value が高い
- linter は parser diagnostics をそのまま扱える

欠点:

- GitHub domain knowledge が parser に集まり続ける
- rule-based な制御や suppression との境界が不明瞭
- parser/linter の責務説明が直感に反しやすい

### 選択肢 B — 完全移管

expression syntax も availability/type もすべて linter 側に移す。

利点:

- parser が純粋な YAML/AST builder に近づく

欠点:

- linter が expression parser を内包することになり責務が重い
- parser 単体利用価値が大きく下がる
- 実装移行コストが高い
- parser/linter 双方の API と spec を大きく壊す

### 選択肢 C — ハイブリッド再分離

parser は expression syntax と AST 構築に集中し、availability/type/property など GitHub Actions 意味論は linter に寄せる。

利点:

- parser と linter の責務説明が最も自然
- semantic diagnostics を rule/config/severity/suppression に乗せやすい
- parser spec の肥大化を抑えられる

欠点:

- 段階的移行が必要
- parser diagnostics と linter diagnostics の再配分が必要
- 既存ルールとの責務整理に時間がかかる

---

## 4. 推奨方針

**推奨は選択肢 C（ハイブリッド再分離）**。

具体的な線引きは次のとおり。

### parser に残すもの

- `${{ ... }}` の境界検出
- expression syntax parse
- expression AST 構築
- token/range 付与
- expression 自体の純粋な構文エラー

### linter に寄せるもの

- root context availability (`github`, `needs`, `matrix`, `secrets`, `inputs` など)
- function availability (`hashFiles`, `success`, `failure`, `always`, `cancelled` など)
- type suitability
- dynamic property existence / strictness
- GitHub domain semantics を伴う availability/type diagnostics

この線引きだと、次の説明が成立する。

- parser は「何が書かれているか」を読む
- linter は「GitHub Actions 上で妥当か」を判定する

これは直感にも合う。

---

## 5. 判断理由

### 5.1 GitHub context 情報は YAML 構文ではない

`github.event`, `matrix`, `needs`, `hashFiles()` の使用可否は YAML syntax ではなく GitHub Actions の runtime/authoring semantics である。

そのため parser より linter に近い。

### 5.2 Rule-configurable であるべき性質に近い

availability/type diagnostics は、少なくとも将来的には次と相性がよい。

- severity override
- rule enable/disable
- suppression
- document-kind / position ごとの運用ポリシー差

これは linter の契約と整合的。

### 5.3 Parser spec の肥大化を防げる

現行 parser spec は expression grammar だけでなく、availability matrix や function availability まで抱えている。

これを続けると parser spec が GitHub Actions domain spec に近づきすぎる。

### 5.4 Parser 単体価値は維持できる

syntax parse と expression AST 構築を parser に残せば、parser 単体利用の価値はまだ高い。

完全移管ほどの破壊はない。

---

## 6. 優先度別の対応策

### P0 — 現状棚卸し

- parser が出している expression-related diagnostics を一覧化
- linter rule 側で expression semantics を見ている箇所を一覧化
- parser/linter の重複領域を表形式で整理

最低限整理すべき分類:

1. syntax-only
2. context availability
3. function availability
4. property existence
5. type suitability
6. security/policy rule

完了条件:

- 各診断カテゴリの現所属と移管候補が見える

### P1 — Spec 上の責務再定義

- `Seiton_spec.md` の責務表を更新
- `Seiton_Parser_spec.md` から GitHub domain semantics を段階的に薄くする方針を書く
- `Seiton_Linter_spec.md` に expression semantic validation の owning responsibility を追加する

この段階ではまだ実装を移さず、**将来の移行先を spec で固定する**。

完了条件:

- parser/linter 境界が spec 上で説明可能になる

### P2 — 実装の段階移行

- parser 側 validation を syntax-centered に限定
- availability/type/property diagnostics を linter 側 rule または shared lint-phase validator に移す
- 既存 diagnostic messages / ranges / severity をなるべく維持する

このフェーズは破壊範囲が大きいため、1 PR に詰め込まない。

完了条件:

- parser 単体は syntax/AST に集中
- semantic diagnostics は linter 側から出る

### P3 — Config / suppression の最適化

- `expr-undefined-var` との責務重複を再整理
- 将来的に availability/type を rule として完全に config/suppression 対象にするか検討
- parser diagnostics から linter diagnostics に移ることで UX が悪化しないか確認

完了条件:

- semantic diagnostics の運用ポリシーが linter contract に収まる

---

## 7. 実装方式候補

### 方式 1 — 専用 lint-phase validator

rule ではなく、linter pass 前の shared semantic validator を置く。

利点:

- parser から linter への移管はしやすい
- rule catalog をむやみに増やさずに済む

欠点:

- linter なのに rule でない責務が増える

### 方式 2 — 既存 rule に統合

`expr-undefined-var` を拡張して availability/property/type の主体にする。

利点:

- ルールとして扱いやすい
- config/suppression に自然に乗る

欠点:

- rule の責務が大きくなりやすい

### 方式 3 — shared analyzer + rule facade

共通 analyzer は linter 層に置き、rule はその結果を診断化する facade とする。

利点:

- analyzer の再利用性が高い
- rule/config との両立がしやすい

欠点:

- 設計レイヤが 1 つ増える

**推奨は方式 3**。

---

## 8. 互換性リスク

このタスクは parser/linter 境界に触れるため、次のリスクがある。

- parser-only consumer の診断セットが変わる
- fatal parse 後の short-circuit 条件との兼ね合いが変わる
- diagnostic category / wording / ordering が変わる可能性がある
- benchmark 上、parser は軽くなり linter は重くなる可能性がある

したがって、P1 で spec を先に固定し、P2 は別 PR に分けるべき。

---

## 9. この task の推奨結論

長期的には、**expression syntax は parser、GitHub Actions 意味論としての availability/type/property validation は linter** に置くのが最も自然である。

現行設計は仕様上は一貫しているが、責務境界としてはやや parser 側に寄りすぎている。したがって、今後の改善方針は「全面撤去」ではなく、**parser から linter への段階的な責務再分配** を採るのが妥当である。
