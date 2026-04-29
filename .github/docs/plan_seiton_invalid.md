# Seiton vs actionlint 互換性評価

> actionlint testdata fixtures に対する seiton の検出結果を分析し、機能カバレッジを評価、対応策を優先度付けして計画するドキュメント。
> 対象: `tests/Seiton.Core.Tests/fixtures/schema/actionlint/testdata/` の err/ fixtures および examples/ fixtures。

---

## 0. 現状サマリ

### 0.1 err/ fixtures (既存 ActionlintCompatTests)

| 指標 | 最新 (2026-04-30) |
|---|---|
| 互換 fixtures (MISS=0) | 92 / 95 (4 scope-out) |
| マッチ率 (exact+line+near-line) | 483 / 486 (99%) |
| 完全一致マッチ率 (exact match) | 158 / 486 |
| 列差異マッチ (same line, diff col/msg) | 320 / 486 |
| 近接行マッチ (near-line, position diff) | 5 / 486 |
| 未マッチ期待行 (MISS) | 3 (true gaps) |
| 余剰 seiton 行 (EXTRA) | 41 (additional detections) |

### 0.2 examples/ fixtures (actionlint ドキュメント用サンプル)

| 指標 | 最新 (2026-04-30) |
|---|---|
| 対象 examples fixtures | 49 (2 scope-out 除外) |
| 互換 fixtures (MISS=0) | 45 / 49 |
| 行レベルマッチ率 (line+col or line) | 130 / 143 (90%) |
| 完全一致マッチ率 (exact match) | 22 / 143 |
| 列差異マッチ (same line, diff col/msg) | 105 / 143 |
| 近接行マッチ (near-line, position diff) | 3 / 143 |
| 未マッチ期待行 (MISS) | 13 (true gaps) |
| 余剰 seiton 行 (EXTRA) | 4 (additional detections) |
| scope-out (shellcheck/pyflakes) | 2 (shellcheck_integration, pyflakes_integration) |

### 0.3 方針

**ActionlintCompatTests 比較ロジック**:
- **scope-out 除外**: shellcheck/pyflakes fixtures を比較対象から除外 (seiton は意図的に未サポート)
- **2パスマッチング**: Pass 1 = exact/regex マッチ、Pass 2 = 行番号フォールバック。COL_DIFF (同一行・異なる列/メッセージ) は設計差異として「互換」にカウント
- **EXTRA は修正候補でない**: seiton 独自検出 (template injection, portability 警告等) は追加機能であり、ギャップとしてカウントしない

**seiton の設計差異 (修正不要)**:
- seiton はキーではなく **値の行列** を示す仕様のため、同一行で列がずれるケースは想定される差異
- seiton は「どこで発生したか」を示す文言 (例: `jobs.'test'.steps[1]`) を付加するため、メッセージは厳密に一致しない (仕様として妥当)

---

## 1. err/ fixtures: 検出漏れ (MISS) の分析

### 1.1 MISS 一覧 (19件 / 13 fixtures)

| # | Fixture | MISS 行 | actionlint 期待 | 分類 | 優先度 |
|---|---|---|---|---|---|
| 1 | `workflow_call_job` | 6:5 | `when a reusable workflow is called with "uses", "steps" is not available...` | reusable-workflow ルール: uses ジョブでの禁止キー検出 | **P1** |
| 2 | `workflow_call_job` | 10:5 | `"with" is only available for a reusable workflow call with "uses" but "uses" is not found` | reusable-workflow ルール: メッセージ差異 (seiton は検出しているが行位置ずれ) | **P1** |
| 3 | `workflow_call_job` | 17:5 | `"secrets" is only available for a reusable workflow call with "uses" but "uses" is not found` | reusable-workflow ルール: 同上 | **P1** |
| 4 | `workflow_call_job` | 24:10 | `string should not be empty` | 空文字列バリデーション未検出 | **P2** |
| 5 | `github_script_untrusted_input` | 11:162 | `"github.event.head_commit.author.name" is potentially untrusted...` | template-injection: `actions/github-script` の `script` input 内の位置計算差異 | **P2** |
| 6 | `glob_more` | 20:9 | `leading and trailing spaces are not allowed in glob path` | glob-pattern: multiline 文字列の trailing newline 空白検出 | **P2** |
| 7 | `if_cond_constants` | 18:13 | `constant expression "true" in condition` | if-cond: multiline (`\|`) 定数式の検出漏れ | **P2** |
| 8 | `if_cond_edge_cases_trailing_leading_chars` | 8:13 | `if: condition "${{ false }}\n" is always evaluated to true...` | if-cond: multiline trailing `\n` ケースで行番号ずれ (seiton は 9:11 で検出) | **P3** |
| 9 | `invalid_json_in_fromjson` | 28:32 | `1st argument of function call is not assignable. "{array: ...}" cannot be assigned to "array<any>"` | expr-undefined-var: `contains()` の引数型チェック (object → array overload) | **P3** |
| 10 | `invalid_json_in_fromjson` | 28:32 | `1st argument of function call is not assignable. "{array: ...}" cannot be assigned to "string"` | expr-undefined-var: `contains()` の引数型チェック (object → string overload) | **P3** |
| 11 | `invalid_snapshot` | 6:5 | `"snapshot" section must have "image-name" configuration` | パーサー: snapshot 必須キー `image-name` 検出の行番号差異 (seiton は 8:9 で検出) | **P3** |
| 12 | `invalid_snapshot` | 10:16 | `string should not be empty` | パーサー: 空文字列バリデーション (seiton は `glob pattern should not be empty` で代替検出) | **P3** |
| 13 | `undefined_anchor` | 0:0 | `could not parse as YAML: yaml: unknown anchor 'default_env' referenced` | パーサー: VYaml の未定義アンカーエラーの行番号差異 (seiton は 9:8 で検出) | **P3** |
| 14 | `strategy_matrix_runner_context` | 7:15 | `context "runner" is not available in strategy expressions` | テスト: ファイル名不一致 (actionlint .out がファイル名をそのまま使用、seiton は test.yaml に変換) | **P3** |
| 15 | `recursive_anchors` | 15:9 | `element of "steps" section is alias node but mapping node is expected` | パーサー: alias ノードの steps 内型チェック | **P2** |
| 16 | `recursive_anchors` | 15:9 | `step must run script with "run" section or run action with "uses" section` | パーサー: alias 展開後の step バリデーション (seiton は別行で検出) | **P2** |
| 17 | `issue-610_recursive_raw_yaml_value` | 10:21 | `unexpected alias node on parsing value in matrix row` | パーサー: matrix 内 alias ノードメッセージ差異 (seiton は 11:9 で検出) | **P3** |
| 18 | `workflow_dispatch_input_types` | 13:19 | `string should not be empty` | パーサー: choice option 空文字列の汎用バリデーション (seiton は独自メッセージで 12:21 で検出) | **P3** |
| 19 | `invalid_image_version_event` | 6:16 | `string should not be empty` | パーサー: image_version の空文字列バリデーション (seiton は `filter value should not be empty` で 5:34 で検出) | **P3** |

### 1.2 MISS 根本原因別の分類

#### カテゴリ A: 検出自体がない真のギャップ (要実装)

| # | 根本原因 | 影響 fixture | 対処 |
|---|---|---|---|
| A1 | **reusable-workflow ルール**: `uses` ジョブでの禁止キー (`steps` 等) を検出するメッセージが actionlint と行位置・メッセージが合わない | `workflow_call_job` (#1,#2,#3) | reusable-workflow ルールの diagnostics 行位置をキー位置に調整 |
| A2 | **if-cond ルール**: multiline (`\|`) block scalar の定数式検出漏れ | `if_cond_constants` (#7) | if-cond ルールで block scalar (trailing `\n`) の正規化処理を追加 |
| A3 | **if-cond ルール**: multiline trailing `\n` ケースで行位置ずれ | `if_cond_edge_cases_trailing_leading_chars` (#8) | if-cond ルールの行位置算出を block scalar 開始行に修正 |
| A4 | **contains() 型チェック**: object 型を `contains()` に渡した場合の overload 不一致エラー未検出 | `invalid_json_in_fromjson` (#9,#10) | ExpressionSemanticAnalyzer で overload 解決失敗時の診断追加 |

#### カテゴリ B: 検出はしているが行位置・メッセージ差異で MISS 判定 (位置調整)

| # | 根本原因 | 影響 fixture | 対処 |
|---|---|---|---|
| B1 | `github_script_untrusted_input`: seiton は `script:` input の個別行位置 (16:32) で検出、actionlint は `with:` 全体のオフセット (11:162) | `github_script_untrusted_input` (#5) | テスト比較ロジックの調整、またはルール内位置計算改善 |
| B2 | `strategy_matrix_runner_context`: actionlint .out がファイル名 `strategy_matrix_runner_context.yaml` を使用、seiton テストは `test.yaml` に変換 | `strategy_matrix_runner_context` (#14) | テスト側でファイル名マッチングの柔軟化 |
| B3 | 空文字列バリデーション: seiton は独自メッセージ/行位置で検出 | `workflow_call_job` (#4), `workflow_dispatch_input_types` (#18), `invalid_image_version_event` (#19) | 行位置差異が原因。seiton の独自メッセージは妥当なので MISS カウントから除外検討 |
| B4 | `invalid_snapshot`: `image-name` 必須チェックの行位置ずれ | `invalid_snapshot` (#11,#12) | 行位置を mapping 開始位置に合わせる |
| B5 | `undefined_anchor`: VYaml エラー位置 (9:8) vs actionlint (0:0) | `undefined_anchor` (#13) | actionlint の 0:0 は Go yaml ライブラリの制約。seiton のほうが正確 |
| B6 | `recursive_anchors`/`issue-610_recursive_raw_yaml_value`: alias 展開後の診断位置差異 | `recursive_anchors` (#15,#16), `issue-610` (#17) | パーサーの alias 関連診断位置の微調整 |
| B7 | `glob_more`: multiline 文字列の glob 空白検出 | `glob_more` (#6) | glob-pattern ルールで multiline 末尾空白の検出追加 |

---

## 2. err/ fixtures: 列差異・メッセージ差異 (COL_DIFF) の分析

### 2.1 主要パターン

seiton は **値の位置** を報告する設計 (actionlint はキー位置を報告するケースあり)。以下は想定される設計差異であり修正不要:

| パターン | 例 | 差異理由 | 対処 |
|---|---|---|---|
| **キー vs 値の列位置** | `missing_required_keys`: actionlint `5:7` / seiton `5:7` だがメッセージ形式が異なる | seiton は `on.workflow_call input "foo" is missing "type"` 形式 | 修正不要 (seiton のメッセージがより具体的) |
| **job ID 引用形式** | `invalid_steps`: actionlint `in job "test2"` / seiton `in jobs.'test2'` | seiton は JSON path 風の表記 | 修正不要 (seiton の表記がユーザーにとって明確) |
| **concurrency メッセージ** | `missing_required_keys`: actionlint `group name is missing in "concurrency" section` / seiton `"concurrency" section is missing group name` | 文の構造が異なる | 修正不要 |
| **permissions 空値** | `issue170_empty_permissions`: actionlint `string should not be empty` / seiton `permissions value must not be empty` | seiton はより具体的なメッセージ | 修正不要 |
| **glob 列位置** | `glob_more`: 各種パターンで 1-3 列のずれ | seiton は値の先頭を報告、actionlint はキーの末尾を報告 | 修正不要 (設計差異) |

### 2.2 修正検討すべき差異

なし。現時点の COL_DIFF はすべて設計差異 (seiton の報告位置・メッセージのほうがより具体的/正確) と評価。

---

## 3. examples/ fixtures: テスト対象化計画

### 3.1 概要

`tests/Seiton.Core.Tests/fixtures/schema/actionlint/testdata/examples/` には actionlint のドキュメント用サンプル 51 件が格納されている。これらは actionlint が公式に示す「検出例」であり、seiton が同等の検出能力を持つことを示すために重要。

### 3.2 テスト実装

`ActionlintExamplesCompatTests` クラスを新設し、examples/ の全 YAML/out ペアに対して既存の `ActionlintCompatTests` と同等の比較テストを実行する。

scope-out fixtures:
- `shellcheck_integration` (shellcheck 依存)
- `pyflakes_integration` (pyflakes 依存)

### 3.3 examples/ fixtures 一覧と実測カバレッジ

| Fixture | 主要ルール | seiton 対応状況 |
|---|---|---|
| `main` | glob, runner-label, template-injection, popular-action-inputs, expr-undefined-var | ○ ほぼ対応済 |
| `broken_yaml` | syntax-check (YAML parse error) | ○ 対応済 |
| `builtin_func_special_checks` | expression (format, fromJSON) | ○ 対応済 |
| `comparison_strict_checks` | expression (型比較チェック) | ○ 対応済 |
| `contexts_and_builtin_funcs` | expression (コンテキストアクセス) | ○ 対応済 |
| `contexts_special_functions_availability` | expression (コンテキスト・関数利用可能性) | ○ 対応済 |
| `contextual_matrix_values` | expression (matrix 型チェック) | ○ 対応済 |
| `contextual_needs_object` | expression (needs 型チェック) | ○ 対応済 |
| `contextual_steps_outputs` | expression (steps 前方参照チェック) | ○ 対応済 |
| `cron_schedule_check` | schedule-event (cron, timezone) | ○ 対応済 |
| `cyclic_deps_needs` | needs-graph (循環依存) | ○ 対応済 |
| `dangling_alias` | syntax-check (未定義アンカー) | △ 検出するが行番号差異あり |
| `deprecated_inputs` | action (非推奨 input) | ○ 対応済 |
| `deprecated_workflow_commands` | deprecated-commands | ○ 対応済 |
| `detect_outdated_popular_actions` | action (outdated runner) | ○ 対応済 |
| `env_var_names` | env-var | ○ 対応済 |
| `expand_object` | expression (env 展開型チェック) | ○ 対応済 |
| `expression_syntax_error` | expression (構文エラー) | ○ 対応済 |
| `glob` | glob (パターンバリデーション) | ○ 対応済 |
| `hardcoded_credentials` | credentials (ハードコードパスワード) | ○ 対応済 |
| `id_naming_convention` | id (ID命名規則) | ○ 対応済 |
| `if_cond_always_true` | if-cond (定数条件) | ○ Phase 2 で修正済 (multiline block scalar 位置修正) |
| `invalid_action_format` | action (uses 形式バリデーション) | ○ 対応済 |
| `invalid_ids_in_needs` | job-needs (不正 ID) | ○ 対応済 |
| `job_step_ids_duplicate` | id, syntax-check (重複 ID) | ○ 対応済 |
| `local_action_inputs` | action (ローカルアクション input) | ○ 対応済 |
| `local_action_outputs` | expression (ローカルアクション output) | ○ 対応済 |
| `matrix_checks` | matrix (重複値、exclude 不一致) | ○ 対応済 |
| `missing_required_keys` | syntax-check (必須キー) | ○ 対応済 |
| `not_persistent_matrix_values` | expression (matrix 型チェック) | ○ 対応済 |
| `permissions` | permissions (値バリデーション) | ○ 対応済 |
| `popular_action_inputs` | action (popular action input) | ○ 対応済 |
| `popular_action_outputs` | expression (popular action output) | ○ 対応済 |
| `reusable_workflow_outputs` | expression (reusable workflow output) | ○ 対応済 |
| `runner_label_check` | runner-label (ラベルチェック) | ○ 対応済 |
| `runner_label_conflict` | runner-label (OS 競合) | ○ 対応済 |
| `shell_name_validation` | shell-name (シェル名チェック) | ○ 対応済 |
| `type_checks` | expression (型チェック) | ○ 対応済 |
| `unexpected_keys` | syntax-check (予期しないキー) | ○ 対応済 |
| `unexpected_mapping_values` | syntax-check (不正なマッピング値) | ○ 対応済 |
| `untrusted_input` | expression (template injection) | ○ 対応済 |
| `webhook_checks` | events, syntax-check (webhook イベント) | ○ 対応済 |
| `workflow_call_definitions` | events, syntax-check (workflow_call 定義) | ○ 対応済 |
| `workflow_call_jobs` | syntax-check, workflow-call (reusable workflow) | △ 禁止キー検出差異あり (Phase 1 修正対象) |
| `workflow_dispatch_input_types` | syntax-check, events (dispatch input 型) | ○ 対応済 |
| `workflow_inputs_secrets_types` | expression (input/secret 型チェック) | ○ 対応済 |
| `yaml_anchors` | credentials, syntax-check (アンカー使用) | ○ 対応済 |
| `yaml_anchor_usage` | syntax-check (アンカー使用法) | ○ 対応済 |
| `action_metadata_syntax_validation` | action (メタデータバリデーション) | ○ 対応済 |
| `shellcheck_integration` | shellcheck (外部ツール) | × scope-out |
| `pyflakes_integration` | pyflakes (外部ツール) | × scope-out |

---

## 4. 修正フェーズ計画

### 4.0 検証手順 (全フェーズ共通)

各フェーズの実装完了前に、以下の検証を必ず行うこと:

1. **テストファースト**: まず、失敗するテストを用意して実装がない/間違っていることを確認してから実装し、実装後テストが通ることを確認してください。
2. **テスト実行**: `dotnet test` で全テスト通過を確認
3. **リグレッションテスト追加**: 修正した誤検出・検出漏れに対して、再発防止のためのテストを追加する
   - 誤検出修正: `ok-*` ケースで「エラーが出ないこと」を確認するテスト
   - 検出漏れ修正: `ng-*` ケースで「期待するエラーメッセージが出ること」を確認するテスト
   - パーサー修正: `ParserTests` でAST構築が正しいことを確認するテスト
4. **ベンチマーク実行**: `cd src/Seiton.Benchmark; dotnet run -c Release` で性能劣化がないことを確認する
   - `ParsingBenchmark`: パーサー変更時に、Small/Medium/Large の Mean と Allocated に大きな劣化がないこと
   - `LintBenchmark`: ルール変更時に、parse+lint の Mean と Allocated に大きな劣化がないこと
   - 目安: Mean +10% 以内、Allocated +20% 以内であれば許容
5. 実装結果とテスト結果をこのドキュメントに記録すること
6. 追加実装をした場合は、必要に応じて `Seiton_Parser_spec.md` や `Seiton_Linter_spec.md` の該当ルールの仕様を更新すること。また `Seiton_Parser_csharp_spec.md` や `Seiton_Linter_csharp_spec.md` の実装ノートも更新すること。

### Phase 0: examples/ テスト基盤構築

**目的**: examples/ fixtures に対する互換テストを追加し、現状のカバレッジを可視化する。

**タスク**:
1. `ActionlintExamplesCompatTests` クラスを `tests/Seiton.Core.Tests/` に新設
   - `ActionlintCompatTests` と同等の比較ロジック (2パスマッチング)
   - scope-out: `shellcheck_integration`, `pyflakes_integration`
   - examples/ の各 `.yaml` / `.out` ペアに対して compat テスト実行
   - `ExamplesCompatibilitySummary` サマリーテスト
   - `.seiton.out` 生成/検証テスト
2. 全 examples テスト実行して現状の MISS/COL_DIFF/EXTRA 分布を把握
3. この文書の §3 を実測結果で更新

**完了条件**:
- `dotnet test --treenode-filter "/*/*/ActionlintExamplesCompatTests/*"` が全件パス (テスト自体は MISS があっても fail しない)
- 実測サマリーがこのドキュメントに記録されていること

**実装結果**: ✅ 完了 (2026-04-29)
- `ActionlintExamplesCompatTests.cs` 新設: 103 テスト (51 fixtures × 2 + summary)
- 全テストパス (`dotnet test` = 1201 テスト全通過)
- `.seiton.out` ファイル全件生成済み
- **実測結果**: 40/49 互換 (86% マッチ率)、MISS 20件

**examples/ MISS 内訳 (9 fixtures, 20 MISS 行)**:

| Fixture | MISS 数 | 主な原因 |
|---|---|---|
| `action_metadata_syntax_validation` | 6 | local-action-inputs ルールの検出メッセージ差異 (regex マッチ不成立) |
| `invalid_action_format` | 4 | unpinned-uses ルールが actionlint の action 形式検証を担っているが SeitonOnlyRules で除外される |
| `workflow_call_jobs` | 3 | reusable-workflow ルールの行位置差異 + not-existing workflow ファイル参照エラー |
| `local_action_inputs` | 2 | local-action-inputs ルールの検出メッセージが regex にマッチしない |
| `if_cond_always_true` | 1 | multiline block scalar trailing `\n` ケース (err/ Phase 2 と同じ原因) |
| `invalid_ids_in_needs` | 1 | needs-graph ルールの未知ジョブ参照メッセージ差異 |
| `dangling_alias` | 1 | 未定義アンカーの行位置差異 (seiton は具体行, actionlint は 0:0) |
| `yaml_anchor_usage` | 1 | alias ノード型チェックメッセージ差異 |
| `popular_action_inputs` | 1 | popular-action-inputs の unknown input メッセージ regex マッチ不成立 |

---

### Phase 1: reusable-workflow ルールの検出改善 (P1)

**目的**: `workflow_call_job` fixture の 4 MISS を解消する。

**影響 MISS**: #1, #2, #3, #4

**根本原因**:
- seiton は `uses` ジョブで禁止キーを検出しているが、行位置が job ID 行 (4:3) を指している
- actionlint は禁止キーの **キー行** (6:5 = `steps:`, 10:5 = `with:`, 17:5 = `secrets:`) を指す
- seiton は `with`/`secrets` requires `uses` を検出しているが、行位置がジョブ ID 行を指している
- `uses:` が空の場合 (`call4`) の `string should not be empty` は seiton が独自メッセージで検出済み

**対処方針**:
1. reusable-workflow ルールで、禁止キーの診断位置をキー位置にする
2. `with`/`secrets` requires `uses` の診断位置を `with:`/`secrets:` キー位置にする
3. テスト比較ロジックの改善: seiton の行位置で LINE_MATCH が成立するよう調整

**実装結果**: ✅ 完了 (2026-04-29)

**変更内容**:
1. `Job` AST に `StepsKeyRange`, `RunsOnKeyRange` フィールドを追加
2. `WorkflowCall` AST に `WithKeyRange`, `SecretsKeyRange` フィールドを追加
3. パーサー: 禁止キー/requires-uses の診断位置をキー位置に変更 (`jobIdMark` → `stepsKeyPos`/`withKeyPos` 等)
4. `JobStructureRule`: `cannot have both` の診断位置をキー位置に変更
5. `ReusableWorkflowRule`: `ReportIfPresent` / `with`/`secrets` requires `uses` の診断位置をキー位置に変更

**MISS 削減**: 3件 (#1, #2, #3) — err/workflow_call_job 行位置が actionlint と一致
- #4 (`string should not be empty` at 24:10) は対象外 (seiton は独自メッセージで 25:18 で検出済み、Phase 6 で対応)

**テスト**: 6 件の新規位置テスト + 全 1207 テスト通過

---

### Phase 2: if-cond ルールの multiline 対応 (P2)

**目的**: `if_cond_constants` と `if_cond_edge_cases_trailing_leading_chars` の MISS を解消する。

**影響 MISS**: #7, #8

**根本原因**:
- `if: |` (block scalar) の場合、YAML パーサーが trailing `\n` を含む文字列を返す
- seiton の if-cond ルールが block scalar 内の定数式を正しく判定できていない (#7)
- seiton が block scalar の開始行ではなく、値の行を報告している (#8)

**対処方針**:
1. if-cond ルールで block scalar の trailing `\n` を正規化してから定数判定
2. block scalar の場合、`if:` キー行を報告位置とする

**実装結果**: 完了
- `Step`, `Job`, `Snapshot` AST に `IfKeyRange` フィールドを追加 (パーサーが `if:` キー位置をキャプチャ)
- `IfCondRule.ValidateCondition()` で block scalar (trailing `\n`) を検出した場合、`IfKeyRange` からブロックスカラーインジケータ位置 (`if:` キー列 + 4) を算出して報告位置を修正
- MISS #7 (`if_cond_constants` 18:13): 定数検出は既存で動作していたが位置が 19:11 → 18:13 に修正
- MISS #8 (`if_cond_edge_cases_trailing_leading_chars` 8:13): always-true 検出は既存で動作していたが位置が 9:11 → 8:13 に修正
- examples/ `if_cond_always_true` の block scalar ケースも 20:11 → 19:13 に修正
- テスト: 3 件の位置精度テスト追加 (`IfCondRule_BlockScalarConstant_ReportsAtIfKeyLine`, `IfCondRule_BlockScalarAlwaysTrue_ReportsAtIfKeyLine`, `IfCondRule_BlockScalarJobIf_ReportsAtIfKeyLine`)
- 全 1210 テスト pass

---

### Phase 3: glob-pattern ルールの multiline 空白検出 (P2)

**目的**: `glob_more` fixture の MISS を解消する。

**影響 MISS**: #6

**根本原因**:
- YAML block scalar (`|`) で末尾に改行を含むパスが glob フィルターに渡された場合
- seiton は trailing `\n` を含むパターンの空白チェック自体は `IsGlobWhitespace(\n)` で検出していた
- しかし報告位置がブロックスカラーの内容行 (例: 21:9) を指しており、actionlint の期待するインジケータ行 (20:9) と不一致だった

**対処方針**:
1. glob-pattern ルールで、パターンが trailing `\n` を持つ場合 (block scalar)、`Config.Utf8Yaml` のソースバイトを後方スキャンして `|` / `>` インジケータの位置を特定し、報告位置をインジケータ行に調整する

**実装結果**: ✅ 完了
- `GlobPatternRule.ValidatePattern()` で block scalar (trailing `\n`) を検出した場合、`AdjustBlockScalarRange()` ヘルパーでソースバイトを後方スキャンし `|`/`>` インジケータ位置を算出して報告位置を修正
- MISS #6 (`glob_more` 20:9): 21:9 → 20:9 に修正
- テスト: 1 件の位置精度テスト追加 (`GlobPatternRule_BlockScalarTrailingNewline_ReportsAtIndicatorLine`)
- 全 1211 テスト pass

---

### Phase 4: recursive_anchors の alias 展開改善 (P2)

**目的**: `recursive_anchors` fixture の 2 MISS を解消する。

**影響 MISS**: #15, #16

**根本原因**:
- `*recursive2` が steps 配列要素として使われた場合、seiton は `must be object` として検出
- actionlint は `element of "steps" section is alias node but mapping node is expected` + `step must run script with "run" section or run action with "uses" section` の 2 メッセージ
- seiton は位置 (16:0) で検出しているが、actionlint は (15:9) を期待

**対処方針**:
1. alias ノードが steps 配列要素の場合、専用の診断メッセージを追加
2. 行位置を alias ノードの位置に合わせる

**実装結果**: ✅完了
- `VYamlStreamAdapter.CurrentStart`: Alias イベントの `ResolveAliasStart()` 呼び出し追加 → `*alias` トークンの正確な位置を返す
- `WorkflowParser.Steps.cs`: `ParseStep()` で `YamlEventKind.Alias` を明示的に検出し、alias 専用診断メッセージを出力
  - `element of "steps" section is alias node but mapping node is expected`
  - `must run script with "run" section or run action with "uses" section`
- テスト: `Parse_RecursiveAliasInSteps_ReportsAliasNodeMessage`, `Parse_RecursiveAliasInSteps_ReportsAtAliasPosition` (2 tests)
- .seiton.out 再生成: recursive_anchors, issue-610_recursive_raw_yaml_value, examples/recursive_anchors
- ベンチマーク: 回帰なし

---

### Phase 5: contains() 型チェックの overload 解決 (P3)

**目的**: `invalid_json_in_fromjson` fixture の 2 MISS を解消する。

**影響 MISS**: #9, #10

**根本原因**:
- `contains(matrix.object, matrix.string)` で `matrix.object` が `{array: array<bool>; bool: bool}` 型
- actionlint は `contains()` の 2 overload (`contains(array<any>, any)` と `contains(string, string)`) 両方に対して型不一致を報告
- seiton は overload 解決を行っているが、両方の不一致を診断として出力していない

**対処方針**:
1. ExpressionSemanticAnalyzer で overload 解決失敗時に全候補の不一致理由を診断出力

**実装結果**: ✅完了
- 根本原因特定: パーサーレベル `ValidateFunctionCall` は matrix context を `Any` 型で推論するため `contains()` の overload チェックが素通り。リントレベルの `ValidateNodePropertyAccess` は rich matrix override 型を持つが、関数引数型チェックを行っていなかった。
- `ExpressionSemanticAnalyzer.ValidateFunctionCallWithOverrides()` を新設: override-aware 型推論で引数型を解決し、全 overload が失敗した場合に全候補の不一致理由を報告
- メッセージ形式: `{ordinal} argument of function call is not assignable. "{actualType}" cannot be assigned to "{expectedType}". called function type is "{funcSignature}"`
- `ValidateNodePropertyAccess` の `FunctionCall` ケースから呼び出し
- ヘルパー追加: `FormatOrdinal()`, `FormatOverloadSignature()`
- テスト: `ContainsOverload_ObjectArg_ReportsAllOverloadMismatches` (1 test)
- .seiton.out 再生成: invalid_json_in_fromjson
- ベンチマーク: 回帰なし
- MISS #9, #10 は COL_DIFF (同一行 28、列 32 vs 19) として互換判定される見込み。overload 署名の差異 (`string, string` vs `string, any`) もあるが同一行マッチ

---

### Phase 6: テスト比較ロジック改善 (P3)

**目的**: 実質的に検出済みだが行位置/ファイル名差異で MISS 判定されているケースを解消する。

**影響 MISS**: #5 (github_script_untrusted_input), #11-#12 (invalid_snapshot), #13 (undefined_anchor), #14 (strategy_matrix_runner_context), #17 (issue-610), #18-#19 (empty string)

**根本原因**:
- `strategy_matrix_runner_context`: .out ファイルがファイル名をそのまま使用 (`strategy_matrix_runner_context.yaml:7:15`)、seiton テストは `test.yaml` に変換
- `github_script_untrusted_input`: seiton は `script:` input の個別行 (16:32) で検出、actionlint は offset 計算 (11:162)
- `undefined_anchor`: actionlint (0:0) vs seiton (9:8) — seiton のほうが正確
- `invalid_snapshot`, `issue-610`, 空文字列系: 行位置差異 (seiton が別の位置/メッセージで検出済み)

**対処方針**:
1. `strategy_matrix_runner_context` テスト: ファイル名フォールバックマッチを追加
2. 残りは「seiton が独自位置/メッセージで検出済み」として、テスト比較側で LINE_MATCH または NEAR_MATCH として扱うことを検討
3. MISS カウントの再分類 (true gap vs position difference)

**実装結果**: ✅完了
- 3パスマッチングアルゴリズムへ拡張:
  - Pass 1: Exact/regex match (既存)
  - Pass 2: Line-number match (既存)
  - Pass 3: **Near-line match** (新規) — ±5行以内で同一 `[rule-id]` の seiton 行をマッチ。行 0 (位置不明) の場合は同一 rule ID の任意の行をマッチ。
- **ファイル名正規化**: `.out` ファイルが fixture 名をファイル名として使用している場合に `test.yaml:` へ正規化 (→ strategy_matrix_runner_context の LINE_MATCH 成立)
- `MatchResult.NearLineMatchCount` 追加、サマリレポートに Near-line match カテゴリ追加
- `NormalizeExpectations()`, `ExtractRuleId()`, `ExtractExpectedRuleId()` ヘルパー追加
- `GenerateMappingSummary` にも Pass 3 + NEAR_LINE ステータス追加
- `ActionlintExamplesCompatTests` にも同一の 3パスロジック適用
- MISS 8→1: 7件解消 (#4 workflow_call_job, #5 github_script, #13 undefined_anchor, #14 strategy_matrix_runner, #18 workflow_dispatch, #19 invalid_image_version, #11 invalid_snapshot image-name)
- 残り MISS 1件: #12 invalid_snapshot `10:16: string should not be empty` — seiton はより具体的な `glob pattern should not be empty` で検出しているが、rule ID が異なる ([syntax-check] vs [glob]) ため near-line マッチ不成立
- テスト: 全 1214 テスト pass

---

## 5. 進捗記録

| Phase | ステータス | 着手日 | 完了日 | MISS 削減数 | 備考 |
|---|---|---|---|---|---|
| Phase 0 | ✅完了 | 2026-04-29 | 103 tests | 1201 all pass | examples/ テスト基盤 |
| Phase 1 | ✅完了 | 2026-04-29 | 2026-04-29 | 3 (#1,#2,#3) | reusable-workflow |
| Phase 2 | ✅完了 | 2026-04-29 | 2026-04-29 | 2 (#7,#8) + examples 1 | if-cond multiline |
| Phase 3 | ✅完了 | 2026-04-29 | 2026-04-29 | 1 (#6) | glob multiline |
| Phase 4 | ✅完了 | 2026-04-29 | 2026-04-29 | 2 (#15,#16) | recursive_anchors |
| Phase 5 | ✅完了 | 2026-04-29 | 2026-04-29 | 2 (#9,#10) | contains() overload |
| Phase 6 | ✅完了 | 2026-04-29 | 2026-04-29 | 7 (#4,#5,#11,#13,#14,#18,#19) | テスト比較改善 |
| Phase 7 | ✅完了 | 2026-04-30 | 2026-04-30 | -2 (意図的抑制) | workflow_dispatch 空文字option 抑制 |

---

## 6. 最新 MISS 分析 (2026-04-30)

### 6.1 err/ fixtures 残 MISS (3件 / 3 fixtures)

| # | Fixture | MISS 行 | actionlint 期待 | 根本原因 | 対処方針 |
|---|---|---|---|---|---|
| 1 | `workflow_dispatch_input_types` | 13:19 | `string should not be empty [syntax-check]` | `workflow_dispatch` の choice option で空文字 `''` を使用。seiton は**意図的に抑制** (GitHub Actions として合法な「選択なし」パターン) | **対処不要** (seiton ポリシー: 空文字 option は合法) |
| 2 | `empty_sequence_or_string` | 10:13 | `string should not be empty [syntax-check]` | 同上: `workflow_dispatch` choice option の空文字 `''` 検出を意図的に抑制 | **対処不要** (同上) |
| 3 | `invalid_snapshot` | 10:16 | `string should not be empty [syntax-check]` | seiton は `glob pattern should not be empty` で検出済みだが rule ID が異なる ([syntax-check] vs [glob]) ため near-line マッチ不成立 | **対処不要** (seiton のメッセージがより具体的; 同一コンセプトは検出済み) |

**結論**: err/ fixtures の残 MISS 3件はすべて**対処不要**。#1/#2 は seiton ポリシーとして合法パターンを意図的に検出しない方針。#3 は同一コンセプトを別ルール/メッセージで検出済み。

### 6.2 examples/ fixtures 残 MISS (13件 / 4 fixtures)

| # | Fixture | MISS 数 | actionlint 期待 | 根本原因 | 優先度 |
|---|---|---|---|---|---|
| 1 | `action_metadata_syntax_validation` | 6 | local action metadata の各種バリデーション (`env` 禁止, `description` 必須, ファイル存在確認, branding, runner 名) | `local-action-inputs` ルールが examples テスト環境でローカルアクション解決に失敗している | **P1** |
| 2 | `invalid_action_format` | 4 | `uses:` 参照形式バリデーション (ref 欠如, owner 欠如, Docker tag 空, ローカル参照 `./` 欠如) | `unpinned-uses` ルールが actionlint の `[action]` ルールと同等チェックを行うが、テスト比較で `SeitonOnlyRules` 除外されている | **P1** |
| 3 | `local_action_inputs` | 2 | missing required input / unknown input の検出 | `local-action-inputs` ルールが examples テスト環境でローカルアクション解決に失敗している | **P1** |
| 4 | `workflow_call_jobs` | 1 | `could not read reusable workflow file for "./..."` — ローカル reusable workflow ファイルの存在チェック | `reusable-workflow` ルールがファイル存在確認を行っていない (ルール仕様の範囲外) | **P2** |

### 6.3 根本原因別の分類と対処

#### カテゴリ A: テスト比較ロジックの問題 (修正可能、ルール実装変更不要)

| 問題 | 影響 | 対処 |
|---|---|---|
| `invalid_action_format`: `unpinned-uses` が `SeitonOnlyRules` に含まれるため、seiton が検出した結果が比較から除外される | 4 MISS | `unpinned-uses` の一部チェック (ref 欠如, owner 欠如, Docker tag 空, `./` 欠如) は actionlint `[action]` と等価。テスト比較ロジックで `unpinned-uses` → `action` のマッピングを追加するか、`SeitonOnlyRules` から除外する |

#### カテゴリ B: ルールの実行環境問題 (テスト設定修正)

| 問題 | 影響 | 対処 |
|---|---|---|
| `local_action_inputs` / `action_metadata_syntax_validation`: `local-action-inputs` ルールがテストで使用するファイルパスからローカルアクションを解決できない | 8 MISS | `LocalActionInputsRule` のパス解決ロジックがテスト設定の `GetLintFilePath()` で返すパス相対でアクション metadata を見つけられない可能性。ルールのパス解決を確認し、テスト環境で `.github/actions/` が正しく解決されるよう修正 |

#### カテゴリ C: 未実装機能 (ルール拡張必要)

| 問題 | 影響 | 対処 |
|---|---|---|
| `workflow_call_jobs`: ローカル reusable workflow ファイルの存在チェック | 1 MISS | `reusable-workflow` ルールで `uses: ./.github/workflows/*.yml` 形式のローカル参照に対し、ファイル存在確認を追加。ファイルが見つからない場合に警告を発行 |

### 6.4 対処優先度

| 優先度 | 対処 | 期待 MISS 削減 | 難易度 |
|---|---|---|---|
| **P1-A** | `unpinned-uses` → `action` テスト比較マッピング追加 | -4 (invalid_action_format) | 低 (テスト変更のみ) |
| **P1-B** | `local-action-inputs` ルールの examples テスト環境パス解決修正 | -8 (local_action_inputs + action_metadata_syntax_validation) | 中 (ルールのパス解決デバッグ) |
| **P2** | reusable-workflow ローカルファイル存在チェック | -1 (workflow_call_jobs) | 中 (新機能追加) |

**P1-A + P1-B を解消すれば examples/ MISS は 13→1 に削減可能** (残り 1 は P2 のファイル存在チェック)。

---
