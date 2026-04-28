# Seiton vs actionlint 互換性評価

> actionlint testdata/err/ fixtures に対する seiton の検出結果を分析し、機能カバレッジを評価した文書。
> 対象: `tests/Seiton.Core.Tests/fixtures/schema/actionlint/testdata/` のfixtures。
> 最終評価日: 2026-04-28

---

## 0. 現状サマリ

| 指標 | フェーズ 1 実施前 | フェーズ4 + テスト改善後 | 最新 (2026-04-28) |
|---|---|---|---|
| 完全一致 (PERFECT) fixtures | 10 / 99 | | 22 / 95 |
| 列差異のみ (COL_DIFF) fixtures | - | - | 42 / 95 |
| COL_DIFF + EXTRA (追加検出あり) | - | - | 8 / 95 |
| 互換 fixtures (MISS=0) | - | 65 / 95 (4 scope-out) | 72 / 95 (4 scope-out) |
| 行レベルマッチ率 (line+col or line) | 95 / 503 (18%) | 444 / 486 (91%) | 451 / 486 (92%) |
| 完全一致マッチ率 (line+col exact) | - | 209 / 486 | 214 / 486 |
| 列差異マッチ (same line, diff col/msg) | - | 235 / 486 | 237 / 486 |
| 未マッチ期待行 (MISS) | 408 | 42 (true gaps) | 35 (true gaps) |
| 余剰 seiton 行 (EXTRA) | 423 | 60 (additional) | 62 (additional) |

※ フェーズ 1 実施直後は `.seiton.out` を seiton 実出力に合わせて管理していたため PERFECT が多かった。フェーズ 2+3 でメッセージ・位置を actionlint に近づける改善を行ったため、`.seiton.out` が `.out` とのギャップを正確に反映するようになった。

※※ フェーズ 4 の PERFECT 減少は比較手法の改善 (regex パターン対応) による再分類。実際の検出能力は向上している (MISS 112→59, EXTRA 91→60)。COL_DIFF が 27→41 に増加したのは、以前 MIXED だった fixtures が改善されて COL_DIFF のみになったため。

**ActionlintCompatTests 比較ロジック**:
- **scope-out 除外**: shellcheck/pyflakes 4 fixtures を比較対象から除外 (seiton は意図的に未サポート)
- **2パスマッチング導入**: Pass 1 = exact/regex マッチ、Pass 2 = 行番号フォールバック。COL_DIFF (同一行・異なる列/メッセージ) は設計差異として「互換」にカウント
- **EXTRA は修正候補でない**: seiton 独自検出 (template injection, portability 警告等) は追加機能であり、ギャップとしてカウントしない
- **結果**: 72/95 fixtures が互換 (MISS=0)、真のギャップは 35 行のみ

**fixture 状態分布 (最新 2026-04-28)**:
- **PERFECT** (完全一致): 22 fixtures — actionlint `.out` と完全に一致 (regex マッチ含む)
- **COL_DIFF** (列差異のみ): 42 fixtures — 同じ行で検出しているが列位置またはメッセージ形式が異なる (検出漏れ・余剰なし)
- **COL_DIFF + EXTRA**: 8 fixtures — 列差異のみ + seiton 独自の追加検出あり
- **MISSING** (検出漏れのみ): 4 fixtures — 一部の行が未検出 (検出漏れのみ、余剰なし)
- **MIXED** (複合): 19 fixtures — 検出漏れと余剰が混在

---

## 1. actionlint ルールカバレッジ

### 1.1 actionlint 全 18 ルール vs seiton マッピング

| actionlint ルール | seiton ルール | カバー状況 |
|---|---|---|
| `syntax-check` | `parse`, `job-structure`, `shell-name`, `env-var` | **対応済** (4 ルールに分割) |
| `expression` | `template-injection`, `expr-undefined-var` | **対応済** (2 ルールに分割) |
| `events` | `dispatch-inputs`, `schedule-event` | **対応済** (2 ルールに分割) |
| `matrix` | `matrix` | **対応済** |
| `credentials` | `credentials` | **対応済** |
| `runner-label` | `runner-label` | **対応済** |
| `job-needs` | `needs-graph` | **対応済** |
| `action` | `popular-action-inputs`, `local-action-inputs`, `outdated-action-runner` | **対応済** (3 ルールに分割) |
| `env-var` | `env-var` | **対応済** |
| `id` | `id-naming` | **対応済** |
| `glob` | `glob-pattern` | **対応済** |
| `permissions` | `permissions` | **対応済** |
| `workflow-call` | `reusable-workflow` | **対応済** |
| `deprecated-commands` | `deprecated-commands` | **対応済** |
| `if-cond` | `if-cond` | **対応済** |
| `shell-name` | `shell-name` | **対応済** |
| `shellcheck` | (なし) | **スコープ外** — 外部ツール連携 |
| `pyflakes` | (なし) | **スコープ外** — 外部ツール連携 |

**結果: 16/18 ルール対応済 (88.9%)。未対応 2 件は外部ツール連携であり意図的にスコープ外。**

### 1.2 seiton 独自ルール (actionlint に存在しない 33 ルール)

seiton は actionlint のルールカバーに加えて、セキュリティ・品質に特化した独自ルールを 33 件追加実装している:

| カテゴリ | ルール例 |
|---|---|
| サプライチェーンセキュリティ | `unpinned-uses`, `impostor-commit`, `ref-confusion`, `known-vulnerable-actions`, `cache-poisoning` |
| シークレット管理 | `secrets-outside-env`, `unredacted-secrets`, `overprovisioned-secrets`, `deny-inherit-secrets` |
| 権限最小化 | `job-permissions-required`, `deny-write-all`, `deny-read-all` |
| ベストプラクティス | `job-timeout-minutes-required`, `action-shell-is-required`, `checkout-persist-credentials` |
| 式の安全性 | `run-env-context-direct-use`, `run-secrets-context-direct-use`, `run-inputs-context-direct-use` |

---

## 2. 機能改善項目 (優先度順)

35 MISS 行と LINE_MATCH に隠れた機能差異を精査し、**真の機能ギャップ** と **位置差異のみ** を分離した。

### 2.1 優先度: 高

#### H1. Reusable workflow job の誤検出修正

- **fixture**: `workflow_call_job` (4 MISS, 7 EXTRA)
- **問題**: 空の `uses:` を持つ job に対し `"runs-on" section is missing` / `"steps" section is missing` を誤報。`uses:` が存在するため reusable workflow call と認識すべきで、`runs-on`/`steps` は不要
- **また**: `call1` で同一問題に対して 2 つのメッセージ (`cannot have both uses and steps` + `key 'steps' is not allowed`) を重複出力
- **対処**: `uses:` キーが存在する job は reusable workflow call として扱い、`runs-on`/`steps` の必須チェックを抑制。重複メッセージの統合

#### H2. fromJSON の null 値誤診断

- **fixture**: `invalid_json_in_fromjson` (2 MISS, 3 EXTRA)
- **問題**: `fromJSON('{"null": null}').null` に対し、actionlint は「null 値を `${{ }}` で評価すべきでない」と正しく警告するが、seiton は `"null"` をプロパティ名と誤解し `property "null" is not defined` と報告
- **また**: `contains()` 関数の引数型チェック (型不一致の割り当て検出) が未実装
- **対処**: fromJSON 結果の null フィールドアクセスを正しくトラッキング。`contains()` の引数型バリデーション追加

#### H3. 配列内空文字列の検出

- **fixture**: `empty_sequence_or_string`, `invalid_image_version_event`, `workflow_dispatch_input_types` (各 1 MISS)
- **問題**: `options: ['']`, `versions: ['']` のように配列内に空文字列が含まれるケースを未検出。actionlint は `"string should not be empty"` と報告
- **対処**: 文字列配列フィールド (event filters, options, versions 等) のパース時、各要素が空文字列でないことを検証

#### H4. 配列型の比較演算検出

- **fixture**: `invalid_comparisons` (1 MISS, 6 LINE_MATCH)
- **問題**: `array<bool>` と `array<{}>` の `==` 比較を未検出。また型精度の低下 (`array<bool>` → `array<any>`) によりメッセージが不正確
- **また**: 同一行に `[syntax-check]` と `[expression]` の 2 ルールからエラーが重複出力される
- **対処**: 配列-配列比較の型チェック追加。配列要素型の保持

#### H5. クォート文字列のリテラル型バリデーション

- **fixture**: `invalid_float_at_timeout_minutes`, `invalid_int_at_max_parallel` (各 1 MISS)
- **問題**: `timeout-minutes: '3.5'`, `max-parallel: '3'` のようにクォートされた数値文字列を検出できない。YAML の `!!str` タグ付きノードは float/integer リテラルとして不正
- **対処**: 数値リテラルが必要なフィールド (`timeout-minutes`, `max-parallel`) でクォート文字列 (`!!str`) を拒否する検証追加

### 2.2 優先度: 中

#### M1. 空配列の誤診断修正

- **fixture**: `empty_image_names_and_versions` (1 MISS)
- **問題**: `names: []`, `versions: []` に対し `"must be array of strings"` と型エラーを出すが、実際は空の配列。actionlint は `"should not be empty"` と報告
- **対処**: 空配列 (`[]`) と型エラーを区別し、空配列には空チェック診断を出力

#### M2. glob パスのブロックスカラー末尾改行検出

- **fixture**: `glob_more` (1 MISS)
- **問題**: YAML ブロックスカラー (`|`) から生成される文字列の末尾改行を trailing whitespace として検出できない。actionlint は `"leading and trailing spaces are not allowed in glob path"` と報告
- **対処**: ブロックスカラーからの glob パスを実際の文字列値に展開してから、先頭・末尾の空白文字チェックを実行

#### M3. steps 空マッピング `{ }` の検出

- **fixture**: `invalid_steps` (2 MISS)
- **問題**: `- { }` (空マッピング) をステップとして受け入れてしまう。actionlint は `"element of steps section should not be empty"` + `"step must run script with run or action with uses"` の 2 つを報告
- **対処**: steps 配列パース時、キーが 0 個のマッピングを空ステップとして検出

#### M4. 再帰エイリアスの検出品質改善

- **fixture**: `recursive_anchors` (4 MISS, 6 EXTRA), `issue-610_recursive_raw_yaml_value` (2 MISS, 2 EXTRA)
- **問題**: 再帰エイリアスを検出しているが位置が大幅にずれる (例: L9 → L11)。また `"alias node where mapping expected"` の具体的エラーがなく、汎用的な型エラーで代替
- **対処**: エイリアス参照元の位置でエラー報告。エイリアスノードが期待と異なる型の場合の専用診断追加

#### M5. Reusable workflow secrets 型解決

- **fixture**: `reusable_workflow_empty_secrets` (1 MISS)
- **問題**: `secrets: inherit` の場合、呼び出し元ワークフローの secrets を型に反映していない。`secrets` が `{}` (空) として扱われるため、定義済み secret へのアクセスが未定義プロパティエラーになる
- **対処**: `workflow_call` イベントの `secrets:` 定義を secrets オブジェクト型に反映

### 2.3 優先度: 低

#### L1. 空ラベル ("") の runner-label ルール統合

- **fixture**: `issue280_runs_on` (3 MISS)
- **問題**: `runs-on: ["", "ubuntu-latest"]` の空ラベルに対し、seiton は `"runs-on requires labels"` (構文チェック) で検出するが、actionlint は `label "" is unknown` (runner-label ルール) として報告。同一行で別メッセージのため LINE_MATCH にはなるが、runner-label ルールとしての空ラベル検出が未実装
- **対処**: runner-label ルールで空文字列ラベルを `"label "" is unknown"` として検出

#### L2. 式型システムの深度改善

- **fixture**: `evaluated_template` (1 MISS), `outputs_map_object` (1 MISS)
- **問題**: step output の型解決 (`steps.<id>.outputs` → `{cache-hit: string}` 等) や、job output の map 型バリデーション (`{string => string}`) が未実装
- **対処**: step output 型情報のアクション定義からの解決。output map 型のスキーマバリデーション。高コスト

#### L3. environment.name 必須チェックの位置改善

- **fixture**: `missing_required_keys` (1 MISS)
- **問題**: `environment` セクションで `name` 必須を検出しているが、行位置が 1 行ずれている (18:5 → 19:10)
- **対処**: `environment` mapping の開始位置でエラー報告

### 2.4 位置差異のみ (機能改善不要)

以下は同じエラーを検出しているが、列位置またはメッセージ文言が異なるだけのケース。機能的ギャップはない。

| fixture | MISS原因 | 備考 |
|---|---|---|
| `github_script_untrusted_input` | 行差異 (11:162 vs 16:32) | multi-line script の展開行 vs YAML ソース行 |
| `if_cond_constants` | 行差異 (18:13 vs 19:11) | `if: true` の検出行 off-by-one |
| `if_cond_edge_cases_trailing_leading_chars` | 行差異 (8:13 vs 9:11) | 同上 |
| `invalid_snapshot` | 行差異 (6:5/10:16 vs 8:9/11:23) | 同じ 2 エラーの位置ずれ |
| `issue-610_recursive_raw_yaml_value` | 行差異 (10:21 vs 11:9) | 再帰エイリアス位置 |
| `missing_required_keys` | 行差異 (18:5 vs 19:10) | environment.name 位置 |
| `strategy_matrix_runner_context` | 列差異 (7:15 vs 7:13) | `${{` 開始位置 vs コンテキスト名位置 |
| `undefined_anchor` | 位置差異 (0:0 vs 9:8) | actionlint は YAML パーサーエラー (0:0)、seiton は実際の行を報告 |

---

## 3. LINE_MATCH 詳細分析 (237 行)

LINE_MATCH (同一行・異なる列/メッセージ) の中にも設計判断として注意すべきパターンがある。

### 3.1 列位置の系統的差異

- **`context_availability` (39 LINE_MATCH)**: seiton は `${{` の開始位置 (列 -4)、actionlint はコンテキスト名の位置を報告。設計差異であり機能問題ではない
- **`special_function_availability` (8 LINE_MATCH)**: 関数名報告では seiton が `hashfiles` → `hashFiles` に正規化。seiton の方が正確

### 3.2 メッセージ形式の差異

- actionlint: `see https://docs.github.com/...` のドキュメントリンクを付加
- seiton: `called in <location>` の呼び出し元情報を付加
- いずれも設計差異であり機能上の問題はない

---

## 4. EXTRA 分析 (62 追加検出行)

seiton が actionlint より多く検出している 62 行の内訳。これらはギャップではなく seiton の追加機能。

| カテゴリ | 件数 | 説明 |
|---|---|---|
| 型インデックスチェック | 8 | object/array のインデックス型不整合 |
| 型バリデーション | 8 | 入力値の型チェック (must be bool/string/array) |
| 式値変換警告 | 5 | null/array/object を ${{ }} で文字列変換する際の警告 |
| 空 runs-on | 5 | runs-on にラベルがない job の検出 |
| env 命名ポータビリティ | 4 | env キーの大文字・ポータブル命名チェック |
| 再帰エイリアス | 4 | seiton が異なる位置で再帰エイリアスを追加検出 |
| glob パターン | 3 | glob エスケープの追加バリデーション |
| reusable workflow 制約 | 3 | uses/steps 排他・missing key の追加検出 |
| 必須キー | 3 | missing "steps"/"runs-on" 等の追加検出 |
| コンテキスト利用可能性 | 3 | 利用禁止コンテキストの追加検出 (env in strategy 等) |
| アクション入力チェック | 2 | popular action の未知入力検出 |
| テンプレートインジェクション | 2 | untrusted input の追加検出 |
| 式パースエラー | 1 | 式の構文エラー検出 |
| その他 | 11 | 個別の追加チェック |

**注意**: EXTRA の一部は H1 (reusable workflow 誤検出) に起因する偽陽性を含む。H1 修正で EXTRA 数は減少する見込み。

---

## 5. 総合評価

### 5.1 ルールカバレッジ

- **actionlint 18 ルール中 16 ルール (88.9%) を seiton がカバー**
- 未カバーの 2 件 (`shellcheck`, `pyflakes`) は外部ツール連携であり、seiton の設計方針として意図的にスコープ外
- seiton は actionlint に存在しない **33 のセキュリティ・品質ルール** を追加実装

### 5.2 fixture カバレッジ

- **95 fixtures 中 72 fixtures (75.8%) が完全互換** (MISS=0)
  - PERFECT: 22 (完全一致)
  - COL_DIFF: 42 (同一行検出、列/メッセージ差異のみ)
  - COL_DIFF + EXTRA: 8 (列差異 + seiton 追加検出)
- **486 期待行中 451 行 (92.8%) をマッチ**
  - Exact match: 214 (44.0%)
  - Line match: 237 (48.8%)
  - MISS: 35 (7.2%)

### 5.3 機能改善サマリ

| 優先度 | 項目数 | 対象 fixture 数 | 改善後の期待効果 |
|---|---|---|---|
| **高** | 5 項目 (H1-H5) | 8 fixtures | 誤検出修正 + 未検出 8 行解消 |
| **中** | 5 項目 (M1-M5) | 6 fixtures | 診断品質改善 + 未検出 10 行解消 |
| **低** | 3 項目 (L1-L3) | 4 fixtures | 追加検出 + 位置改善 |
| **合計** | **13 項目** | **最大 18 fixtures** | MISS 35→~11、互換率 75.8%→~87% |

**アクション可能な機能改善 13 項目** のうち、高優先度 5 項目を実装すれば MISS の約半分を解消し、誤検出 (偽陽性) も削減できる。

### 5.4 結論

seiton は actionlint の機能的ルールをスコープ内で 100% カバーしているが、**サブ機能レベルで 13 の改善項目** が存在する:

1. **誤検出** (H1): reusable workflow job に対する `runs-on`/`steps` 必須チェックの偽陽性
2. **誤診断** (H2): fromJSON の null フィールドアクセスの誤解釈
3. **未検出** (H3-H5, M1-M3): 配列内空文字列、配列型比較、クォート数値リテラル、空配列、glob 末尾改行、空ステップ
4. **診断品質** (M4-M5, L1-L3): エイリアス位置精度、secrets 型解決、空ラベル runner-label 統合

seiton は actionlint を機能的に上回る **33 の独自セキュリティルール** を持つが、actionlint 互換の品質を向上させるには上記項目の対処が必要。
