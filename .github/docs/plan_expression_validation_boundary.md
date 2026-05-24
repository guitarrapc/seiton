# Expression Validation の Parser/Linter 境界見直し — 調査分析結果と優先度付きフェーズ実装計画

> 作成日: 2026-05-23
> 対象: expression validation の parser/linter 境界を将来公開 API・独自 rule 実装・性能要件まで含めて再定義する
> 結論: 長期方針は A ではなく、 refined C を採る

---

## 1. この文書の目的

この文書は、expression validation の責務を parser と linter のどちらが持つべきかを再整理し、以後の実装を進めるための判断材料と段階的な計画をまとめたものである。

本タスクでは、単に現行実装を説明するだけでは足りない。次の観点まで含めて整合する必要がある。

- Seiton を parser/linter ライブラリとして公開したときの API 一貫性
- custom rule を実装する利用者にとっての拡張しやすさ
- parser 単体利用時の価値
- spec/test/implementation の同期しやすさ
- parser/linter 双方の速度と allocation の継続的改善

特に性能面では、単に退行を避けるだけでなく、各フェーズで現状よりよい実装を狙う。

- 速度改善を追う
- allocation 改善を追う
- 少なくともメモリ悪化は許容しない

---

## 2. 比較対象の定義

この文書では、expression validation の境界案として A、B、C、refined C を比較する。後続の比較が前提なしに見えないよう、ここで各案の意味を明示する。

### 2.1 A — 現行維持

A は、現在の parser/linter 境界を基本的に維持する案である。

- parser が expression syntax を parse する
- parser が context availability / function availability / type validation / property validation の一部も担う
- linter は parser diagnostics を受け取りつつ、rule ごとの文脈依存診断を追加する

この案では、parser は expression front-end に留まらず、GitHub Actions 文脈に踏み込んだ semantic validation も継続して担当する。

### 2.2 B — 完全移管

B は、expression syntax を含めて expression validation 全体を linter 側へ全面移管する案である。

- parser は YAML structural parse と AST 構築に集中する
- expression parse 自体も linter 側が担う、または linter 側の phase に従属させる
- parser 単体では expression diagnostics を基本的に持たない

この案は責務分離は明快だが、parser 単体価値を大きく下げ、linter に expression front-end まで抱え込ませるため、本計画では採らない。

### 2.3 C — ハイブリッド再分離

C は、expression syntax と GitHub Actions 文脈依存 semantics を分け、parser と linter の責務を再配分する案である。

- parser は expression syntax parse と AST 構築に集中する
- linter は availability / property / type などの GitHub Actions 文脈依存 validation を担う
- parser-only consumer の価値を保ちながら、semantic diagnostics を linter contract に寄せる

ただし、このままでは「parser がどの expression artifact を構築して linter に渡すか」が十分に具体化されていない。

### 2.4 refined C — 本計画で採る具体案

refined C は、上の C を実装計画に落とし込める粒度まで具体化した案である。

- parser は YAML + expression front-end として振る舞う
- parser は expression occurrence、expression AST / IR、site metadata など、linter や custom rule が再利用できる成果物を持つ方向に進む
- parser は expression-language intrinsic validation を担う
- linter は workflow-aware semantic model を構築し、GitHub Actions 文脈依存 validation を担う
- semantic diagnostics は rule/config/suppression/severity の世界に収める

つまり refined C は、単なる「semantic を linter に寄せる案」ではなく、**parser が何を作り、linter が何を評価し、library / custom rule が何を使えるか** まで含めて定義した具体案である。

---

## 3. 結論

### 3.1 採用方針

長期的に望ましいのは、現行維持の **A** ではなく、責務を明確に再分離した **refined C** である。

ただし、ここで言う C は単なる「semantic validation を linter に寄せる」という一般論ではない。実際には次の設計方針を含む。

- parser は YAML + expression front-end として責務を持つ
- linter は GitHub Actions 文脈を解釈する semantic phase として責務を持つ
- parser は linter が再利用できる expression artifact を返す方向に進む
- linter はその artifact と workflow AST を使って availability / property / type / policy を評価する

### 3.2 A を採らない理由

A は短期互換性では有利だが、将来にわたって次の問題を固定化する。

- parser spec が GitHub Actions domain knowledge を抱え続ける
- parser diagnostics と linter diagnostics の重複前提が残る
- suppression / severity / enable-disable の境界が曖昧なままになる
- custom rule 作者が expression を再 parse / 再解釈しやすい構造にならない
- parser/linter の公開 API 説明が直感に反する

### 3.3 refined C を採る理由

refined C は次の点で最も整合的である。

- parser は「何が書かれているか」を返す
- linter は「GitHub Actions 上で妥当か」を判定する
- parser-only consumer には syntax / AST / expression IR の価値が残る
- semantic diagnostics は config / suppression / severity override と自然に結び付く
- custom rule は parser 由来の構造化データを利用できる
- 将来の public API 設計が単純になる

---

## 4. 調査分析結果

### 4.1 現行仕様は一貫しているが、境界は過密である

現行 spec では parser 側に expression semantic analysis が明示的に含まれている。そのため、現行設計は仕様上は一貫している。

一方で、その一貫性は「責務分離として自然か」とは別問題である。現行の parser は expression syntax だけでなく、GitHub Actions 固有の availability / function restriction / dynamic context / type diagnostics の一部まで担っており、linter も同じ問題領域を別解像度で扱っている。

結果として、現在は「仕様上は一貫」「設計上は重複」という状態になっている。

### 4.2 現行実装では parser/linter の二重評価が制度化されている

現行実装の実態は次のとおりである。

- parser は scalar parse 中に expression を inline parse / validate する
- linter は rule 実行中に expression を再 parse し、より文脈依存な validation を行う
- dynamic context は lint 時点で workflow/job/step 情報から override される
- parser/linter の診断重複を dedup 前提で処理している

つまり、重複は偶発的なものではなく、現行仕様と実装の両方で前提化されている。

### 4.3 現行の利点

現行設計にも実利はある。

- parser 単体で expression diagnostics がある程度得られる
- linter は parser が expression を見つけてくれる前提で組める
- syntax error と基礎 semantic error を parser で早期に返せる

この利点は捨てるべきではない。したがって、B のような全面移管は採らない。

### 4.4 現行の問題点

現行の主な問題点は次のとおりである。

1. parser が GitHub Actions 文脈依存の意味論を抱えすぎている
2. parser と linter が同一領域を違う粒度で診ている
3. dynamic context 解決が lint phase 依存なのに、parser spec 側に semantic 責務が厚く残っている
4. custom rule 観点では expression artifact が parser 成果物として十分に露出していない
5. linter 側で expression 再 parse / 再評価が多く、設計と性能の両面で非対称がある

---

## 5. parser/linter の重複領域整理

### 5.1 診断カテゴリ別の現状整理

| カテゴリ | 現在 parser | 現在 linter | 重複度 | 将来の主担当 |
|---|---|---|---|---|
| syntax-only | 担当 | 一部 rule が parse 結果を前提に消費 | 低 | parser |
| built-in function existence / arity / overload | 担当 | 一部 rule が型推論目的で再利用 | 中 | parser |
| root context availability | 担当 | `expr-undefined-var` でも担当 | 高 | linter |
| function availability by workflow position | 担当 | `expr-undefined-var` でも担当 | 高 | linter |
| dynamic property existence | 一部担当 | `expr-undefined-var` が strict override で担当 | 高 | linter |
| type suitability for workflow site | 一部担当 | `expr-undefined-var` が workflow site aware に担当 | 高 | linter |
| operator local type validity | 担当 | `expr-undefined-var` が override-aware に再評価 | 高 | 原則 parser、必要に応じて linter 補完 |
| security / policy semantics | 非担当 | 各 rule が担当 | 低 | linter |

### 5.2 重複の本質

重複の本質は「parser が syntax 以上の semantic を持っていること」ではなく、**GitHub Actions 文脈がないと正確に判定できない問題を parser 側にも置いていること** にある。

特に次は linter 側が自然である。

- その式が書かれている workflow position
- matrix / needs / steps / inputs の具体的 shape
- local action / local reusable workflow から得られる出力情報
- config / suppression / severity override との結び付き

---

## 6. 現在の仕様で考慮不足な点

### 6.1 parser が linter に何を渡すかの契約が弱い

現状の spec は parser が expression parsing と semantic typing data を所有すると書いているが、将来の public API / custom rule 目線では「何が parser 成果物として再利用可能なのか」が十分に定義されていない。

今後は次の観点を spec に明示する必要がある。

- expression occurrence がどの YAML site に属するか
- expression AST / IR を parser 成果物として扱うか
- linter が expression を再 parse せずに済む contract を持てるか
- parser-only consumer が expression 情報をどう参照できるか

### 6.2 syntax と semantic の境界では粗すぎる

今回の再分離では、単純に「syntax は parser、semantic は linter」と切るのは粗すぎる。

実際には次のように分けるべきである。

- expression language 自体に属する妥当性: parser
- GitHub Actions workflow position / dynamic context に依存する妥当性: linter

この分離でないと、parser から too much を削りすぎるか、逆に linter が過剰に parser の仕事を背負う。

### 6.3 custom rule 実装者向け contract が不足している

将来ライブラリとして公開するなら、custom rule 作者が欲しいのは次である。

- typed workflow AST
- expression occurrence / expression AST
- site metadata
- rule から参照できる semantic model

単に `StringNodeId` を decode できるだけでは十分ではない。custom rule が毎回 expression を再検出 / 再 parse / 再解釈する設計は、拡張性・性能の両方で不利である。

### 6.4 parser-only consumer の期待値整理が必要

parser-only consumer にどこまでの価値を保証するかを決める必要がある。

今後の契約としては、parser-only consumer には少なくとも次を保証するのが妥当である。

- YAML structural diagnostics
- expression syntax diagnostics
- expression AST / occurrence metadata
- expression-language intrinsic validation

一方で、GitHub Actions 文脈依存の availability/property/type は parser-only consumer の責務から外してよい。

---

## 7. 目標アーキテクチャ

### 7.1 parser が構築すべきもの

parser は最終的に次を構築する層として整理する。

1. YAML AST
2. expression occurrence index
3. expression AST / IR
4. source range / token / site metadata
5. expression-language intrinsic diagnostics

ここでいう expression-language intrinsic diagnostics は次を含む。

- expression syntax error
- unknown function
- function arity mismatch
- function overload mismatch
- expression grammar 上の局所的な不整合
- workflow position に依存しない operator-level validation

### 7.2 linter が構築・評価すべきもの

linter は parser 成果物と workflow AST を受け取り、次を担当する。

1. workflow-aware semantic model 構築
2. dynamic context resolution
3. context availability validation
4. function availability validation by workflow position
5. dynamic property strictness
6. workflow site aware type suitability
7. security / policy rule
8. suppression / severity / enable-disable / config

### 7.3 shared analyzer + rule facade を採る

実装方式は、現時点では **shared analyzer + rule facade** が最も妥当である。

- analyzer は linter 層に置く
- analyzer は workflow-aware semantic model を入力に取る
- rule は analyzer 結果を diagnostics として surface する facade になる
- config / suppression / severity は rule 経由で扱う

これにより、rule catalog を不必要に肥大化させず、かつ rule contract に自然に載せられる。

### 7.4 将来の公開 API の方向性

将来的に Seiton.Core を library として整える場合、少なくとも次の方向性を取る。

- parser API は reusable である
- linter API は pre-parsed result を受けられる方向に寄せる
- custom rule は parser 成果物と semantic model を利用できる
- parser / linter を façade API で束ねても、内部 contract は分離されたままである

---

## 8. A と refined C の比較

| 観点 | A: 現行維持 | refined C |
|---|---|---|
| 短期互換 | 最も高い | 中程度 |
| parser 単体価値 | 高いが責務過剰 | 高いまま整理可能 |
| linter の自然さ | 低い | 高い |
| suppression / severity との整合 | 低い | 高い |
| custom rule 実装しやすさ | 低い | 高い |
| public API の説明しやすさ | 低い | 高い |
| spec の保守性 | 低い | 高い |
| parser 側 domain knowledge 蓄積 | 継続する | 抑制できる |
| 実装移行コスト | 小さい | 中程度 |
| 長期最適性 | 低い | 高い |

結論として、短期安定性は A が優位だが、長期の API / 拡張性 / 仕様保守性では refined C が明確に優位である。

---

## 9. 実装に先立つ基本方針

### 9.1 spec-first で進める

今後の進め方は、**仕様更新作業から先に進める**。

理由は次のとおりである。

- parser/linter boundary を実装だけ先に動かすと downstream spec が崩れる
- parser-only consumer と lint consumer の contract が曖昧なままになる
- public API / custom rule contract を後付けにすると設計の辻褄合わせになる

したがって、まず contract を spec で固定し、その後の実装はその contract を満たすように進める。

### 9.2 red-first test を実装フェーズの原則にする

実装フェーズは **red-first test** を原則にする。

各 PR / 各フェーズでは、次の順序を守る。

1. 先に failing test を書く
2. 境界変更の意図を test で固定する
3. 最小実装で green にする
4. 関連する focused benchmark / allocation check を回す
5. 必要なら spec/doc を同一スコープで更新する

### 9.3 性能方針

本計画に基づく実装では、性能制約を常時適用する。

- parser success path で新たな string materialization を増やさない
- parser/linter の hot path に新しい `List<T>` / `new T[]` / growth path を持ち込まない
- expression artifact 導入時も zero-copy / pooled / per-run cache を前提に設計する
- rule 側の expression 利用は再 parse を減らす方向に寄せる
- benchmark で allocation 悪化が出る案は不採用とする

特に **allocation 悪化は不許可** とする。速度改善だけを理由に heap pressure が増える設計は採らない。

---

## 10. 優先度付きフェーズ実装計画

## Phase 0 — 調査結果の contract 化

### 目的

境界再定義の結論を spec 上で固定し、今後の実装のブレを防ぐ。

### このフェーズで行うこと

1. `Seiton_spec.md` の責務表を refined C に合わせて更新する
2. `Seiton_Parser_spec.md` に parser-owned / linter-owned の再定義を反映する
3. `Seiton_Linter_spec.md` に expression semantic validation の owning responsibility を明記する
4. `Seiton_Parser_csharp_spec.md` / `Seiton_Parser_go_spec.md` / `Seiton_Linter_csharp_spec.md` / `Seiton_Linter_go_spec.md` を同期する
5. implementation plan 文書も boundary 再定義に合わせて更新する

### このフェーズでまだやらないこと

- production code の責務移管
- diagnostics の実際の移動
- public API 破壊的変更

### 完了条件

- refined C が spec 上で明文化されている
- parser/linter の責務説明が一文で説明できる
- downstream spec との矛盾がない

### 性能条件

- spec-only change のため benchmark 変更なし
- 次フェーズ以降の性能 acceptance criteria を文書化する

---

## Phase 1 — 現状棚卸しと移行単位の固定

### 目的

責務移管を一括で行わず、診断カテゴリ単位に切り分けて実装可能な単位に分解する。

### このフェーズで行うこと

1. parser が出している expression 関連診断を分類する
2. linter rule が出している expression 関連診断を分類する
3. parser/linter 重複領域を診断カテゴリ表として確定する
4. 各カテゴリについて「parser に残す / linter に移す / 二段階残置」の判断を明示する
5. 移行順序を低リスク順に固定する

### 最低限固定するカテゴリ

1. syntax-only
2. built-in function validity
3. context availability
4. function availability by position
5. dynamic property existence
6. workflow site aware type suitability
7. security / policy rule

### 棚卸し結果 — 診断カテゴリ表

#### Parser 側 (ExpressionSemanticAnalyzer + ExpressionParser)

| カテゴリ | 診断メッセージパターン | メソッド | 将来 owner |
|---|---|---|---|
| syntax-only | unexpected token at position {pos} | ExpressionParser.Parse | **parser** |
| syntax-only | operator '!' requires an operand | ExpressionParser.ParseUnary | **parser** |
| syntax-only | unexpected end of expression | ExpressionParser.ParsePrimary | **parser** |
| syntax-only | missing closing ')' | ExpressionParser.ParsePrimary | **parser** |
| syntax-only | got unexpected character '"'; only single quotes are available | ExpressionParser.ParsePrimary | **parser** |
| syntax-only | member name is missing after '.' | ExpressionParser.ParsePrimary | **parser** |
| syntax-only | missing closing ']' after wildcard index | ExpressionParser.ParsePrimary | **parser** |
| syntax-only | missing closing ']' in index access | ExpressionParser.ParsePrimary | **parser** |
| syntax-only | expected ',' or ')' in function call | ExpressionParser.ParsePrimary | **parser** |
| syntax-only | index expression is missing | ExpressionParser.ParseIndexExpression | **parser** |
| syntax-only | unterminated string literal | ExpressionParser.ParseStringLiteral | **parser** |
| syntax-only | operator '{op}' requires both operands | ExpressionParser.AddBinary | **parser** |
| function-intrinsic | unknown expression function: {name} | ValidateFunctionCall | **parser** |
| function-intrinsic | function '{name}' expects {n} argument(s), but got {argCount} | ValidateFunctionCall | **parser** |
| function-intrinsic | function '{name}' expects {min}-{max} argument(s), but got {argCount} | ValidateFunctionCall | **parser** |
| function-intrinsic | argument {index} should be {expectedType}, but got {actualType} | ValidateFunctionCall | **parser** |
| function-intrinsic | format placeholder '{{{i}}}' requires argument {i+1}... | ValidateFormatPlaceholders | **parser** |
| function-intrinsic | format string does not contain placeholder {i}; remove argument... | ValidateFormatPlaceholders | **parser** |
| function-intrinsic | fromJSON() argument is not valid JSON: {msg} | ValidateFromJsonLiteral | **parser** |
| operator-local | {leftType} value cannot be compared to {rightType} value with '{op}' | ValidateCompareOp | **parser** |
| operator-local | operator '!' does not support {type} type | ValidateUnaryOp | **parser** |
| operator-local | receiver of '.*' must be an object or array, but got {type} | ValidateWildcardAccess | **parser** |
| operator-local | index of array must be number, but got {type} | ValidateIndexAccess | **parser** |
| operator-local | index of object must be string, but got {type} | ValidateIndexAccess | **parser** |
| dynamic-property | property "{prop}" is not defined in {contextLabel}... | ValidatePropertyAccess | **linter** (transitional: parser) |
| dynamic-property | receiver of object dereference "{prop}" must be type of object but got "{type}" | ValidatePropertyAccess | **linter** (transitional: parser) |
| dynamic-property | configuration variable name '{name}' must not start with 'GITHUB_' | ValidateVarsNamingConvention | **parser** |
| dynamic-property | configuration variable name '{name}' contains invalid characters | ValidateVarsNamingConvention | **parser** |
| type-suitability | {type} value in ${{ }} will be converted to string "[Object]" | CheckTypeForTemplate | **linter** (transitional: parser) |
| type-suitability | array value in ${{ }} will be converted to string "[Array]" | CheckTypeForTemplate | **linter** (transitional: parser) |
| type-suitability | null value in ${{ }} will be converted to empty string | CheckTypeForTemplate | **linter** (transitional: parser) |
| type-suitability | {type} value cannot be expanded as mapping for "env:" section | CheckEnvMappingType | **linter** (transitional: parser) |
| type-suitability | type of expression at "runs-on" must be string or array but found type "{type}" | CheckRunsOnType | **linter** (transitional: parser) |
| type-suitability | type of expression at "{sectionName}" must be object but found type {type} | CheckExpectedObjectType | **linter** (transitional: parser) |

#### Linter 側 (ExprUndefinedVarRule)

| カテゴリ | 診断メッセージパターン | メソッド | 将来 owner |
|---|---|---|---|
| context-availability | context "{rootName}" is not allowed here. available contexts are... | VisitExpressionNode | **linter** |
| context-availability | context "{rootName}" is not allowed here. undefined context... | VisitExpressionNode | **linter** |
| function-availability | function "{funcName}" is not allowed here... only available in "if" conditions | VisitExpressionNode | **linter** |
| function-availability | function "hashFiles" is not allowed here... only available in step-level | VisitExpressionNode | **linter** |
| dynamic-property | property "{prop}" is not defined in {contextLabel}... | ValidatePropertyAccessWithOverrides | **linter** |
| dynamic-property | receiver of object dereference "{prop}" must be type of object... | ValidatePropertyAccessWithOverrides | **linter** |
| type-suitability | {type} value in ${{ }} will be converted to string "[Object]" | CheckTemplateTypeWithOverrides | **linter** |
| type-suitability | array/null template conversions | CheckTemplateTypeWithOverrides | **linter** |
| type-suitability | {type} cannot be expanded as mapping for "env:" | CheckEnvMappingType | **linter** |
| type-suitability | type of expression at "runs-on" must be string or array... | CheckRunsOnType | **linter** |
| type-suitability | type of expression at "{section}" must be object... | CheckExpectedObjectType | **linter** |
| operator-with-overrides | index of array must be number, but got {type} | ValidateIndexAccessWithOverrides | **linter** |
| operator-with-overrides | index of object must be string, but got {type} | ValidateIndexAccessWithOverrides | **linter** |
| operator-with-overrides | {left} value cannot be compared to {right} with '{op}' | ValidateCompareOpWithOverrides | **linter** |
| operator-with-overrides | {ordinal} argument of function call is not assignable... | ValidateFunctionCallWithOverrides | **linter** |

#### 重複整理と移行判断まとめ

| カテゴリ | Parser に残す | Linter に移す | 重複度 | 移行順 |
|---|---|---|---|---|
| syntax-only | ✓ | - | なし | - (不動) |
| function-intrinsic (existence/arity) | ✓ | - | なし | - (不動) |
| operator-local (static type) | ✓ | - | 低 | - (不動) |
| vars naming convention | ✓ | - | なし | - (不動) |
| context-availability | - | ✓ (既に linter 専任) | なし | - (完了) |
| function-availability | - | ✓ (既に linter 専任) | なし | - (完了) |
| dynamic-property existence | 二段階残置 → linter | ✓ | 高 | 1st |
| type-suitability | 二段階残置 → linter | ✓ | 高 | 2nd |
| operator-with-overrides | 二段階残置 → linter | ✓ | 高 | 3rd |

**重要な発見**: context-availability と function-availability は既に parser 側で発行されておらず linter 専任である。実質的に移行が必要なのは dynamic-property / type-suitability / operator-with-overrides の 3 カテゴリのみ。

### Benchmark Baseline (Phase 1 時点)

| Benchmark | Size | Mean | Allocated |
|---|---|---|---|
| WorkflowParser.Parse | Small | 42.883 us | 3.87 KB |
| WorkflowParser.Parse | Medium | 1,000.764 us | 35.59 KB |
| WorkflowParser.Parse | Large | 15,876.122 us | 180.04 KB |
| ExpressionExtractor | Small | 3.749 us | 2.92 KB |
| ExpressionExtractor | Medium | 44.919 us | 30.64 KB |
| ExpressionExtractor | Large | 214.319 us | 143.04 KB |
| LintEngine.Check (no fix) | Small | 54.79 us | 8.7 KB |
| LintEngine.Check (no fix) | Medium | 1,229.32 us | 68.89 KB |
| LintEngine.Check (no fix) | Large | 19,033.07 us | 327.41 KB |
| LintEngine.Check (fix) | Small | 60.30 us | 10.15 KB |
| LintEngine.Check (fix) | Medium | 1,683.93 us | 82.25 KB |
| LintEngine.Check (fix) | Large | 28,201.26 us | 382.25 KB |

環境: .NET 10.0.8, AMD Ryzen 9 7950X3D, Windows 11

### 完了条件

- 各カテゴリの owner と移行順序が表で見える ✓
- どのカテゴリを parser に残すかが曖昧でない ✓

---

## Phase 2 — red-first test / benchmark gate の先行整備

### 目的

以後の責務移管を、仕様準拠と性能制約の両面で安全に進められるようにする。

### このフェーズで行うこと

1. expression boundary 用の regression test 群をカテゴリ別に整備する
2. parser-only expectation と lint expectation を分けて固定する
3. duplicate diagnostics / replacement behavior を test で固定する
4. benchmark の比較手順をフェーズごとに明文化する
5. parser / lint hot path に関する allocation guard を review checklist に組み込む

### test 原則

- 各実装 PR は red-first で開始する
- one category at a time で移す
- false positive / false negative の両方を test する
- security-sensitive rule では negative test 数が positive test 数以上であることを守る

### benchmark 原則

- parser 関連変更では parser benchmark を比較する
- linter 関連変更では lint benchmark を比較する
- Mean と Allocated の両方を比較する
- Allocated が悪化した案は採用しない

### 完了条件

- 境界移管 PR を安全に進める test/benchmark gate が揃っている

---

## Phase 3 — parser 成果物の再利用性向上

### 目的

linter や custom rule が expression を再 parse しなくてもよい方向に、parser の成果物 contract を強化する。

### このフェーズで行うこと

1. expression occurrence / site metadata の扱いを整理する
2. expression AST / IR を parser 成果物として再利用できる形を検討・導入する
3. linter が parser 成果物を受け取って評価できる内部 contract を整える
4. 将来 public API として公開可能な surface を設計する

### このフェーズの設計原則

- success path で余分な allocation を増やさない
- expression artifact は zero-copy / pooled 前提で持つ
- parser 側で linter 専用の heavyweight object graph を毎回構築しない
- current hot path より heap pressure を増やさない

### 完了条件

- linter 側が parser 成果物を使う migration path を持つ
- custom rule / public API へ展開可能な内部 contract が見える

---

## Phase 4 — lint semantic analyzer 導入

### 目的

GitHub Actions 文脈依存の semantic validation を linter 層へ寄せるための shared analyzer を整備する。

### このフェーズで行うこと

1. workflow-aware semantic model を linter 層に導入する
2. dynamic context resolution を analyzer 側に集約する
3. context availability / function availability / dynamic property / site aware type check を analyzer で扱えるようにする
4. rule facade が analyzer 結果を diagnostics 化する形に整える

### 実装原則

- per-run cache を活用する
- 既存の content-hash based expression parse cache は維持または削減する方向に使う
- per-job / per-step の override 配列や辞書は再利用する
- `new T[]` / `List<T>` の増加を避ける

### 完了条件

- linter 側に semantic ownership の受け皿ができる
- rule facade から利用できる

### 性能条件

- lint benchmark で allocation 改善または同等
- parser benchmark に悪影響がない

---

## Phase 5 — 責務の段階移管

### 目的

GitHub Actions 文脈依存の validation を parser から linter に一括ではなくカテゴリ単位で移す。

### 推奨移行順

1. root context availability
2. function availability by workflow position
3. dynamic property existence / strictness
4. workflow site aware type suitability

### 各 PR で守ること

1. 対象カテゴリの failing test を先に書く
2. parser / linter どちらが owner かを spec と test で固定する
3. 既存 diagnostic wording / location / severity は可能な限り維持する
4. benchmark と allocation を比較する
5. parser 側の不要な validation を削除する前に linter 側の受け皿を green にする

### 実施結果

コード調査の結果、4 カテゴリすべてが **既に linter-owned** であることを確認した。

1. **root context availability** — `ExprUndefinedVarRule.VisitExpressionNode` が `Availability.IsRootContextAvailable` で検証。  
   parser 側 `ValidateNode` に `// NOTE: Context availability ... handled by the linter` コメントあり。
2. **function availability by workflow position** — `ExprUndefinedVarRule.VisitExpressionNode` が status function / hashFiles scope を検証。  
   parser 側 `ValidateFunctionCall` に `// NOTE: Status-check function and hashFiles availability checks are handled by the linter` コメントあり。
3. **dynamic property existence / strictness** — `ExprUndefinedVarRule` が `ExpressionSemanticAnalyzer.ValidateDynamicPropertyAccessInline` を呼び出し、per-job override 付きで検証。
4. **workflow site aware type suitability** — `ExprUndefinedVarRule` が `CheckTemplateTypeWithOverrides`, `CheckEnvMappingType`, `CheckRunsOnType` 等を呼び出し。

ExpressionBoundaryTests (Phase 2) で確認済み:
- `ParserOnly_ContextAvailability_DoesNotEmitDiagnostic` → parser は context availability を検証しない
- `Lint_ContextAvailability_EmitsDiagnostic` → linter が検証する
- `ParserOnly_FunctionAvailability_DoesNotEmitDiagnostic` → parser は function availability を検証しない
- `Lint_FunctionAvailability_EmitsDiagnostic` → linter が検証する
- `ParserOnly_StatusFunctionOutsideIf_DoesNotEmitDiagnostic` → parser は status function scope を検証しない
- `Lint_StatusFunctionOutsideIf_EmitsDiagnostic` → linter が検証する

### 完了条件

- parser は syntax / front-end / intrinsic validation に集中している ✅
- GitHub Actions 文脈依存 semantic diagnostics は linter から出る ✅

### 性能条件

- parser は current baseline 以上の allocation を出さない ✅ (3.87/35.59/180.04 KB unchanged)
- linter は増えた仕事量に対しても total allocation を抑制する ✅ (8.7/68.89/327.41 KB unchanged)
- total parse+lint として現状比で少なくとも同等、できれば改善を目指す ✅ (同等)

---

## Phase 6 — 公開 API / custom rule 向けの仕上げ

### 目的

Seiton.Core を parser/linter library として出したときに、利用者が自然に使える contract を整える。

### このフェーズで行うこと

1. pre-parsed result を linter に渡せる surface の整理
2. custom rule が expression artifact と semantic model を利用できる contract の整備
3. façade API の説明と下位 contract の責務分離を docs に反映
4. parser-only / linter-only / combined use case を docs で説明する

### 完了条件

- parser/linter を個別にも組み合わせでも説明できる
- custom rule の実装体験が「AST を読んで自前で全部やり直す」状態ではなくなる

---

## 11. フェーズ横断の性能・品質ゲート

### 11.1 共通品質ゲート

すべての実装フェーズで次を満たす。

1. red-first test で開始する
2. focused test を先に通す
3. full test suite を通す
4. benchmark を比較する
5. spec/doc と implementation を同期する

### 11.2 parser 側の禁止事項

- success path で新しい string decode を増やさない
- hot path に `List<T>` / `Dictionary<TKey, TValue>` growth を増やさない
- per-node allocation を増やさない
- linter 向け convenience のために parser が heavyweight object を毎回構築しない

### 11.3 linter 側の禁止事項

- rule ごとの expression 再 parse を増やす
- shared cache で済む計算を rule ごとに重複させる
- per-job / per-step で新しい heap allocation を増やす
- parser から移した責務の分だけ無制限に allocation を増やす

### 11.4 benchmark gate

原則として、各フェーズで次を確認する。

- parser 変更: parser benchmark
- linter 変更: lint benchmark
- Mean 比較
- Allocated 比較

判定原則:

- Allocated が悪化した場合は原則差し戻し
- Mean が悪化した場合は改善案を優先検討
- 両方改善できる実装を優先採用

---

## 12. この計画で明示的に避けること

1. spec を更新せずにコードだけで責務移管すること
2. expression semantic を一括で parser から剥がすこと
3. parser-only consumer の価値を考えずに linter へ全面移管すること
4. custom rule 利用者の視点を後回しにすること
5. 速度改善だけを理由に allocation を悪化させること
6. 互換性リスクの大きい変更を 1 PR に詰め込むこと

---

## 13. 最終的な判断

本件の長期方針は、**A ではなく refined C** である。

ただし、その実行は「semantic validation を linter に寄せる」とだけ書けば済む話ではない。実際には、次の順で進めるべきである。

1. まず spec を更新して boundary を固定する
2. つぎに棚卸しと red-first test / benchmark gate を整える
3. parser 成果物の再利用性を高める
4. linter 側に shared semantic analyzer を導入する
5. 診断カテゴリごとに責務を段階移管する
6. 最後に public API / custom rule contract を仕上げる

この順序なら、parser/linter の説明が自然になり、library としても rule 拡張基盤としても整合が取りやすい。さらに、性能改善と allocation 抑制を各フェーズの acceptance criteria に組み込める。

したがって、今後は **仕様更新先行 + red-first test ベース + allocation 悪化不許可** を基本原則として、この計画に沿って進める。
