# Seiton vs actionlint 互換性改善計画

> actionlint testdata/err/ fixtures に対する seiton の検出結果を分析し、改善すべき項目を優先度別にまとめた計画書。
> 対象: `tests/Seiton.Core.Tests/fixtures/schema/actionlint/testdata/err/` の 99 fixtures。

---

## 0. 現状サマリ

| 指標 | 値 |
|---|---|
| 完全一致 fixtures | 10 / 99 |
| 期待行マッチ率 | 95 / 503 (18%) |
| 未マッチ期待行 (MISS) | 408 |
| 余剰 seiton 行 (EXTRA) | 423 |

### 完全一致 fixtures (10)

これらは actionlint 期待と完全に一致しており、変更不要。

1. `expr_check_in_credentials`
2. `issue558_read_write_none_are_not_always_valid_permissions`
3. `matrix_exclude_value_mismatch`
4. `workflow_call_required_default`
5. `missing_jobs`
6. `missing_on`
7. `issue207_work_dir_with_uses`
8. `issue170_empty_permissions` (部分一致、EXTRA なし)
9. `schedule_event_with_no_config_1`
10. `schedule_event_with_no_config_2`

---

## 1. ギャップ分類

### 分類 A: メッセージ形式の差異 (検出はしているがメッセージが .out と不一致)

seiton は同じ行で同じ問題を検出しているが、メッセージの文言が actionlint と異なるため `.out` regex/literal に一致しない。

### 分類 B: 行・列オフセットの差異 (検出しているが位置がずれている)

seiton はキーではなく値の位置を報告する設計方針のため、行や列が actionlint と異なる場合がある。また seiton が別の場所 (例: `exclude` セクション全体の開始行) で報告する場合もある。

### 分類 C: 検出漏れ (actionlint は検出、seiton は未検出)

actionlint が検出しているが seiton が全く検出しない (EXTRA にも対応する行がない) パターン。

### 分類 D: 重複・余剰検出 (seiton が余分な診断を出す)

seiton が actionlint よりも多くの診断を出すケース。dedup 不足や、同じ問題を別ルールが重複報告するケースを含む。

### 分類 E: スコープ外 (意図的に未対応)

actionlint 固有の機能 (shellcheck/pyflakes 連携、snapshot キー) など、seiton として対応しない項目。

---

## 2. フェーズ別改善計画

### フェーズ 1: メッセージ形式の統一 (高優先度・低リスク)

多数の fixture で「検出しているがメッセージが違う」だけで MISS になっている。`.out` の regex にマッチするようメッセージを調整する。

#### 1.1 context availability メッセージ統一

**対象 fixtures**: `context_availability`, `env_context_banned`, `issue155_env_in_job_level_if`, `shell_key_context_availability`, `strategy_matrix_runner_context`, `special_function_availability`

**現状**: seiton は2箇所から context availability エラーを出している:
- Parser 側: `context '{name}' is not available in {scope} expressions` → rule ID = parse (syntax-check にマップ)
- Linter 側 (expr-undefined-var): `context "{name}" is not allowed here. available contexts are ...` → rule ID = expression

actionlint は 1 行のみ出力: `context "xxx" is not allowed here. ...available contexts... [expression]`

**問題**: seiton は同一問題を 2 回報告 (Parser + Linter)。さらに列位置が actionlint と異なる。

**対処方針**:
- Parser 側の context availability チェックを削除し、Linter の `expr-undefined-var` ルールに一本化する
- Linter 側のメッセージ文言と列位置を actionlint に合わせる
- 列位置は `${{ }}` 内の context 参照の位置を基準にする (actionlint 準拠)

**影響 fixture 数**: ~6 fixtures, ~80+ expected lines

#### 1.2 template-injection メッセージ統一

**対象 fixtures**: `one_error`, `nested_untrusted_input`, `github_script_untrusted_input`

**現状**: seiton メッセージの末尾に `see https://docs.github.com/...` リンクがない。

**対処方針**:
- メッセージ末尾は URL なしのまま維持 (seiton のポリシー)
- `.out` を seiton 向けに書き換えるか、regex をより柔軟にする
- **推奨**: `.out` 期待値を調整 (seiton は URL を含まない設計方針)

**影響 fixture 数**: 3 fixtures, ~5 expected lines

#### 1.3 duplicate key メッセージ統一

**対象 fixtures**: `duplicate_keys`, `upper_case_duplicate_keys`

**現状**: seiton は `strategy.matrix contains duplicate key: FOO` 形式。actionlint は `key "FOO" is duplicated in "matrix" section. previously defined at line:X,col:Y. note that this key is case insensitive` 形式。

**対処方針**:
- seiton のメッセージに "previously defined at" 情報を追加する
- "note that this key is case insensitive" のサフィックスを追加する
- `.out` regex にマッチする形に統一

**影響 fixture 数**: 2 fixtures, ~13 expected lines

#### 1.4 unexpected key メッセージ統一

**対象 fixtures**: `unexpected_keys`, `case_sensitive_keys`

**現状**: seiton は `unexpected workflow key: NAME` / `on.push does not support option: BRANCHES` 形式。actionlint は `unexpected key "NAME" for "workflow" section. expected one of ...` 形式で期待キー一覧を表示。

**対処方針**:
- seiton のメッセージに "expected one of" で期待キー一覧を追加する
- メッセージ形式を actionlint 準拠にする

**影響 fixture 数**: 2 fixtures, ~33 expected lines

#### 1.5 if-cond メッセージ統一

**対象 fixtures**: `if_cond_constants`, `if_cond_edge_cases_trailing_leading_chars`

**現状**:
- `if_cond_constants`: seiton は `step if condition is always true` 形式。actionlint は `constant expression "true" in condition. remove the if: section` 形式で式内容を表示。
- `if_cond_edge_cases_trailing_leading_chars`: seiton は行番号が 1 行ずれ (値ベースで報告) + メッセージに条件式の内容が含まれない。

**対処方針**:
- `if_cond_constants`: メッセージに定数式の内容を含めるよう変更
- `if_cond_edge_cases_trailing_leading_chars`: メッセージに条件式テキストと理由を含める
- 行位置は seiton のポリシー (値の位置) を維持

**影響 fixture 数**: 2 fixtures, ~17 expected lines

#### 1.6 merge key メッセージ統一

**対象 fixture**: `merge_key_unsupported`

**現状**: seiton は `on.workflow_call.inputs does not support merge key '<<'` 形式。actionlint は `GitHub Actions does not support YAML merge key "<<"` 形式。

**対処方針**:
- メッセージを `GitHub Actions does not support YAML merge key "<<"` に統一

**影響 fixture 数**: 1 fixture, 3 expected lines

#### 1.7 needs-graph cycle メッセージ統一

**対象 fixtures**: `minimal_cycle_in_needs`, `random_order_cycle_in_needs`

**現状**: seiton は needs 値の位置で報告 (設計方針)。actionlint はジョブキーの位置で報告。メッセージ形式も異なる。

**対処方針**:
- 位置の差異は seiton のポリシーとして維持 (§4.5.1)
- メッセージ形式を可能な範囲で actionlint に近づける
- **注意**: これは意図的な設計差異のため `.out` 期待値の調整も検討

**影響 fixture 数**: 2 fixtures, 2 expected lines

#### 1.8 deprecated-commands メッセージ統一

**対象 fixture**: `deprecated_workflow_commands`

**現状**: seiton は `run script uses deprecated command '::set-output'; use $GITHUB_OUTPUT instead` 形式。actionlint は `workflow command "set-output" was deprecated. use ... instead: https://...` 形式で URL 付き。

**対処方針**:
- メッセージ形式を actionlint に近づける (URL は省略)
- `::` プレフィックスを除去して command 名だけにする

**影響 fixture 数**: 1 fixture, 4 expected lines

#### 1.9 exclusive webhook filters メッセージ統一

**対象 fixture**: `exclusive_webhook_filters`

**現状**: seiton は `on.push cannot use both branches and branches-ignore` 形式でイベントのマッピング開始位置で報告。actionlint は `both "branches" and "branches-ignore" filters cannot be used for the same event "push". note: use '!' to negate patterns` 形式で各フィルターキー位置で報告。

**対処方針**:
- メッセージ形式に `note: use '!' to negate patterns` を追加
- 報告位置をフィルターキーの位置に変更 (後方のフィルターキー行)

**影響 fixture 数**: 1 fixture, 9 expected lines

#### 1.10 invalid event filters メッセージ統一

**対象 fixture**: `invalid_event_filters`

**現状**: seiton のメッセージは actionlint とほぼ同等だが、利用可能イベント一覧のソート順が異なる (seiton: アルファベット順、actionlint: 異なる順序)。actionlint は `activity type "opened" for "merge_group" Webhook event` 形式、seiton は `on.merge_group.types contains unsupported activity type: opened` 形式。

**対処方針**:
- activity type メッセージ形式を統一
- filter availability メッセージのイベント一覧ソート順は seiton のまま維持
- `.out` 期待値を seiton のソート順に合わせるか、regex 化する

**影響 fixture 数**: 1 fixture, 13 expected lines

#### 1.11 invalid_permissions: permission scope 一覧差異

**対象 fixture**: `invalid_permissions`

**現状**: seiton は `vulnerability-alerts` スコープを含む (GitHub の公式ドキュメントに記載あり)。actionlint は含まない。4 行が不一致。

**対処方針**:
- seiton のスコープ一覧が正しい (最新仕様準拠) ため変更不要
- `.out` 期待値を seiton の一覧に合わせる

**影響 fixture 数**: 1 fixture, 4 expected lines

#### 1.12 glob-pattern メッセージ統一

**対象 fixture**: `glob_more`

**現状**: seiton は一部のパターンのみ検出 (6/18)。メッセージ形式も異なる。

**対処方針**:
- 未検出パターンを glob-pattern ルールに追加 (空パターン、リーディング/トレイリングスペース、`.`/`..` パス、`!` のみパターン、`[]` 未閉じ)
- メッセージ形式を actionlint に近づける

**影響 fixture 数**: 1 fixture, 18 expected lines

#### 1.13 schedule-event メッセージ統一

**対象 fixtures**: `cron_5minutes_limit`, `schedule_invalid_timezone`, `schedule_iana_like_invalid_timezone`

**現状**: 列位置が 1 ずれ (seiton は値、actionlint はキー)。メッセージ形式も微妙に異なる。

**対処方針**:
- 列位置は seiton のポリシーとして維持
- メッセージ形式を可能な範囲で統一
- `.out` 期待値を seiton の位置に合わせる

**影響 fixture 数**: 3 fixtures, 7 expected lines

#### 1.14 runner-label メッセージ統一

**対象 fixtures**: `invalid_runner_labels`, `runner_labels_conflict_matrix`, `macos_10.15_removed`, `macos12_runner`

**現状**: seiton のメッセージは actionlint とほぼ同等だが、フォーマットが微妙に異なる (引用符の使い方、サフィックスの違い)。

**対処方針**:
- メッセージフォーマットを actionlint の regex にマッチするよう微調整
- `.out` 期待値を regex 化して柔軟にする

**影響 fixture 数**: 4 fixtures, ~9 expected lines

#### 1.15 id-naming メッセージ統一

**対象 fixture**: `invalid_id`

**現状**: seiton は `job id '-foo' contains invalid characters; ...` 形式。actionlint は `invalid job ID "-foo". job ID must start with...` 形式。actionlint は空 ID を `string should not be empty` で報告。

**対処方針**:
- メッセージ形式を actionlint に近づける
- 空 ID 検出メッセージを統一

**影響 fixture 数**: 1 fixture, 7 expected lines

#### 1.16 workflow_call_event メッセージ統一

**対象 fixture**: `workflow_call_event`

**現状**: seiton は検出しているがメッセージ形式が異なる (例: `on.workflow_call.inputs.input0.type is required` vs `"type" is missing at "input0" input of workflow_call event`)。

**対処方針**:
- メッセージ形式を統一
- 一部は `.out` 期待値の調整

**影響 fixture 数**: 1 fixture, 14 expected lines

#### 1.17 workflow_dispatch_input_types メッセージ統一

**対象 fixture**: `workflow_dispatch_input_types`

**現状**: seiton は検出しているがメッセージ形式が異なる。

**対処方針**:
- メッセージ形式を統一
- `dispatch-inputs` ルールの出力を actionlint に合わせる

**影響 fixture 数**: 1 fixture, 12 expected lines

#### 1.18 workflow_call_job メッセージ統一

**対象 fixture**: `workflow_call_job`

**現状**: seiton は検出しているがメッセージ形式が異なる。reusable workflow の uses フォーマット検証は別問題 (フェーズ 2)。

**対処方針**:
- メッセージ形式を統一
- `with`/`secrets` requires `uses` メッセージを actionlint に合わせる

**影響 fixture 数**: 1 fixture, 8 expected lines

#### 1.19 missing_required_keys メッセージ統一

**対象 fixture**: `missing_required_keys`

**現状**: seiton は検出しているがメッセージ形式が異なる (例: `on.workflow_call.inputs.foo.type is required` vs `"type" is missing at "foo" input of workflow_call event`)。

**対処方針**:
- メッセージ形式を統一

**影響 fixture 数**: 1 fixture, 7 expected lines

#### 1.20 invalid_steps メッセージ統一

**対象 fixture**: `invalid_steps`

**現状**: seiton は検出しているが行位置がずれている (1行ずれなど)。

**対処方針**:
- 空ステップ/不正ステップの報告位置を精査

**影響 fixture 数**: 1 fixture, 7 expected lines

---

### フェーズ 2: 検出漏れの対処 (中優先度)

#### 2.1 `evaluated_template` — テンプレート型チェックの不足

**対象 fixture**: `evaluated_template`

**現状**: seiton は `object value in ${{ }}` と `null value in ${{ }}` を検出するが、`array` や `{cache-hit: string}` のような具体的な型表示がない。actionlint は具体的な推論型を表示。

**対処方針**:
- `CheckTemplateType` の型情報表示を具体化する
- array 型の検出を追加

**影響 fixture 数**: 1 fixture, 4 expected lines

#### 2.2 `expr_check_in_matrix_row_assign` — matrix 行代入の型チェック

**対象 fixture**: `expr_check_in_matrix_row_assign`

**現状**: seiton は `receiver of object dereference "foo" must be type of object but got "number"` を検出しない。

**対処方針**:
- matrix row 代入式の object dereference 型チェックを実装

**影響 fixture 数**: 1 fixture, 1 expected line

#### 2.3 `outputs_of_action_skipping_inputs_check` — action output プロパティ検証

**対象 fixture**: `outputs_of_action_skipping_inputs_check`

**現状**: seiton は `property "this_output_does_not_exist" is not defined in object type {data: string; headers: string; status: string}` を検出しない。

**対処方針**:
- Popular action の output 名検証を `expr-undefined-var` に追加
- PopularActions の `GetOutputNames()` を利用

**影響 fixture 数**: 1 fixture, 1 expected line

#### 2.4 `invalid_comparisons` — 比較演算子の型チェック不足

**対象 fixture**: `invalid_comparisons`

**現状**: seiton は一部の比較演算子型チェックを検出する (number vs null with `>`, bool vs bool with `<`) が、string vs object, number vs array, array vs array などは未検出。

**対処方針**:
- 比較演算子の型チェック範囲を拡大する
- 特に `==`/`!=` での型不一致警告と `>=`/`<=` での非比較型チェック

**影響 fixture 数**: 1 fixture, 7 expected lines

#### 2.5 `workflow_dispatch_type_check_inputs` — inputs 型チェック不足

**対象 fixture**: `workflow_dispatch_type_check_inputs`

**現状**: seiton は `property "select" is not defined in object type ...` を出すが rule ID が `expression` にマッピングされ形式が異なる。array index type / object property access type チェックが一部不足。

**対処方針**:
- `inputs` オブジェクトの型推論を改善
- array/object のインデックスアクセス型チェックを拡充

**影響 fixture 数**: 1 fixture, 10 expected lines

#### 2.6 `invalid_json_in_fromjson` — fromJSON の型推論不足

**対象 fixture**: `invalid_json_in_fromjson`

**現状**: seiton は broken JSON エラーを検出するがメッセージが異なる。正常 JSON の場合の型推論 (null/array/object のテンプレート型チェック、contains の型チェック) が不足。

**対処方針**:
- fromJSON() の戻り値型推論を実装
- テンプレート型チェックを推論型に対応

**影響 fixture 数**: 1 fixture, 9 expected lines

#### 2.7 `docker_specific_inputs_with_normal_action` — Docker 固有 input の検証

**対象 fixture**: `docker_specific_inputs_with_normal_action`

**現状**: seiton は `entrypoint`/`args` が非 Docker action に対して使われた場合のエラーを出さない。

**対処方針**:
- popular-action-inputs ルールで `entrypoint`/`args` を Docker action 以外で使った場合のチェックを追加

**影響 fixture 数**: 1 fixture, 2 expected lines

#### 2.8 `outdated_actions` / `outdated_popular_action` — outdated action runner 検出

**対象 fixtures**: `outdated_actions`, `outdated_popular_action`

**現状**: `outdated-action-runner` が `SeitonOnlyRules` に含まれているためフィルタされている。actionlint の `[action]` ルールに相当するため、マッピングを追加すべき。

**対処方針**:
- `outdated-action-runner` を `SeitonOnlyRules` から除外し、`RuleIdMap` に `["outdated-action-runner"] = "action"` を追加
- メッセージ形式を actionlint に合わせる

**影響 fixture 数**: 2 fixtures, 4 expected lines

#### 2.9 `object_at_runner_label` — runs-on の型チェック

**対象 fixture**: `object_at_runner_label`

**現状**: seiton は `object value in ${{ }} will be converted to string "[Object]"` を出すが、actionlint は `type of expression at "runs-on" must be string or array but found type "{foo: string}"` を出す。

**対処方針**:
- runs-on の式の型チェックを実装し、object/null の場合にエラーを出す

**影響 fixture 数**: 1 fixture, 1 expected line

#### 2.10 `reusable_workflow_empty_secrets` — workflow_call secrets 検証

**対象 fixture**: `reusable_workflow_empty_secrets`

**現状**: seiton は `on.workflow_call.secrets must be mapping` を出す (parser エラー) が、actionlint は secrets プロパティの未定義を検出。

**対処方針**:
- workflow_call の空 secrets セクションの処理を改善

**影響 fixture 数**: 1 fixture, 1 expected line

#### 2.11 `workflow_call_outputs_sema` — workflow_call outputs の意味解析

**対象 fixture**: `workflow_call_outputs_sema`

**現状**: seiton は `property 'some_output' is not defined in 'jobs' object` を出す。actionlint は `property "some_output" is not defined in object type {}` を出す。型の表示が異なる。

**対処方針**:
- メッセージの型表示を改善

**影響 fixture 数**: 1 fixture, 2 expected lines

#### 2.12 `workflow_call_job` — reusable workflow uses フォーマット検証

**対象 fixture**: `workflow_call_job`

**現状**: seiton は reusable workflow の uses 文字列のフォーマット検証 (`owner/repo/path/to/workflow.yml@ref` or `./path/to/workflow.yml`) を行わない。

**対処方針**:
- `reusable-workflow` ルールに uses フォーマット検証を追加

**影響 fixture 数**: 1 fixture, 4 expected lines

#### 2.13 `glob_more` — glob パターン検証の拡充

フェーズ 1.12 と重複するが、追加で以下のパターン検出が必要:

- リーディング/トレイリングスペースの検出
- `.` / `..` パスセグメントの検出
- `!` のみのパターン (少なくとも 1 文字必要)
- `[` 未閉じの検出 (paths-ignore でも)

**影響 fixture 数**: 1 fixture, 12+ expected lines

---

### フェーズ 3: 重複・余剰検出の修正 (中優先度)

#### 3.1 `dedup_errors` — アンカー展開時の診断重複

**対象 fixture**: `dedup_errors`

**現状**: seiton は YAML anchor `*step` の展開ごとに `unexpected key "with" for step to run shell command` を 11 回出す。actionlint は 1 回のみ (dedup 済み)。

**対処方針**:
- anchor 展開由来の診断を重複排除する
- 同一メッセージ・同一位置の診断を dedup する

**影響 fixture 数**: 1 fixture, 11 extra lines

#### 3.2 context availability の重複報告

**対象 fixtures**: `context_availability`, `env_context_banned`, `issue155_env_in_job_level_if`, `special_function_availability`

**現状**: フェーズ 1.1 で述べたとおり、Parser と Linter の両方から同一の context availability エラーが報告される。

**対処方針**: フェーズ 1.1 の対処で解決

#### 3.3 `invalid_int_at_max_parallel` — 正常値への誤報

**対象 fixture**: `invalid_int_at_max_parallel`

**現状**: seiton は `ok2` ジョブ (expression `${{ ... }}` を使用) に対して `must be integer` エラーを誤って出す。

**対処方針**:
- `${{ }}` 式の場合は integer チェックをスキップする

**影響 fixture 数**: 1 fixture, 1 extra line

#### 3.4 `deprecated_action_inputs` — 行位置の報告先

**対象 fixture**: `deprecated_action_inputs`

**現状**: seiton は uses 行 (line 7) で deprecated input を報告。actionlint は個別の input 行 (line 9, 10) で報告。

**対処方針**:
- deprecated input の報告位置を個別 input 行に変更

**影響 fixture 数**: 1 fixture, 2 expected lines

---

### フェーズ 4: 設計方針として維持する差異

以下は seiton の設計方針上の差異であり、修正しない。

#### 4.1 snapshot キーの非サポート

**対象 fixtures**: `invalid_snapshot`, `if_cond_constants` (snapshot 関連), `context_availability` (snapshot 関連), `glob_more` (snapshot 関連)

**理由**: GitHub Actions の `snapshot` キーは限定プレビュー機能。seiton は `unexpected job key 'snapshot'` として報告する。

**対処**: `.out` 期待値を調整するか、テストから snapshot 関連を除外

#### 4.2 shellcheck / pyflakes 連携の非サポート

**対象 fixtures**: `shellcheck_default_shell_detection`, `pyflakes_job_default_shell`, `pyflakes_step_shell`, `pyflakes_workflow_default_shell`

**理由**: seiton は外部リンターとの連携を行わない設計方針。

**対処**: これらの fixture は互換性テストのスコープ外

#### 4.3 URL サフィックスの非付与

**対象 fixtures**: `one_error`, `nested_untrusted_input`, `github_script_untrusted_input` 等

**理由**: seiton のメッセージは URL を含まない設計方針。

**対処**: `.out` 期待値を regex 化して URL 部分をオプショナルにする

#### 4.4 値位置 vs キー位置の報告

**対象**: 多数の fixture

**理由**: seiton はキーではなく値の位置を報告する設計方針。

**対処**: 期待される差異として `.out` 期待値を調整

---

## 3. fixture 別詳細一覧

### 凡例

- **状態**: `✅` 完全一致 / `🔧` メッセージ調整で対応可 / `⚠️` 検出漏れ / `🔴` 重複・余剰 / `⬜` スコープ外
- **優先度**: P1 (高) / P2 (中) / P3 (低)
- **フェーズ**: 上記フェーズ番号

| # | Fixture | 状態 | 問題区分 | フェーズ | 優先度 | 備考 |
|---|---------|------|----------|----------|--------|------|
| 1 | `assign_expression` | 🔧 | A: メッセージ | 1 | P2 | bool/int/float の型エラーメッセージ差異 |
| 2 | `case_sensitive_keys` | 🔧 | A: メッセージ | 1.4 | P1 | unexpected key メッセージに期待キー一覧を追加 |
| 3 | `context_availability` | 🔧🔴 | A+D: メッセージ+重複 | 1.1 | P1 | Parser/Linter 重複。統一で大幅改善 |
| 4 | `cron_5minutes_limit` | 🔧 | B: 列位置 | 1.13 | P2 | 列位置1ずれ + メッセージ形式 |
| 5 | `dedup_errors` | 🔴 | D: 重複 | 3.1 | P1 | anchor 展開で 11 重複。dedup 必要 |
| 6 | `deprecated_action_inputs` | 🔧 | B: 行位置 | 3.4 | P2 | uses 行 vs input 行 |
| 7 | `deprecated_workflow_commands` | 🔧 | A: メッセージ | 1.8 | P2 | メッセージ形式差異 |
| 8 | `docker_specific_inputs_with_normal_action` | ⚠️ | C: 検出漏れ | 2.7 | P2 | Docker 固有 input 検証なし |
| 9 | `duplicate_keys` | 🔧 | A: メッセージ | 1.3 | P2 | "previously defined at" 追加 |
| 10 | `empty` | 🔧 | A: メッセージ | 1 | P3 | `workflow is empty` vs `workflow root must be mapping` |
| 11 | `empty_image_names_and_versions` | 🔧 | A: メッセージ | 1 | P3 | メッセージ差異 |
| 12 | `empty_on` | 🔧 | A: メッセージ | 1 | P3 | `string should not be empty` vs `unknown event in on:` |
| 13 | `empty_sequence_or_string` | 🔧 | A+B: メッセージ+位置 | 1 | P2 | 部分一致 (11/16)。残りはメッセージ/位置差異 |
| 14 | `env_context_banned` | 🔧🔴 | A+D: メッセージ+重複 | 1.1 | P1 | context availability 重複 |
| 15 | `errors_in_anchor` | 🔧 | A: メッセージ | 1 | P2 | anchor 内エラーのメッセージ差異 |
| 16 | `evaluated_template` | ⚠️ | C: 検出漏れ | 2.1 | P2 | array/具体型の表示不足 |
| 17 | `exclusive_webhook_filters` | 🔧 | A+B: メッセージ+位置 | 1.9 | P1 | フィルターキー位置 + メッセージ |
| 18 | `expr_check_in_credentials` | ✅ | - | - | - | 完全一致 |
| 19 | `expr_check_in_env_var_name` | 🔧⚠️ | A+C: メッセージ+検出漏れ | 1.1, 2 | P2 | context availability + property 未定義 |
| 20 | `expr_check_in_matrix_row_assign` | ⚠️ | C: 検出漏れ | 2.2 | P2 | object dereference 型チェック |
| 21 | `expr_check_in_services` | 🔧 | A: メッセージ | 1 | P3 | services scalar のメッセージ |
| 22 | `expr_in_default_input` | 🔧 | B: 列位置 | 1 | P2 | 列位置差異 |
| 23 | `github_script_untrusted_input` | 🔧 | A+B: メッセージ+位置 | 1.2 | P2 | URL なし + 行位置差異 |
| 24 | `glob_more` | ⚠️ | C: 検出漏れ | 1.12, 2.13 | P1 | glob パターン検証の大幅拡充 |
| 25 | `if_cond_constants` | 🔧 | A: メッセージ | 1.5 | P1 | 定数式内容をメッセージに含める |
| 26 | `if_cond_edge_cases_trailing_leading_chars` | 🔧 | A+B: メッセージ+位置 | 1.5 | P1 | 条件式テキスト + 行位置 |
| 27 | `inputs_without_workflow_call_event` | 🔧 | A+B: メッセージ+位置 | 1 | P2 | メッセージ形式差異 |
| 28 | `invalid_comparisons` | ⚠️ | C: 検出漏れ | 2.4 | P2 | 比較演算子の型チェック不足 |
| 29 | `invalid_container_syntax` | 🔧 | B: 位置 | 1 | P3 | credentials 位置 1 行ずれ |
| 30 | `invalid_event_filters` | 🔧 | A: メッセージ | 1.10 | P1 | activity type + filter メッセージ統一 |
| 31 | `invalid_float_at_timeout_minutes` | 🔧 | A: メッセージ | 1 | P2 | 型エラーメッセージ差異 |
| 32 | `invalid_id` | 🔧 | A: メッセージ | 1.15 | P1 | ID 検証メッセージ統一 |
| 33 | `invalid_image_version_event` | 🔧 | A: メッセージ | 1 | P2 | メッセージ形式差異 |
| 34 | `invalid_int_at_max_parallel` | 🔧🔴 | A+D: メッセージ+誤報 | 1, 3.3 | P2 | 式に対する誤報 + メッセージ差異 |
| 35 | `invalid_json_in_fromjson` | ⚠️ | C: 検出漏れ | 2.6 | P2 | fromJSON 型推論不足 |
| 36 | `invalid_permissions` | 🔧 | A: メッセージ | 1.11 | P3 | scope 一覧差異 (seiton が正しい) |
| 37 | `invalid_runner_labels` | 🔧 | A: メッセージ | 1.14 | P2 | メッセージフォーマット微調整 |
| 38 | `invalid_snapshot` | ⬜ | E: スコープ外 | 4.1 | - | snapshot 非サポート |
| 39 | `invalid_steps` | 🔧 | B: 位置 | 1.20 | P2 | 行位置ずれ |
| 40 | `issue-610_recursive_raw_yaml_value` | 🔧 | A: メッセージ | 1 | P3 | recursive alias メッセージ差異 |
| 41 | `issue102` | 🔧 | B: 位置 | 1 | P3 | 列位置差異 |
| 42 | `issue151_child_of_child_job` | 🔧 | A: メッセージ | 1 | P2 | needs property メッセージ差異 |
| 43 | `issue155_env_in_job_level_if` | 🔧🔴 | A+D: メッセージ+重複 | 1.1 | P1 | context availability 重複 |
| 44 | `issue170_empty_permissions` | 🔧 | A: メッセージ | 1 | P3 | `string should not be empty` vs 独自メッセージ |
| 45 | `issue193` | 🔧 | A: メッセージ | 1 | P3 | expression parse error メッセージ差異 |
| 46 | `issue207_work_dir_with_uses` | ✅ | - | - | - | 完全一致 |
| 47 | `issue280_runs_on` | 🔧 | A+B: メッセージ+位置 | 1 | P2 | 空ラベル・位置差異 |
| 48 | `issue558_...permissions` | ✅ | - | - | - | 完全一致 |
| 49 | `macos_10.15_removed` | 🔧 | A: メッセージ | 1.14 | P3 | regex マッチ失敗 |
| 50 | `macos12_runner` | 🔧 | A: メッセージ | 1.14 | P3 | regex マッチ失敗 |
| 51 | `matrix_exclude_mismatch` | 🔧 | B: 位置 | 1 | P2 | exclude 位置差異 |
| 52 | `matrix_exclude_no_match` | 🔧 | B: 位置 | 1 | P2 | exclude 位置差異 |
| 53 | `matrix_exclude_value_mismatch` | ✅ | - | - | - | 完全一致 |
| 54 | `merge_key_unsupported` | 🔧 | A: メッセージ | 1.6 | P2 | メッセージ統一 |
| 55 | `minimal_cycle_in_needs` | 🔧 | A+B: メッセージ+位置 | 1.7 | P2 | 設計方針差異 |
| 56 | `missing_jobs` | ✅ | - | - | - | 完全一致 |
| 57 | `missing_on` | ✅ | - | - | - | 完全一致 |
| 58 | `missing_required_keys` | 🔧 | A: メッセージ | 1.19 | P2 | メッセージ形式差異 |
| 59 | `nested_untrusted_input` | 🔧 | A: メッセージ | 1.2 | P2 | URL なし |
| 60 | `no_job` | 🔧 | A: メッセージ | 1 | P3 | `jobs must be mapping` vs `should not be empty` |
| 61 | `object_at_runner_label` | ⚠️ | C: 検出漏れ | 2.9 | P2 | runs-on 型チェック |
| 62 | `one_error` | 🔧 | A: メッセージ | 1.2 | P2 | URL なし |
| 63 | `outdated_actions` | ⚠️ | C: 検出漏れ (mapping 問題) | 2.8 | P1 | RuleIdMap 追加 |
| 64 | `outdated_popular_action` | ⚠️ | C: 検出漏れ (mapping 問題) | 2.8 | P1 | RuleIdMap 追加 |
| 65 | `outputs_map_object` | 🔧 | A: メッセージ | 1 | P3 | 型表示差異 |
| 66 | `outputs_of_action_skipping_inputs_check` | ⚠️ | C: 検出漏れ | 2.3 | P2 | action output 検証 |
| 67 | `pyflakes_job_default_shell` | ⬜ | E: スコープ外 | 4.2 | - | pyflakes 非サポート |
| 68 | `pyflakes_step_shell` | ⬜ | E: スコープ外 | 4.2 | - | pyflakes 非サポート |
| 69 | `pyflakes_workflow_default_shell` | ⬜ | E: スコープ外 | 4.2 | - | pyflakes 非サポート |
| 70 | `random_order_cycle_in_needs` | 🔧 | A+B: メッセージ+位置 | 1.7 | P2 | 設計方針差異 |
| 71 | `recursive_anchors` | 🔧 | A: メッセージ | 1 | P2 | recursive alias メッセージ差異 |
| 72 | `reusable_workflow_empty_secrets` | ⚠️ | C: 検出漏れ | 2.10 | P3 | 空 secrets 処理 |
| 73 | `run_name_check_expr` | 🔧🔴 | A+D: メッセージ+重複 | 1 | P2 | undefined context 重複報告 |
| 74 | `runner_labels_conflict_matrix` | 🔧 | A: メッセージ | 1.14 | P2 | conflict メッセージ差異 |
| 75 | `schedule_event_with_no_config_1` | ✅ | - | - | - | 完全一致 |
| 76 | `schedule_event_with_no_config_2` | ✅ | - | - | - | 完全一致 |
| 77 | `schedule_iana_like_invalid_timezone` | 🔧 | B: 列位置 | 1.13 | P3 | 列位置 1 ずれ |
| 78 | `schedule_invalid_timezone` | 🔧 | A+B: メッセージ+位置 | 1.13 | P2 | メッセージ + 列位置 |
| 79 | `shell_key_context_availability` | 🔧 | B: 列位置 | 1.1 | P2 | 列位置差異 |
| 80 | `shellcheck_default_shell_detection` | ⬜ | E: スコープ外 | 4.2 | - | shellcheck 非サポート |
| 81 | `special_function_availability` | 🔧🔴 | A+D: メッセージ+重複 | 1.1 | P1 | function availability 重複 |
| 82 | `strategy_matrix_runner_context` | 🔧🔴 | A+D: メッセージ+重複 | 1.1 | P2 | context availability 重複 |
| 83 | `undefined_anchor` | 🔧 | A: メッセージ | 1 | P3 | yaml parse error メッセージ |
| 84 | `unexpected_keys` | 🔧 | A: メッセージ | 1.4 | P1 | unexpected key メッセージ統一 |
| 85 | `unused_anchors` | 🔧 | B: 列位置 | 1 | P3 | 列位置差異 |
| 86 | `upper_case_duplicate_keys` | 🔧 | A: メッセージ | 1.3 | P2 | duplicate key メッセージ統一 |
| 87 | `variables_type_check` | 🔧 | A: メッセージ | 1 | P2 | 型チェック表示差異 |
| 88 | `workflow_call_event` | 🔧 | A: メッセージ | 1.16 | P1 | メッセージ形式統一 |
| 89 | `workflow_call_inputs` | 🔧 | A+B: メッセージ+位置 | 1 | P2 | メッセージ + 位置 |
| 90 | `workflow_call_invalid_secrets` | 🔧 | A: メッセージ | 1 | P3 | メッセージ差異 |
| 91 | `workflow_call_job` | 🔧⚠️ | A+C: メッセージ+検出漏れ | 1.18, 2.12 | P1 | メッセージ統一 + uses 検証 |
| 92 | `workflow_call_outputs_sema` | 🔧 | A: メッセージ | 2.11 | P2 | 型表示改善 |
| 93 | `workflow_call_outputs_syntax` | 🔧 | A: メッセージ | 1 | P2 | メッセージ形式差異 |
| 94 | `workflow_call_required_default` | ✅ | - | - | - | 完全一致 |
| 95 | `workflow_call_secrets` | 🔧 | A: メッセージ | 1 | P2 | メッセージ差異 |
| 96 | `workflow_dispatch_input_types` | 🔧 | A: メッセージ | 1.17 | P1 | メッセージ形式統一 |
| 97 | `workflow_dispatch_more_than_25_inputs` | 🔧 | A: メッセージ | 1 | P2 | regex マッチ失敗 (URL 差) |
| 98 | `workflow_dispatch_type_check_inputs` | ⚠️ | C: 検出漏れ | 2.5 | P2 | inputs 型チェック不足 |
| 99 | `yaml_syntax_error` | 🔧 | A: メッセージ | 1 | P3 | yaml error メッセージ差異 |

---

## 4. 実装優先度サマリ

### P1 (最優先 — 多数の fixture に影響)

1. **1.1 context availability 統一** — 6+ fixtures, 80+ lines
2. **1.4 unexpected key メッセージ統一** — 2 fixtures, 33 lines
3. **3.1 anchor 展開 dedup** — 1 fixture, 11 lines
4. **1.5 if-cond メッセージ統一** — 2 fixtures, 17 lines
5. **1.9 exclusive webhook filters メッセージ統一** — 1 fixture, 9 lines
6. **1.10 invalid event filters メッセージ統一** — 1 fixture, 13 lines
7. **1.12+2.13 glob-pattern 拡充** — 1 fixture, 18 lines
8. **1.15 id-naming メッセージ統一** — 1 fixture, 7 lines
9. **2.8 outdated action runner マッピング修正** — 2 fixtures, 4 lines
10. **1.16 workflow_call_event メッセージ統一** — 1 fixture, 14 lines
11. **1.17 workflow_dispatch_input_types メッセージ統一** — 1 fixture, 12 lines
12. **1.18 workflow_call_job メッセージ統一** — 1 fixture, 8 lines

### P2 (標準優先 — 個別 fixture の改善)

1. 1.3 duplicate key メッセージ統一
2. 1.7 needs-graph cycle メッセージ
3. 1.8 deprecated-commands メッセージ
4. 1.13 schedule-event メッセージ
5. 1.14 runner-label メッセージ
6. 1.19 missing_required_keys メッセージ
7. 1.20 invalid_steps 位置
8. 2.1 evaluated_template 型チェック
9. 2.2 matrix row assign 型チェック
10. 2.3 action output プロパティ検証
11. 2.4 invalid_comparisons 型チェック
12. 2.5 workflow_dispatch inputs 型チェック
13. 2.6 fromJSON 型推論
14. 2.7 Docker 固有 input 検証
15. 2.9 runs-on 型チェック
16. 2.12 reusable workflow uses フォーマット検証
17. 3.3 max-parallel 式誤報
18. 3.4 deprecated_action_inputs 行位置

### P3 (低優先度 — 軽微な差異)

- empty / empty_on / no_job メッセージ
- yaml_syntax_error / undefined_anchor メッセージ
- invalid_permissions scope 一覧 (seiton が正しい)
- その他位置微調整

---

## 5. 検証ルール

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
6. 追加実装をした場合は、必要に応じて Seiton_Parser_spec.md や Seiton_Linter_spec.md の該当ルールの仕様を更新すること。また Seiton_Parser_csharp_spec.md や Seiton_Linter_csharp_spec.md の実装ノートも更新すること。

---

## 6. `.out` 期待値の調整方針

以下のケースでは actionlint の `.out` ファイルを seiton 向けに調整する:

1. **URL サフィックス差異**: seiton はメッセージに URL を含まない → `.out` を regex 化して URL 部分をオプショナル化
2. **値位置 vs キー位置**: seiton は値の位置を報告する設計 → `.out` の列位置を seiton に合わせる
3. **seiton が正しい差異**: permission scope 一覧 (`vulnerability-alerts` 含む) → `.out` を seiton に合わせる
4. **snapshot キー**: seiton は非サポート → snapshot 関連の期待行を除外

`.out` 調整は、seiton の実装変更では対応できない設計方針上の差異のみに限定する。

---

## 7. 実装記録

> 各フェーズの実装結果をここに記録する。

### フェーズ 1 実装記録

(未実施)

### フェーズ 2 実装記録

(未実施)

### フェーズ 3 実装記録

(未実施)
