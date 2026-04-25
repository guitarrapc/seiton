# Seiton vs actionlint 検出ギャップ分析・対処計画

> actionlint testdata (`.references/actionlint/testdata/err/`, `testdata/err/`) を基準に、seiton の検出漏れ・メッセージ品質・位置ずれを分析し、対処フェーズを定義する。

---

## 検証ルール

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
6. 追加実装をした場合は、必要に応じて Seiton_Parser_spec.md や Seiton_Lint_spec.md の該当ルールの仕様を更新すること。また Seiton_Parser_csharp.md や Seiton_Lint_csharp.md の実装ノートも更新すること。

---

## 分析概要

### 比較方法

- actionlint `testdata/err/` の各 `.yaml` + `.out` ペア（約115件）をゴールとして使用
- seiton を `--oneline --no-color` で各 `.yaml` に対して実行し、出力を収集
- 期待 `.out` の各行 `file:line:col: message [rule-id]` と seiton 出力を line:col で照合
- 照合結果を「検出漏れ」「列ずれ」「メッセージ差異」に分類

### 結果サマリ

| 分類 | 件数 | 説明 |
|---|---|---|
| 完全一致 | 0 | line:col + メッセージが完全一致するケースなし（フォーマット差異のため） |
| 検出漏れ (MISSING) | 502 | seiton が同一 line:col で何も検出していない |
| 列ずれ (WRONG_COL) | 0 | 同一行だが列が異なるケースは比較スクリプト上は 0（実際は多数あり、下記で個別分析） |
| フォーマット差異 | 全件 | seiton は `file:line:col: severity [rule-id] message`、actionlint は `file:line:col: message [rule-id]` |

### 重要な構造的差異

| 項目 | actionlint | seiton |
|---|---|---|
| 出力形式 | `file:line:col: message [rule-id]` | `file:line:col: severity [rule-id] message` |
| rule-id 命名 | `syntax-check`, `expression`, `events`, `id` 等 | `parse`, `template-injection`, `schedule-event`, `id-naming` 等 |
| extra ルール | shellcheck, pyflakes 連携 | job-permissions-required, runner-no-latest, job-timeout-minutes-required 等のセキュリティルール |
| 位置基準 | キー位置を報告する傾向 | 値位置を報告する傾向がある |

---

## カテゴリ別検出漏れ一覧

### A. 対象外 (Out of Scope) — 対処不要

外部ツール連携であり seiton のスコープ外。

| テストケース | actionlint ルール | 検出数 | 理由 |
|---|---|---|---|
| `pyflakes_job_default_shell` | `[pyflakes]` | 1 | Python linter 連携 |
| `pyflakes_step_shell` | `[pyflakes]` | 3 | Python linter 連携 |
| `pyflakes_workflow_default_shell` | `[pyflakes]` | 1 | Python linter 連携 |
| `shellcheck_default_shell_detection` | `[shellcheck]` | 12 | Shell linter 連携 |
| **小計** | | **17** | |

---

### B. 検出済みだが line:col/メッセージが不一致 — 品質改善が必要

seiton が検出しているが、位置やメッセージが actionlint と異なるため MISSING として計上されたもの。

#### B-1. permissions ルール — 列位置ずれ

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `invalid_permissions` | `4:13`, `5:3`, `6:13`, etc. (12行) | seiton は `[permissions]` で検出するが、列位置が値位置を指している。また permission scope リストに `vulnerability-alerts` が追加されておりメッセージが異なる | seiton は値ノード位置を報告、actionlint はキー/値境界を報告 |
| `issue558_read_write_none_are_not_always_valid_permissions` | `8:17`, `9:15` (2行) | seiton の `[permissions]` で検出済みだが列ずれ | 同上 |
| `issue170_empty_permissions` | `12:17` (2行) | 空文字列 permission の検出が不足 | parse レベルで空文字列 permission を検出すべき |

**対処**: seitonのpermissions ルールの位置報告は値位置が仕様なので、actionlintのキー位置基準に合わせない。空文字列パーミッション検出の追加。

#### B-2. id-naming ルール — ルール名差異

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `invalid_id` | `3:3`, `7:13`, `8:3`, `12:13`, `13:3`, `17:13` (6行) | seiton `[id-naming]` で検出済み。空文字列 step id (`22:13`) は未検出 | ルール名差異 + 空文字列ケース漏れ |

**対処**: 空文字列 step id の検出追加。

#### B-3. deprecated-commands ルール — 列位置ずれ

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `deprecated_workflow_commands` | `8:14`, `9:14`, `10:14`, `11:14` (4行) | seiton `[deprecated-commands]` で検出済みだが列位置が異なる | run スクリプト内の位置指定方式が異なる |

**対処**: 列位置をコマンド名の先頭に合わせる。

#### B-4. needs-graph ルール — 位置ずれ

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `minimal_cycle_in_needs` | `4:3` (1行) | seiton `[needs-graph]` で検出済みだが位置が異なる | 報告位置の差異 |
| `random_order_cycle_in_needs` | `4:3` (1行) | 同上 | 同上 |

**対処**: サイクル検出の報告位置を needs キーの位置に合わせる。

#### B-5. schedule-event ルール — 部分的検出

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `schedule_invalid_timezone` | `4:17`, `6:17`, `8:17`, `10:17`, `11:13` (5行) | seiton は一部を検出するが、`UTC` が無効な IANA timezone であることの検出が不足。空文字列 timezone/cron の検出も不足 | IANA timezone バリデーションの差異 |
| `cron_5minutes_limit` | `6:13` (1行) | seiton `[schedule-event]` で検出済みだが列位置が異なる | 列位置ずれ |

**対処**: UTC の扱い確認。空文字列 timezone/cron の検出追加。

#### B-6. runner-label ルール — 部分検出

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `invalid_runner_labels` | `4:14`, `8:30`, `8:46` (3行) | 未知ラベルは検出するがラベル競合検出が不足 | ラベル競合チェック未実装 |
| `runner_labels_conflict_matrix` | `6:14`, `6:30`, `6:44` (3行) | matrix 内ラベル競合が未検出。seiton は `${{matrix.os}}` を動的値として扱い検証をスキップ | matrix 展開後のラベル競合チェック未実装 |
| `macos_10.15_removed` | `5:14`, `9:14` (2行) | 廃止ラベル検出が不足 | ラベルの鮮度チェック未実装 |
| `macos12_runner` | `5:14` (1行) | 同上 | 同上 |

**対処**: ラベル競合チェック実装。廃止ラベル検出の追加。

#### B-7. if-cond ルール — 部分検出

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `if_cond_constants` | 11行期待 | seiton `[if-cond]` で 8行検出。3行不足（`true` のバリエーション、`contains(...)` 定数畳み込み） | 定数畳み込みの深さが不足 |
| `if_cond_edge_cases_trailing_leading_chars` | 6行期待 | seiton が `${{ }} ` の前後に余分な文字がある場合の always-true 検出が不足 | `${{ }}` 前後テキスト検出未実装 |

**対処**: 定数畳み込みの改善。`${{ }}` 前後テキスト検出の実装。

#### B-8. merge_key_unsupported — 位置ずれ

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `merge_key_unsupported` | `8:7`, `21:11`, `27:9` (3行) | seiton `[parse]` で検出済みだが位置が異なる場合あり | 位置報告の差異 |

**対処**: 位置の確認と調整。

---

### C. 検出漏れ (Genuinely Missing) — 実装が必要

#### C-1. 構造バリデーション `[syntax-check]` — パーサーレベル

seiton のパーサーが検出すべき構造的問題。**最大の検出漏れカテゴリ (約200件)**。

##### C-1a. 予期しないキーの検出

actionlint はすべてのセクションで未知のキーをエラーとする。seiton は一部検出するが網羅性が不足。

| テストケース | 対象セクション | 件数 | seiton の状態 |
|---|---|---|---|
| `unexpected_keys` | workflow, on.*, defaults, concurrency, job, environment, strategy, container, credentials, runs-on, step | 18 | seiton は大部分を検出するが line:col が異なる |
| `case_sensitive_keys` | 全セクション (大文字キー) | 22 | seiton は大部分を検出するが `[parse]` として報告 |
| `invalid_steps` | step (run+uses 混在, with+run 混在等) | 16 | 部分的に検出 |
| `invalid_container_syntax` | container, services, credentials | 18 | 部分的に検出 |

**対処**: 未検出の unexpected key パターンの洗い出し。特に step 内の `run` + `uses` 共存、`shell` + `uses` 共存、`with` + `run` 共存の検出確認。

##### C-1b. 空セクション検出

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `empty_sequence_or_string` | 15 | 一部検出（schedule 空、options 空等）だが多くが未検出 |
| `empty` | 1 | seiton は `workflow root must be mapping [parse]` で検出（メッセージ差異） |
| `empty_on` | 1 | 未検出 |
| `empty_image_names_and_versions` | 2 | 未検出 |

**対処**: 空シーケンス、空文字列の系統的な検出追加。

##### C-1c. 必須キー欠落

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `missing_jobs` | 1 | seiton `[parse]` で検出（位置 `1:1` vs 期待 `2:1`） |
| `missing_on` | 1 | seiton `[parse]` で検出（位置差異あり） |
| `missing_required_keys` | 7 | 部分的に検出（workflow_call input type 必須、output value 必須等） |

**対処**: 位置の精度改善。

##### C-1d. 重複キー検出

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `duplicate_keys` | 2 | seiton `[parse]` で検出済み（位置差異あり） |
| `upper_case_duplicate_keys` | 11 | seiton `[parse]` で大部分検出（大文字小文字無視の重複） |

**対処**: 位置の精度確認。

##### C-1e. 型不一致検出

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `invalid_float_at_timeout_minutes` | 4 | seiton `[parse]` で一部検出（bool→float 拒否等） |
| `invalid_int_at_max_parallel` | 5 | seiton `[parse]` で一部検出 |
| `assign_expression` | 3 | 式で bool/int/float を期待する箇所にプレーンテキストが来た場合の検出 |

**対処**: 型チェックの網羅性確認。

##### C-1f. アンカー関連

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `unused_anchors` | 6 | **完全に未検出** — 未使用アンカーの検出なし |
| `recursive_anchors` | 7 | seiton はパース時に検出するが一部パターンが漏れ |
| `errors_in_anchor` | 4 | アンカー内のエラーの一部が未検出 |
| `undefined_anchor` | 1 | seiton `[parse]` で検出済み |
| `issue-610_recursive_raw_yaml_value` | 2 | 再帰エイリアスの検出が不足 |

**対処**: 未使用アンカー検出の実装。再帰アンカーパターンの網羅。

##### C-1g. runs-on 構造バリデーション

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `issue280_runs_on` | 17 | seiton は一部検出するが、空文字列、空ラベル配列、`groups` vs `group` の typo、labels 型不一致等の検出が不足 |

**対処**: runs-on セクションの構造バリデーション強化。

#### C-2. イベント設定バリデーション `[events]`

##### C-2a. イベントフィルター可用性

actionlint は各フィルター (`paths`, `branches`, `tags` 等) がどのイベントで利用可能かを厳密にチェック。

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `invalid_event_filters` | 12 | seiton は一部を `[parse]` で検出（不正な activity type）するが、フィルターの利用可能性チェック（例: `paths` は merge_group で使えない）が不足 |
| `exclusive_webhook_filters` | 9 | seiton `[parse]` で検出済み（メッセージ形式差異） |

**対処**: イベント別フィルター可用性テーブルの実装。

##### C-2b. workflow_dispatch 入力バリデーション

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `workflow_dispatch_input_types` | 13 | seiton `[dispatch-inputs]` と `[parse]` で大部分検出（位置/メッセージ差異） |
| `workflow_dispatch_more_than_25_inputs` | 1 | **未検出** — 25 入力制限チェックなし |

**対処**: 25 入力制限の実装。

##### C-2c. workflow_call イベントバリデーション

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `workflow_call_event` | 15 | seiton `[parse]` で大部分検出だが一部漏れ |
| `workflow_call_inputs` | 1 | required + default の警告が不足 |
| `workflow_call_outputs_syntax` | 5 | seiton `[parse]` で大部分検出 |
| `workflow_call_secrets` | 1 | secrets のプロパティ未定義チェックが不足 |
| `workflow_call_invalid_secrets` | 1 | 不正なシークレット形式検出が不足 |

**対処**: workflow_call の入力/出力/シークレットバリデーション強化。

##### C-2d. schedule イベント

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `schedule_event_with_no_config_1` | 1 | **未検出** — mapping なしの schedule イベント |
| `schedule_event_with_no_config_2` | 1 | **未検出** — 同上 |

**対処**: schedule イベントの mapping 必須チェック追加。

#### C-3. 式セマンティック分析 `[expression]`

##### C-3a. コンテキスト可用性チェック

actionlint は式中のコンテキスト参照がその位置で利用可能かを厳密にチェック。

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `context_availability` | 38 | seiton は `[parse]` で一部検出（`env` が workflow 式で不可等）するが、多くのスコープ×コンテキスト組み合わせが未検出 |
| `env_context_banned` | 2 | 部分的に検出 |
| `issue155_env_in_job_level_if` | 4 | **未検出** — job レベル if での env コンテキスト不可 |
| `shell_key_context_availability` | 2 | **未検出** — shell キーではコンテキストが利用不可 |
| `special_function_availability` | 8 | **未検出** — `always()`, `cancelled()`, `success()`, `failure()`, `hashFiles()` の利用可能スコープチェック |
| `expr_check_in_env_var_name` | 4 | コンテキスト可用性 + プロパティ未定義の複合チェック |

**対処**: コンテキスト可用性マトリクスの完全実装。各式位置（workflow env, job if, step if, runs-on, strategy, container, services, env var name, shell 等）で利用可能なコンテキストを定義し検証。

##### C-3b. 式の型チェック

actionlint は式の型推論を行い、型不一致を検出。

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `invalid_comparisons` | 7 | **未検出** — 比較演算子の型チェック |
| `variables_type_check` | 2 | **未検出** — vars コンテキストのプロパティチェック |
| `evaluated_template` | 3 | **未検出** — object/array/null の `${{ }}` 内評価警告 |
| `expr_check_in_matrix_row_assign` | 1 | **未検出** — matrix 行代入の型チェック |
| `object_at_runner_label` | 1 | **未検出** — runs-on にオブジェクト型の式 |
| `workflow_dispatch_type_check_inputs` | 9 | seiton は一部検出するが型推論ベースの深いチェックが不足 |

**対処**: 段階的に型推論エンジンを強化。まず runs-on/strategy での型チェックから開始。

##### C-3c. 未定義プロパティ・変数チェック

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `inputs_without_workflow_call_event` | 1 | **未検出** — workflow_call イベントなしでの inputs 参照 |
| `issue151_child_of_child_job` | 1 | **未検出** — needs の子→孫のプロパティ参照 |
| `workflow_call_outputs_sema` | 2 | **未検出** — workflow_call outputs の未定義プロパティ |
| `outputs_of_action_skipping_inputs_check` | 1 | **未検出** — アクション outputs の未定義プロパティ |
| `run_name_check_expr` | 1 | **未検出** — run-name 内の未定義変数 |
| `expr_in_default_input` | 3 | **未検出** — デフォルト入力値内の未定義プロパティ |
| `reusable_workflow_empty_secrets` | 1 | **未検出** — reusable workflow 内の未定義シークレット |

**対処**: expr-undefined-var ルールの検出スコープ拡大。

##### C-3d. アンチパターン検出

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `one_error` | 1 | seiton `[template-injection]` で検出済みだが位置が異なる |
| `nested_untrusted_input` | 3 | seiton `[template-injection]` で部分検出だがネストパターン（`pages.*.page_name`）が不足 |
| `github_script_untrusted_input` | 1 | seiton `[template-injection]` で検出だが長い文字列の中の位置が異なる |

**対処**: template-injection の位置精度改善。ネストパターンの追加。

##### C-3e. fromJSON 型推論

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `invalid_json_in_fromjson` | 8 | **未検出** — 不正な JSON 文字列の検出 + fromJSON 結果の型推論 |

**対処**: fromJSON の引数が文字列リテラルの場合の JSON バリデーション実装。

##### C-3f. `issue193` — 式内の不正文字

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `issue193` | 1 | **未検出** — 式内のダブルクォートリテラル検出 |

**対処**: 式パーサーでダブルクォート使用時のエラーメッセージ改善。

#### C-4. グロブパターンバリデーション `[glob]`

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `glob_more` | 18 | seiton `[glob-pattern]` で 6件検出、12件未検出。特に先頭/末尾スペース、`.`/`..`、空パターン、ref name ルール違反が不足 |

**対処**: glob-pattern ルールの検出パターン拡充。

#### C-5. Matrix バリデーション `[matrix]`

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `matrix_exclude_mismatch` | 12 | seiton `[matrix]` で部分検出（warning として）だが一部パターンが漏れ |
| `matrix_exclude_no_match` | 4 | seiton `[matrix]` で部分検出 |
| `matrix_exclude_value_mismatch` | 1 | seiton `[matrix]` で検出済み |

**対処**: matrix exclude バリデーションの網羅性改善。

#### C-6. Workflow Call ジョブバリデーション

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `workflow_call_job` | 7 | seiton `[job-structure]`, `[reusable-workflow]` で部分検出。不正な uses 形式の検出は `[unpinned-uses]` として出力される場合あり |

**対処**: reusable workflow の uses 形式バリデーション強化。

#### C-7. アクション入力バリデーション `[action]`

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `deprecated_action_inputs` | 2 | **未検出** — 非推奨入力の検出なし |
| `docker_specific_inputs_with_normal_action` | 2 | **未検出** — Docker 固有入力を通常アクションに使用 |
| `outdated_actions` | 2 | seiton `[outdated-action-runner]` で検出済みだが位置が異なる |
| `outdated_popular_action` | 2 | 同上 |

**対処**: 非推奨入力と Docker 固有入力の検出実装。

#### C-8. credentials バリデーション

| テストケース | 件数 | seiton の状態 |
|---|---|---|
| `expr_check_in_credentials` | 6 | seiton は credentials の式チェック（username + password 必須）が不足 |
| `expr_check_in_services` | 2 | services セクションの式型チェックが不足 |

**対処**: credentials/services の構造 + 式型チェック実装。

---

### D. seiton の余剰検出 (OK テストデータでの誤検出)

seiton は actionlint にないルールを持っており、actionlint の OK テストデータに対してもエラー/警告を出力する。

| ルール | OK テストデータでの影響 | 対処 |
|---|---|---|
| `job-timeout-minutes-required` | ほぼ全 OK ファイルで error | seiton 固有ルール。OK テスト比較時は除外すべき |
| `job-permissions-required` | ほぼ全 OK ファイルで warning | 同上 |
| `runner-no-latest` | 多数の OK ファイルで warning | 同上 |
| `unpinned-uses` | 一部の OK ファイルで error | 同上 |
| `unpinned-image` | 一部の OK ファイルで error | 同上 |
| `run-env-context-direct-use` | anchors.yaml 等で error | 同上 |
| `parse` | anchors.yaml, container_syntax.yaml で error | **要調査** — OK ファイルでパースエラーが出るのは問題 |

**対処**:
- OK テストデータでの `[parse]` エラーは修正が必要（パーサーのバグ）
- seiton 固有ルールは actionlint 比較テストでは除外して比較すべき
- 統合テスト作成時に、actionlint 互換テストは特定ルールのみ検証する方式を採用

---

### E. メッセージ品質・バグ

| テストケース | 問題 | 重要度 |
|---|---|---|
| `invalid_steps` | `Utf8Slice { Offset = 358, Length = 5, IsEmpty = False }` がエラーメッセージに表示される | **Critical** — 内部表現のリーク |
| `invalid_steps` | 位置 `0:0` のエラーが存在 | **High** — 位置情報の欠落 |
| 全般 | 行番号が 0-based の箇所がある（ 期待は 1-based） | **High** — 一貫性 |
| `context_availability` | seiton のメッセージが actionlint より簡素 | **Low** — 情報量の差 |

---

## 対処フェーズ計画

### Phase 1: バグ修正・品質改善 (Critical)

**目標**: 既存検出の品質を上げる。新規検出は追加しない。

| # | 対処 | 対象テスト | 分類 |
|---|---|---|---|
| 1-1 | `Utf8Slice` 内部表現のエラーメッセージリーク修正 | `invalid_steps` | E (バグ) |
| 1-2 | 位置 `0:0` のエラーを正しい位置に修正 | `invalid_steps`, その他 | E (バグ) |
| 1-3 | OK テストデータでの `[parse]` エラー修正 | `anchors.yaml`, `container_syntax.yaml` | D (誤検出) |
| 1-4 | 行番号の 0-based → 1-based 統一 | 全般 | E (品質) |

### Phase 2: 構造バリデーション強化 (High)

**目標**: パーサーレベルの構造チェックを actionlint 水準に引き上げる。

| # | 対処 | 対象テスト | 分類 |
|---|---|---|---|
| 2-1 | 空セクション検出の網羅化 | `empty_sequence_or_string`, `empty_on`, `empty`, `empty_image_names_and_versions` | C-1b |
| 2-2 | 必須キー欠落の位置精度改善 | `missing_jobs`, `missing_on`, `missing_required_keys` | C-1c |
| 2-3 | 未使用アンカー検出の実装 | `unused_anchors` | C-1f |
| 2-4 | runs-on 構造バリデーション強化 | `issue280_runs_on` | C-1g |
| 2-5 | step の run/uses 共存チェック強化 | `invalid_steps` | C-1a |
| 2-6 | container/services 構造バリデーション強化 | `invalid_container_syntax` | C-1a |
| 2-7 | schedule イベントの mapping 必須チェック | `schedule_event_with_no_config_1`, `schedule_event_with_no_config_2` | C-2d |

### Phase 3: イベント・フィルターバリデーション (High)

**目標**: イベント設定の検出を actionlint 水準に引き上げる。

| # | 対処 | 対象テスト | 分類 |
|---|---|---|---|
| 3-1 | イベント別フィルター可用性テーブル実装 | `invalid_event_filters` | C-2a |
| 3-2 | workflow_dispatch 25 入力制限 | `workflow_dispatch_more_than_25_inputs` | C-2b |
| 3-3 | workflow_call 入力/出力/シークレットバリデーション強化 | `workflow_call_event`, `workflow_call_outputs_syntax`, `workflow_call_secrets` | C-2c |

### Phase 4: コンテキスト可用性チェック (High)

**目標**: 式内のコンテキスト参照が利用可能なスコープに限定されていることを検証。

| # | 対処 | 対象テスト | 分類 |
|---|---|---|---|
| 4-1 | コンテキスト可用性マトリクス完全実装 | `context_availability`, `env_context_banned`, `issue155_env_in_job_level_if` | C-3a |
| 4-2 | shell キーでのコンテキスト不可チェック | `shell_key_context_availability` | C-3a |
| 4-3 | 特殊関数の利用可能スコープチェック | `special_function_availability` | C-3a |

### Phase 5: 位置・メッセージ品質改善 (Medium)

**目標**: 検出済みだが位置やメッセージが不適切なケースを修正。

| # | 対処 | 対象テスト | 分類 |
|---|---|---|---|
| 5-1 | permissions ルールの列位置修正 | `invalid_permissions`, `issue558_...` | B-1 |
| 5-2 | deprecated-commands の列位置修正 | `deprecated_workflow_commands` | B-3 |
| 5-3 | needs-graph の報告位置修正 | `minimal_cycle_in_needs`, `random_order_cycle_in_needs` | B-4 |
| 5-4 | template-injection の位置精度改善 | `one_error`, `nested_untrusted_input` | C-3d |
| 5-5 | if-cond の定数畳み込み改善 | `if_cond_constants` | B-7 |
| 5-6 | if-cond の `${{ }}` 前後テキスト検出 | `if_cond_edge_cases_trailing_leading_chars` | B-7 |

### Phase 6: 式セマンティック分析拡張 (Medium)

**目標**: 式の型推論と意味解析の強化。

| # | 対処 | 対象テスト | 分類 |
|---|---|---|---|
| 6-1 | expr-undefined-var の検出スコープ拡大 | `inputs_without_workflow_call_event`, `issue151_child_of_child_job`, `workflow_call_outputs_sema`, `run_name_check_expr`, `expr_in_default_input` | C-3c |
| 6-2 | runs-on/strategy 式の型チェック | `object_at_runner_label` | C-3b |
| 6-3 | fromJSON 引数の JSON バリデーション | `invalid_json_in_fromjson` | C-3e |
| 6-4 | ダブルクォートリテラル検出改善 | `issue193` | C-3f |

### Phase 7: 追加検出 (Low)

**目標**: actionlint が検出するその他のパターンを追加。

| # | 対処 | 対象テスト | 分類 |
|---|---|---|---|
| 7-1 | glob パターン検出拡充 | `glob_more` | C-4 |
| 7-2 | ラベル競合チェック | `invalid_runner_labels`, `runner_labels_conflict_matrix` | B-6 |
| 7-3 | 廃止ラベル検出 | `macos_10.15_removed`, `macos12_runner` | B-6 |
| 7-4 | 非推奨アクション入力検出 | `deprecated_action_inputs` | C-7 |
| 7-5 | Docker 固有入力チェック | `docker_specific_inputs_with_normal_action` | C-7 |
| 7-6 | matrix exclude バリデーション強化 | `matrix_exclude_mismatch`, `matrix_exclude_no_match` | C-5 |
| 7-7 | credentials/services 式型チェック | `expr_check_in_credentials`, `expr_check_in_services` | C-8 |
| 7-8 | 比較演算子の型チェック | `invalid_comparisons` | C-3b |
| 7-9 | `${{ }}` 内 object/array/null 評価警告 | `evaluated_template`, `variables_type_check` | C-3b |

### Phase 8: 統合テスト基盤 (Infrastructure)

**目標**: actionlint testdata に対する自動回帰テストを構築。

| # | 対処 |
|---|---|
| 8-1 | actionlint `testdata/err/*.yaml` を seiton で実行し `.out` と比較する統合テストランナーの実装 |
| 8-2 | 出力形式の変換レイヤー実装（seiton 形式 → actionlint 互換形式） |
| 8-3 | seiton 固有ルールを除外して比較する機能 |
| 8-4 | 正規表現 `.out` 行への対応（`/pattern/` 形式） |

---

## 実装記録

> 各フェーズの実装後、テスト結果・ベンチマーク結果をここに追記すること。

### Phase 1 実装記録

(未着手)

### Phase 2 実装記録

(未着手)

### Phase 3 実装記録

(未着手)

### Phase 4 実装記録

(未着手)

### Phase 5 実装記録

(未着手)

### Phase 6 実装記録

(未着手)

### Phase 7 実装記録

(未着手)

### Phase 8 実装記録

(未着手)
