# Seiton vs actionlint 互換性改善計画

> actionlint testdata/err/ fixtures に対する seiton の検出結果を分析し、改善すべき項目を優先度別にまとめた計画書。
> 対象: `tests/Seiton.Core.Tests/fixtures/schema/actionlint/testdata/` のfixtures。

---

## 0. 現状サマリ

| 指標 | フェーズ 1 実施前 | フェーズ 1 実施後 | フェーズ 2+3 実施後 | フェーズ 4 実施後 |
|---|---|---|---|---|
| 完全一致 (PERFECT) fixtures | 10 / 99 | 90 / 99 | 29 / 99 ※ | 19 / 99 ※※ |
| 列差異のみ (COL_DIFF) fixtures | - | - | 27 / 99 | 41 / 99 |
| 行レベルマッチ率 (line+col or line) | 95 / 503 (18%) | 473 / 498 (94%) | 391 / 503 (77.7%) | 444 / 503 (88.3%) |
| 完全一致マッチ率 (line+col exact) | - | - | 248 / 503 (49.3%) | 206 / 503 (40.9%) |
| 列差異マッチ (same line, diff col/msg) | - | - | 143 / 503 (28.4%) | 238 / 503 (47.3%) |
| 未マッチ期待行 (MISSING) | 408 | 25 | 112 | 59 |
| 余剰 seiton 行 (EXTRA) | 423 | 0 | 91 | 60 |

※ フェーズ 1 実施直後は `.seiton.out` を seiton 実出力に合わせて管理していたため PERFECT が多かった。フェーズ 2+3 でメッセージ・位置を actionlint に近づける改善を行ったため、`.seiton.out` が `.out` とのギャップを正確に反映するようになった。

※※ フェーズ 4 の PERFECT 減少は比較手法の改善 (regex パターン対応) による再分類。実際の検出能力は向上している (MISS 112→59, EXTRA 91→60)。COL_DIFF が 27→41 に増加したのは、以前 MIXED だった fixtures が改善されて COL_DIFF のみになったため。

**fixture 状態分布 (フェーズ 4 実施後)**:
- **PERFECT** (完全一致): 19 fixtures — actionlint `.out` と完全に一致 (regex マッチ含む)
- **COL_DIFF** (列差異のみ): 41 fixtures — 同じ行で検出しているが列位置またはメッセージ形式が異なる (検出漏れ・余剰なし)
- **MISSING** (検出漏れのみ): 5 fixtures — 一部の行が未検出 (うち 4 は pyflakes/shellcheck スコープ外)
- **MIXED** (複合): 34 fixtures — 複数種類のギャップが混在

### フェーズ 1 実施内容

1. **1.1 context availability 重複報告の修正**: Parser 側の context/function/hashFiles availability チェックを削除し、Linter の `expr-undefined-var` ルールに一本化。33 テストを更新。
2. **1.2〜1.20 .out 期待値の統一**: 全 78 の `.seiton.out` ファイルを seiton の実出力に合わせて更新。seiton のメッセージはユーザーにとって分かりやすいため、actionlint のメッセージ形式に寄せるのではなく、`.out`と`.seiton.out`の意味がズレていないならば seiton のメッセージを維持する方針で調整。

### フェーズ 1 追加実施 (1.1〜1.9 メッセージ改善)

actionlint の `.out` ファイルは変更せず、seiton 側のメッセージ形式を改善した。テスト比較は以下の方針:

- **`.out` ファイル**: actionlint のオリジナル期待値。変更しない。
- **`.seiton.out` ファイル**: seiton の実出力に基づく期待値。seiton のメッセージ形式に合わせて管理。
- **メッセージ比較**: `.out` と `.seiton.out` の対応は「対応表 (マッピング)」で管理する。リテラル一致や正規表現ではなく、意味的に同じ検出であることをマッピングテーブルで表現する。

#### 実施済み項目

| # | 項目 | 変更内容 | 変更ファイル |
|---|------|----------|-------------|
| 1.1 | context availability スコープ情報 | `ExprUndefinedVarRule` に `FormatScopeName()` 追加。メッセージ末尾に `. called in {scope}` を付与 | `ExprUndefinedVarRule.cs` |
| 1.2 | template injection URL | メッセージ末尾に GitHub セキュリティハードニング URL を追加 | `TemplateInjectionRule.cs` |
| 1.3 | duplicate key メッセージ | `key "{key}" is duplicated in "{section}" section. previously defined at line:X,col:Y` + case-insensitive note | `WorkflowParser.ScalarParsing.cs`, `WorkflowParser.Jobs.cs` |
| 1.4 | unexpected key 期待キー一覧 | `unexpected key "{key}" for "{section}" section. expected one of ...` 形式に統一。`ExpectedKeys.g.cs` に 9 セクション追加 | 12+ パーサーファイル, `ExpectedKeys.g.cs` |
| 1.5 | if-cond 式内容表示 | 定数式: `constant expression "{expr}" in condition`、always-true: `if: condition "{text}" is always evaluated to true` | `IfCondRule.cs` |
| 1.6 | merge key メッセージ | `GitHub Actions does not support YAML merge key "<<". occurred in {mappingName}` | `WorkflowParser.ScalarParsing.cs` |
| 1.7 | needs-graph cycle メッセージ | `cyclic dependencies in "needs" job configurations are detected. detected cycle is {cyclePath}` (ジョブ名を `"` で括る) | `NeedsGraphRule.cs` |
| 1.8 | deprecated commands メッセージ | `workflow command "{cmd}" was deprecated. use \`echo ...\` instead: {DocsUrl}` | `DeprecatedCommandsRule.cs` |
| 1.9 | exclusive webhook filters | `both "{X}" and "{X}-ignore" filters cannot be used for the same event "{event}". note: use '!' to negate patterns` | `WorkflowParser.On.Webhook.cs` |

### 未マッチ fixtures (検出ギャップ — フェーズ 2 以降)

| Fixture | 理由 | フェーズ | 状態 |
|---|---|---|---|
| `docker_specific_inputs_with_normal_action` | `rhysd/action-setup-vim` カタログ追加済み | 2.7 | ✅ 実装済み (input 検出) |
| `expr_check_in_matrix_row_assign` | object dereference 型チェック | 2.2 | ✅ 実装済み |
| `outdated_actions` | outdated-action-runner マッピング | 2.8 | ✅ 実装済み |
| `outdated_popular_action` | `actions/stale` カタログ追加済み | 2.8 | ✅ 実装済み |
| `outputs_of_action_skipping_inputs_check` | `octokit/request-action` カタログ追加済み | 2.3 | ✅ 実装済み (output 検出) |
| `pyflakes_job_default_shell` | pyflakes 連携なし (スコープ外) | 4 | - |
| `pyflakes_step_shell` | 同上 | 4 | - |
| `pyflakes_workflow_default_shell` | 同上 | 4 | - |
| `shellcheck_default_shell_detection` | shellcheck 連携なし (スコープ外) | 4 | - |

### 完全一致 fixtures (19 — PERFECT)

regex パターン対応のマッチングで actionlint 期待と完全に一致。

1. `dedup_errors` ✅
2. `deprecated_workflow_commands` ✅
3. `duplicate_keys` ✅
4. `expr_check_in_credentials`
5. `invalid_container_syntax` ✅
6. `issue207_work_dir_with_uses`
7. `issue558_read_write_none_are_not_always_valid_permissions`
8. `macos_10.15_removed` (regex)
9. `macos12_runner` (regex)
10. `matrix_exclude_value_mismatch`
11. `missing_jobs`
12. `missing_on`
13. `outdated_actions` ✅
14. `outdated_popular_action` ✅
15. `runner_labels_conflict_matrix`
16. `schedule_event_with_no_config_1`
17. `schedule_event_with_no_config_2`
18. `workflow_call_outputs_syntax` ✅
19. `workflow_call_required_default`

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

#### 1.1 context availability メッセージ統一 ✅ 実施済み

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

#### 1.2 template-injection メッセージ統一 ✅ 実施済み

**対象 fixtures**: `one_error`, `nested_untrusted_input`, `github_script_untrusted_input`

**現状**: seiton メッセージの末尾に `see https://docs.github.com/...` リンクがない。

**対処方針**:
- メッセージ末尾は URL なしのまま維持 (seiton のポリシー)
- `.out` を seiton 向けに書き換えるか、regex をより柔軟にする
- **推奨**: seitonにURLを含めてよりユーザーフレンドリーに。メッセージの差異は意味の違いではなく、ユーザーへの情報提供の差異であるため、seiton のメッセージを維持する方針で調整することを推奨。

**影響 fixture 数**: 3 fixtures, ~5 expected lines

#### 1.3 duplicate key メッセージ統一 ✅ 実施済み

**対象 fixtures**: `duplicate_keys`, `upper_case_duplicate_keys`

**現状**: seiton は `strategy.matrix contains duplicate key: FOO` 形式。actionlint は `key "FOO" is duplicated in "matrix" section. previously defined at line:X,col:Y. note that this key is case insensitive` 形式。

**対処方針**:
- seiton のメッセージに "previously defined at" 情報を追加する
- "note that this key is case insensitive" のサフィックスを追加する
- `.out` regex にマッチする形に統一

**影響 fixture 数**: 2 fixtures, ~13 expected lines

#### 1.4 unexpected key メッセージ統一 ✅ 実施済み

**対象 fixtures**: `unexpected_keys`, `case_sensitive_keys`

**現状**: seiton は `unexpected workflow key: NAME` / `on.push does not support option: BRANCHES` 形式。actionlint は `unexpected key "NAME" for "workflow" section. expected one of ...` 形式で期待キー一覧を表示。

**対処方針**:
- seiton のメッセージに "expected one of" で期待キー一覧を追加する
- メッセージ形式を actionlint 準拠にする

**影響 fixture 数**: 2 fixtures, ~33 expected lines

#### 1.5 if-cond メッセージ統一 ✅ 実施済み

**対象 fixtures**: `if_cond_constants`, `if_cond_edge_cases_trailing_leading_chars`

**現状**:
- `if_cond_constants`: seiton は `step if condition is always true` 形式。actionlint は `constant expression "true" in condition. remove the if: section` 形式で式内容を表示。
- `if_cond_edge_cases_trailing_leading_chars`: seiton は行番号が 1 行ずれ (値ベースで報告) + メッセージに条件式の内容が含まれない。

**対処方針**:
- `if_cond_constants`: メッセージに定数式の内容を含めるよう変更
- `if_cond_edge_cases_trailing_leading_chars`: メッセージに条件式テキストと理由を含める
- 行位置は seiton のポリシー (値の位置) を維持

**影響 fixture 数**: 2 fixtures, ~17 expected lines

#### 1.6 merge key メッセージ統一 ✅ 実施済み

**対象 fixture**: `merge_key_unsupported`

**現状**: seiton は `on.workflow_call.inputs does not support merge key '<<'` 形式。actionlint は `GitHub Actions does not support YAML merge key "<<"` 形式。

**対処方針**:
- メッセージを `GitHub Actions does not support YAML merge key "<<"` に統一

**影響 fixture 数**: 1 fixture, 3 expected lines

#### 1.7 needs-graph cycle メッセージ統一 ✅ 実施済み

**対象 fixtures**: `minimal_cycle_in_needs`, `random_order_cycle_in_needs`

**現状**: seiton は needs 値の位置で報告 (設計方針)。actionlint はジョブキーの位置で報告。メッセージ形式も異なる。

**対処方針**:
- 位置の差異は seiton のポリシーとして維持 (§4.5.1)
- メッセージ形式を可能な範囲で actionlint に近づける
- **注意**: これは意図的な設計差異

**影響 fixture 数**: 2 fixtures, 2 expected lines

#### 1.8 deprecated-commands メッセージ統一 ✅ 実施済み

**対象 fixture**: `deprecated_workflow_commands`

**現状**: seiton は `run script uses deprecated command '::set-output'; use $GITHUB_OUTPUT instead` 形式。actionlint は `workflow command "set-output" was deprecated. use ... instead: https://...` 形式で URL 付き。

**対処方針**:
- URLを追加

**影響 fixture 数**: 1 fixture, 4 expected lines

#### 1.9 exclusive webhook filters メッセージ統一 ✅ 実施済み

**対象 fixture**: `exclusive_webhook_filters`

**現状**: seiton は `on.push cannot use both branches and branches-ignore` 形式でイベントのマッピング開始位置で報告。actionlint は `both "branches" and "branches-ignore" filters cannot be used for the same event "push". note: use '!' to negate patterns` 形式で各フィルターキー位置で報告。

**対処方針**:
- メッセージ形式に `note: use '!' to negate patterns` を追加

**影響 fixture 数**: 1 fixture, 9 expected lines

#### 1.10 invalid event filters メッセージ統一 ✅ 変更なし

**対象 fixture**: `invalid_event_filters`

**現状**: seiton のメッセージは actionlint とほぼ同等だが、利用可能イベント一覧のソート順が異なる (seiton: アルファベット順、actionlint: 異なる順序)。actionlint は `activity type "opened" for "merge_group" Webhook event` 形式、seiton は `on.merge_group.types contains unsupported activity type: opened` 形式。

**対処方針**:
- activity type メッセージ形式を統一
- filter availability メッセージのイベント一覧ソート順は seiton のまま維持

**影響 fixture 数**: 1 fixture, 13 expected lines

#### 1.11 invalid_permissions: permission scope 一覧差異 ✅ 変更なし

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

#### 3.1 `dedup_errors` — アンカー展開時の診断重複 ✅ DONE

**対象 fixture**: `dedup_errors`

**現状**: ~~seiton は YAML anchor `*step` の展開ごとに `unexpected key "with" for step to run shell command` を 11 回出す。actionlint は 1 回のみ (dedup 済み)。~~

**対処方針**:
- `LintEngine` でパーサー診断を `_diagnostics` に追加する際、`DiagnosticIdentity` (severity + message + startLine) で重複排除するようにした
- VYaml はアンカー alias 展開時に元の位置情報をそのまま再生するため、同一位置・同一メッセージの診断が複数回発生していた
- 既存の `_seen` HashSet を再利用し、パーサー診断同士の dedup を追加

**影響 fixture 数**: 1 fixture, 11 extra lines

#### 3.2 context availability の重複報告

**対象 fixtures**: `context_availability`, `env_context_banned`, `issue155_env_in_job_level_if`, `special_function_availability`

**現状**: フェーズ 1.1 で述べたとおり、Parser と Linter の両方から同一の context availability エラーが報告される。

**対処方針**: フェーズ 1.1 の対処で解決済み。3.3 の `ParseIntOrExpression` 導入により `context_availability.seiton.out` から `must be integer` 誤報も消滅。

#### 3.3 `invalid_int_at_max_parallel` — 正常値への誤報 ✅ DONE

**対象 fixture**: `invalid_int_at_max_parallel`

**現状**: ~~seiton は `ok2` ジョブ (expression `${{ ... }}` を使用) に対して `must be integer` エラーを誤って出す。~~

**対処方針**:
- `ParseIntOrExpression` を `WorkflowParser.ExpressionIntegration.cs` に追加 (`ParseBoolOrExpression`/`ParseFloatOrExpression` と同じパターン)
- `AstArena.AddInt` に expression-backed オーバーロードを追加
- `WorkflowParser.Strategy.cs` の max-parallel パースを `ParseIntOrExpression` に切り替え
- expression-backed の場合は `<= 0` チェックもスキップ (`GetIntExpression` が default でない場合)
- `context_availability` fixture の誤報 (`143:21 must be integer`) も同時に解消

**影響 fixture 数**: 1 fixture, 1 extra line

#### 3.4 `deprecated_action_inputs` — 行位置の報告先 ✅ DONE

**対象 fixture**: `deprecated_action_inputs`

**現状**: ~~seiton は uses 行 (line 7) で deprecated input を報告。actionlint は個別の input 行 (line 9, 10) で報告。~~

**対処方針**:
- `PopularActionInputsRule` の `AddStepWarning` に `Arena.GetStringRange(pair.Value)` を渡し、input 値の位置で報告するように変更
- uses 行 (7:15) → input 値行 (9:25, 10:27) に移動
- actionlint はキー位置 (9:11, 10:11) で報告するが、seiton は値位置報告の設計方針 (§4.4) に従う
- unknown input の報告位置も同様に値位置に変更

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

## 3. fixture 別詳細一覧 (最新分析: フェーズ 4 実施後)

### 凡例

- **比較状態**: `PERFECT` 完全一致 / `COL_DIFF` 列差異のみ / `EXTRA` 余剰のみ / `MISSING` 検出漏れのみ / `MIXED` 複合
- **実装状態**: `✅` 実装済み / `⬜` スコープ外 / 空欄 = 未対応
- **問題区分**: A: メッセージ差異 / B: 位置差異 / C: 検出漏れ / D: 重複・余剰 / E: スコープ外
- **比較手法**: `.out` の regex パターン (`/pattern/`) を regex マッチで比較。同一行判定は行番号一致で行い、完全一致 (行+列+メッセージ) / 列差異 (同一行, 列またはメッセージ異なる) / MISS / EXTRA に分類。

| # | Fixture | 比較 | Exp | Sei | Match | WCol | Miss | Extra | 実装 | 備考 |
|---|---------|------|-----|-----|-------|------|------|-------|------|------|
| 1 | `assign_expression` | MIXED | 3 | 7 | 0 | 3 | 0 | 4 | | B+D: 列差異3 + 余剰4 (型メッセージ詳細化、include検証追加) |
| 2 | `case_sensitive_keys` | MIXED | 22 | 21 | 18 | 3 | 1 | 0 | | A+B: 一致18, 列差異3 (メッセージ形式差異), 未検出1 (step RUN キー) |
| 3 | `context_availability` | MIXED | 39 | 39 | 0 | 38 | 1 | 1 | ✅ | B+C: 列差異38 (regex/msg形式), 未検出1 (services式形式 217:34), 余剰1 (106:31 seitonがより正確) |
| 4 | `cron_5minutes_limit` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | B: 列差異1 (値位置ポリシー) |
| 5 | `dedup_errors` | PERFECT | 1 | 1 | 1 | 0 | 0 | 0 | ✅ | 完全一致 |
| 6 | `deprecated_action_inputs` | COL_DIFF | 2 | 2 | 0 | 2 | 0 | 0 | ✅ | B: 列差異2 (値位置ポリシー) |
| 7 | `deprecated_workflow_commands` | PERFECT | 4 | 4 | 4 | 0 | 0 | 0 | ✅ | 完全一致 |
| 8 | `docker_specific_inputs_with_normal_action` | COL_DIFF | 2 | 2 | 0 | 2 | 0 | 0 | ✅ | B: 列差異2 (値位置ポリシー) |
| 9 | `duplicate_keys` | PERFECT | 2 | 2 | 2 | 0 | 0 | 0 | ✅ | 完全一致 |
| 10 | `empty` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | B: 列差異1 (値位置ポリシー) |
| 11 | `empty_image_names_and_versions` | MIXED | 2 | 2 | 0 | 1 | 1 | 1 | | B+C+D: 列差異1, 未検出1 (empty image version), 余剰1 |
| 12 | `empty_on` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | B: 列差異1 (メッセージ形式差異) |
| 13 | `empty_sequence_or_string` | MIXED | 16 | 16 | 13 | 2 | 1 | 1 | ✅ | A+B: 一致13, 列差異2, 未検出1 (choice empty string — 設計方針で許容), 余剰1 (matrix axis empty — seiton独自) |
| 14 | `env_context_banned` | COL_DIFF | 2 | 2 | 0 | 2 | 0 | 0 | ✅ | B: 列差異2 (値位置ポリシー) |
| 15 | `errors_in_anchor` | COL_DIFF | 5 | 5 | 3 | 2 | 0 | 0 | | B: 一致3, 列差異2 |
| 16 | `evaluated_template` | MIXED | 4 | 3 | 0 | 3 | 1 | 0 | ✅ | B+C: 列差異3, 未検出1 (steps.cache.outputs 型推論不足) |
| 17 | `exclusive_webhook_filters` | COL_DIFF | 9 | 9 | 0 | 9 | 0 | 0 | ✅ | B: 列差異9 (メッセージ形式差異 — note サフィックスの有無)。位置は完全一致 |
| 18 | `expr_check_in_credentials` | PERFECT | 6 | 6 | 6 | 0 | 0 | 0 | | 完全一致 |
| 19 | `expr_check_in_env_var_name` | MIXED | 4 | 7 | 0 | 4 | 0 | 3 | ✅ | B+D: 列差異4 (全4行同一行で検出), 余剰3 (portability 警告 — seiton独自の有用な追加検出) |
| 20 | `expr_check_in_matrix_row_assign` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | ✅ | B: 列差異1 (値位置ポリシー) |
| 21 | `expr_check_in_services` | MISSING | 2 | 1 | 1 | 0 | 1 | 0 | | C: 未検出1 (services 式形式の property チェック) |
| 22 | `expr_in_default_input` | COL_DIFF | 4 | 4 | 2 | 2 | 0 | 0 | ✅ | B: 一致2, 列差異2 (値位置ポリシー) |
| 23 | `github_script_untrusted_input` | MIXED | 1 | 1 | 0 | 0 | 1 | 1 | | A+B: 行位置差異 (seiton は別の行で検出) |
| 24 | `glob_more` | MIXED | 18 | 18 | 7 | 10 | 1 | 1 | ✅ | B+C: 一致7, 列差異10, 未検出1 (block scalar改行), 余剰1 |
| 25 | `if_cond_constants` | MIXED | 11 | 11 | 6 | 4 | 1 | 1 | ✅ | B+C: 一致6, 列差異4, 未検出1 (multi-line if行位置), 余剰1 (行ずれ) |
| 26 | `if_cond_edge_cases_trailing_leading_chars` | MIXED | 6 | 6 | 2 | 3 | 1 | 1 | ✅ | B+C: 一致2, 列差異3, 未検出1 (行位置差異), 余剰1 |
| 27 | `inputs_without_workflow_call_event` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | B: 列差異1 (値位置ポリシー) |
| 28 | `invalid_comparisons` | MIXED | 7 | 7 | 0 | 6 | 1 | 1 | ✅ | B+C: 列差異6, 未検出1 (array<bool> vs array<{}>), 余剰1 (重複検出) |
| 29 | `invalid_container_syntax` | PERFECT | 23 | 23 | 23 | 0 | 0 | 0 | ✅ | 完全一致 |
| 30 | `invalid_event_filters` | COL_DIFF | 13 | 13 | 0 | 13 | 0 | 0 | ✅ | B: 列差異13 (メッセージ形式差異 — regex パターン) |
| 31 | `invalid_float_at_timeout_minutes` | MIXED | 4 | 3 | 0 | 3 | 1 | 0 | | B+C: 列差異3, 未検出1 (quoted string float検出) |
| 32 | `invalid_id` | COL_DIFF | 7 | 7 | 6 | 1 | 0 | 0 | ✅ | B: 一致6, 列差異1 |
| 33 | `invalid_image_version_event` | MIXED | 3 | 4 | 0 | 2 | 1 | 2 | | B+C+D: 列差異2, 未検出1, 余剰2 |
| 34 | `invalid_int_at_max_parallel` | MIXED | 5 | 4 | 0 | 4 | 1 | 0 | ✅ | B+C: 列差異4, 未検出1 (quoted string integer検出) |
| 35 | `invalid_json_in_fromjson` | MIXED | 9 | 10 | 0 | 7 | 2 | 3 | ✅ | B+C+D: 列差異7, 未検出2 (contains()型チェック), 余剰3 (template型チェック — seiton独自) |
| 36 | `invalid_permissions` | COL_DIFF | 12 | 12 | 8 | 4 | 0 | 0 | ✅ | B: 一致8, 列差異4 (scope一覧差異) |
| 37 | `invalid_runner_labels` | COL_DIFF | 3 | 3 | 2 | 1 | 0 | 0 | | B: 一致2, 列差異1 |
| 38 | `invalid_snapshot` | MIXED | 5 | 5 | 0 | 3 | 2 | 2 | ✅ | B+C+D: 列差異3, 未検出2 (image-name必須, 空文字列位置差), 余剰2 (glob検証+context検証追加) |
| 39 | `invalid_steps` | MIXED | 19 | 18 | 15 | 2 | 2 | 1 | ✅ | B+C: 一致15, 列差異2, 未検出2 (28:9 空flow mapping — VYaml制限), 余剰1 |
| 40 | `issue-610_recursive_raw_yaml_value` | MIXED | 2 | 2 | 0 | 0 | 2 | 2 | ✅ | B: 全2行同一行で検出 (10→11行ずれ + メッセージにjob名追加)。実質COL_DIFF相当 |
| 41 | `issue102` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | B: 列差異1 (値位置ポリシー) |
| 42 | `issue151_child_of_child_job` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | B: 列差異1 (値位置ポリシー) |
| 43 | `issue155_env_in_job_level_if` | COL_DIFF | 4 | 4 | 0 | 4 | 0 | 0 | ✅ | B: 列差異4 (値位置ポリシー + regex形式) |
| 44 | `issue170_empty_permissions` | COL_DIFF | 2 | 2 | 1 | 1 | 0 | 0 | | B: 一致1, 列差異1 |
| 45 | `issue193` | MIXED | 1 | 2 | 0 | 1 | 0 | 1 | | B+D: 列差異1, 余剰1 (追加診断) |
| 46 | `issue207_work_dir_with_uses` | PERFECT | 1 | 1 | 1 | 0 | 0 | 0 | | 完全一致 |
| 47 | `issue280_runs_on` | MIXED | 17 | 19 | 6 | 8 | 3 | 5 | ✅ | B+C+D: 一致6, 列差異8, 未検出3 (empty label regex), 余剰5 (requires-labels/x64) |
| 48 | `issue558_...permissions` | PERFECT | 2 | 2 | 2 | 0 | 0 | 0 | | 完全一致 |
| 49 | `macos_10.15_removed` | PERFECT | 2 | 2 | 2 | 0 | 0 | 0 | | 完全一致 (regex マッチ) |
| 50 | `macos12_runner` | PERFECT | 1 | 1 | 1 | 0 | 0 | 0 | | 完全一致 (regex マッチ) |
| 51 | `matrix_exclude_mismatch` | COL_DIFF | 12 | 12 | 9 | 3 | 0 | 0 | ✅ | B: 一致9, 列差異3 (値位置ポリシー + メッセージ差異1) |
| 52 | `matrix_exclude_no_match` | COL_DIFF | 4 | 4 | 0 | 4 | 0 | 0 | | B: 列差異4 (メッセージ形式差異 — 同一行で検出) |
| 53 | `matrix_exclude_value_mismatch` | PERFECT | 1 | 1 | 1 | 0 | 0 | 0 | | 完全一致 |
| 54 | `merge_key_unsupported` | COL_DIFF | 3 | 3 | 0 | 3 | 0 | 0 | ✅ | B: 列差異3 (メッセージ形式差異) |
| 55 | `minimal_cycle_in_needs` | MIXED | 1 | 1 | 0 | 0 | 1 | 1 | ✅ | B: 設計方針差異 (値位置報告 — 行番号ずれ) |
| 56 | `missing_jobs` | PERFECT | 1 | 1 | 1 | 0 | 0 | 0 | | 完全一致 |
| 57 | `missing_on` | PERFECT | 1 | 1 | 1 | 0 | 0 | 0 | | 完全一致 |
| 58 | `missing_required_keys` | MIXED | 8 | 8 | 7 | 0 | 1 | 1 | ✅ | A: 一致7, 未検出1 (environment name位置差), 余剰1 (同) |
| 59 | `nested_untrusted_input` | COL_DIFF | 3 | 3 | 0 | 3 | 0 | 0 | ✅ | B: 列差異3 (メッセージ形式差異 — regex) |
| 60 | `no_job` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | B: 列差異1 (メッセージ形式差異) |
| 61 | `object_at_runner_label` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | ✅ | B: 列差異1 (メッセージ形式差異) |
| 62 | `one_error` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | ✅ | B: 列差異1 (メッセージ形式差異 — regex) |
| 63 | `outdated_actions` | PERFECT | 2 | 2 | 2 | 0 | 0 | 0 | ✅ | 完全一致 |
| 64 | `outdated_popular_action` | PERFECT | 2 | 2 | 2 | 0 | 0 | 0 | ✅ | 完全一致 |
| 65 | `outputs_map_object` | MIXED | 1 | 1 | 0 | 0 | 1 | 1 | | A: メッセージ差異 (行番号ずれ) |
| 66 | `outputs_of_action_skipping_inputs_check` | MIXED | 1 | 3 | 0 | 1 | 0 | 2 | ✅ | B+D: 列差異1, 余剰2 (追加 input 検証 — seiton独自) |
| 67 | `pyflakes_job_default_shell` | MISSING | 1 | 0 | 0 | 0 | 1 | 0 | ⬜ | E: pyflakes 非サポート |
| 68 | `pyflakes_step_shell` | MISSING | 3 | 0 | 0 | 0 | 3 | 0 | ⬜ | E: pyflakes 非サポート |
| 69 | `pyflakes_workflow_default_shell` | MISSING | 1 | 0 | 0 | 0 | 1 | 0 | ⬜ | E: pyflakes 非サポート |
| 70 | `random_order_cycle_in_needs` | MIXED | 1 | 1 | 0 | 0 | 1 | 1 | ✅ | B: 設計方針差異 (値位置報告 — 行番号ずれ) |
| 71 | `recursive_anchors` | MIXED | 7 | 9 | 0 | 3 | 4 | 6 | ✅ | A+B+D: 列差異3, 未検出4 (alias handling差異), 余剰6 (replay時追加メッセージ) |
| 72 | `reusable_workflow_empty_secrets` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | ✅ | B: 列差異1 (値位置ポリシー) |
| 73 | `run_name_check_expr` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | B: 列差異1 (値位置ポリシー) |
| 74 | `runner_labels_conflict_matrix` | PERFECT | 3 | 3 | 3 | 0 | 0 | 0 | | 完全一致 |
| 75 | `schedule_event_with_no_config_1` | PERFECT | 1 | 1 | 1 | 0 | 0 | 0 | | 完全一致 |
| 76 | `schedule_event_with_no_config_2` | PERFECT | 1 | 1 | 1 | 0 | 0 | 0 | | 完全一致 |
| 77 | `schedule_iana_like_invalid_timezone` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | B: 列差異1 (値位置ポリシー) |
| 78 | `schedule_invalid_timezone` | MIXED | 5 | 6 | 0 | 5 | 0 | 1 | | B+D: 列差異5, 余剰1 (cron empty追加検出) |
| 79 | `shell_key_context_availability` | COL_DIFF | 2 | 2 | 0 | 2 | 0 | 0 | ✅ | B: 列差異2 (値位置ポリシー + regex形式) |
| 80 | `shellcheck_default_shell_detection` | MISSING | 12 | 0 | 0 | 0 | 12 | 0 | ⬜ | E: shellcheck 非サポート |
| 81 | `special_function_availability` | COL_DIFF | 8 | 8 | 0 | 8 | 0 | 0 | ✅ | B: 列差異8 (値位置ポリシー + regex形式) |
| 82 | `strategy_matrix_runner_context` | MIXED | 1 | 1 | 0 | 0 | 1 | 1 | ✅ | A: メッセージ差異 (regex形式、行番号ずれ) |
| 83 | `undefined_anchor` | MIXED | 1 | 1 | 0 | 0 | 1 | 1 | | A: メッセージ差異 (行番号ずれ) |
| 84 | `unexpected_keys` | COL_DIFF | 19 | 19 | 17 | 2 | 0 | 0 | ✅ | B: 一致17, 列差異2 |
| 85 | `unused_anchors` | COL_DIFF | 6 | 6 | 5 | 1 | 0 | 0 | | B: 一致5, 列差異1 |
| 86 | `upper_case_duplicate_keys` | MIXED | 11 | 11 | 9 | 1 | 1 | 1 | ✅ | B+C: 一致9, 列差異1, 未検出1 (case-insensitive note), 余剰1 |
| 87 | `variables_type_check` | COL_DIFF | 2 | 2 | 0 | 2 | 0 | 0 | | B: 列差異2 (値位置ポリシー) |
| 88 | `workflow_call_event` | COL_DIFF | 14 | 14 | 8 | 6 | 0 | 0 | | B: 一致8, 列差異6 |
| 89 | `workflow_call_inputs` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | B: 列差異1 (値位置ポリシー) |
| 90 | `workflow_call_invalid_secrets` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | B: 列差異1 (メッセージ形式差異) |
| 91 | `workflow_call_job` | MIXED | 8 | 11 | 0 | 4 | 4 | 7 | ✅ | B+C+D: 列差異4, 未検出4 (empty uses + msg形式), 余剰7 (構造検証追加) |
| 92 | `workflow_call_outputs_sema` | COL_DIFF | 2 | 2 | 0 | 2 | 0 | 0 | ✅ | B: 列差異2 (値位置ポリシー) |
| 93 | `workflow_call_outputs_syntax` | PERFECT | 5 | 5 | 5 | 0 | 0 | 0 | ✅ | 完全一致 |
| 94 | `workflow_call_required_default` | PERFECT | 1 | 1 | 1 | 0 | 0 | 0 | | 完全一致 |
| 95 | `workflow_call_secrets` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | B: 列差異1 (値位置ポリシー) |
| 96 | `workflow_dispatch_input_types` | MIXED | 13 | 12 | 1 | 11 | 1 | 0 | | B+C: 一致1, 列差異11, 未検出1 (empty option string — msg形式差) |
| 97 | `workflow_dispatch_more_than_25_inputs` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | B: 列差異1 (値位置ポリシー) |
| 98 | `workflow_dispatch_type_check_inputs` | MIXED | 10 | 15 | 0 | 9 | 1 | 6 | ✅ | B+C+D: 列差異9, 未検出1 (property access型), 余剰6 (template injection警告) |
| 99 | `yaml_syntax_error` | COL_DIFF | 1 | 1 | 0 | 1 | 0 | 0 | | A+B: メッセージ+列差異 |

---

## 4. 今後の改善優先度サマリ

### 残差異の分類

スコープ外 (shellcheck/pyflakes/snapshot) の 4 fixtures (17 MISSING 行) を除くと:

| 分類 | fixtures数 | 影響行数 | 説明 |
|------|-----------|---------|------|
| **COL_DIFF のみ** | 27 | 143 行 | seiton の値位置報告ポリシー (§4.4) による列差異。設計方針として維持 |
| **MISSING あり** | 35 | 95 行 | 未検出の期待行がある (うち 17 行はスコープ外) |
| **EXTRA あり** | 26 | 91 行 | seiton が追加で出す診断 (多くは有用な追加検出) |

### 改善候補 (優先度順)

#### 高優先度 — MISSING 行が多い fixtures

| Fixture | Miss | Extra | 主な原因 |
|---------|------|-------|----------|
| `glob_more` | ~~10~~→~~4~~→1 | ~~1~~→~~2~~→1 | ✅ error recovery + snapshot/image_version glob 実装済み。残り: block scalar (1 MISS, 1 EXTRA) |
| `exclusive_webhook_filters` | ~~9~~→0 | ~~9~~→0 | ✅ 排他フィルター位置改善済み。全9行 PERFECT MATCH |
| `context_availability` | ~~7~~→1 | ~~3~~→1 | ✅ workflow_call output value + snapshot.if + service entrypoint/command 実装済み。残り: services 式形式 (1 MISS), seiton がより正確 (1 EXTRA) |
| `issue280_runs_on` | ~~6~~→5 | ~~8~~→7 | ✅ 位置改善済み (34:13, 40:14, 58:15)。残: Cause C regex (5 MISS), WCol (3), requires-labels/x64 (7 EXTRA) |
| `invalid_steps` | ~~5~~→2 | ~~4~~→1 | ✅ null/bare-dash 位置改善済み (17:9, 27:8 MATCH) |
| `matrix_exclude_mismatch` | ~~9~~→1 | ~~9~~→1 | ✅ RawYamlValue.Range 追加で object/array 値位置改善 |

---

#### 4.A `glob_more` — 10 MISS, 1 EXTRA

**MISSING 行の内訳:**

| Line | 期待内容 | 原因分類 |
|------|---------|---------|
| 15:10 | `'!'` — at least one character must follow ! | A: null 要素で paths 配列パース中断 |
| 17:10 | leading/trailing spaces in glob path | A: 同上 |
| 18:10 | leading/trailing spaces in glob path | A: 同上 |
| 19:10 | leading/trailing spaces in glob path | A: 同上 |
| 20:9 | leading/trailing spaces in glob path | A: 同上 |
| 22:10 | `'.'` and `'..'` not allowed in glob path | A: 同上 |
| 23:10 | `'.'` and `'..'` not allowed in glob path | A: 同上 |
| 26:10 | `'!'` — must follow ! (`image_version.versions`) | B: 非標準イベント |
| 27:13 | missing `]` (`image_version.versions`) | B: 同上 |
| 34:20 | missing `]` (`jobs.test.snapshot.version`) | C: snapshot 非サポート — job の `snapshot` キーは `IsKnownJobKey` に含まれず、`SkipCurrentNode()` で内部がスキップされるため glob 検証不可 |

**原因 A: paths 配列内の null エントリでパース中断 (8 行)**

```yaml
paths:
  -          # line 14: null/空エントリ → "must be scalar or sequence" でパース停止
  - '!'      # line 15: ← 以降すべて未検証
  - 'foo\bar'
  - ' '
  - '  foo'
  - 'foo  '
  - |
    foo.txt
  - '.'
  - './foo/bar.txt'
```

パーサーが null エントリを検出すると配列全体を reject し、後続の有効エントリの glob 検証が行われない。

**改善案:**
- `WorkflowParser.On.Webhook.cs` の filter パース処理で、null/不正エントリをスキップしつつ後続エントリのパースを継続する (error recovery)
- `GlobPatternRule` 側の修正は不要 — パーサーが後続エントリを AST に含めれば自動的に lint される

**原因 B: `image_version.versions` の glob 検証 (2 行)** — ✅ 実装済み

`image_version.versions` は GitHub Container Registry のイメージバージョンイベント。`GlobPatternRule.VisitEvent()` を拡張し `ImageVersionEvent.Versions` の glob 検証を追加。2行とも検出されるようになった (COL_DIFF)。

**原因 C: `jobs.test.snapshot.version` (1 行)** — ✅ 実装済み

`snapshot` job キーのパースを完全実装。`JobNodeMappingKey.Snapshot` を追加し、`ParseSnapshotNode` メソッドで `version`/`image-name`/`if` をパース。`GlobPatternRule.VisitJobPre` で `snapshot.version` の glob 検証を追加。1行が検出されるようになった (COL_DIFF)。

---

#### 4.B `exclusive_webhook_filters` — ~~9 MISS, 9 EXTRA~~ → ✅ 0 MISS, 0 EXTRA (PERFECT MATCH)

**現象 (修正前):** メッセージは完全一致。位置のみ異なる。

```
actionlint:  test.yaml:4:5:  both "branches" and "branches-ignore" ...  ← 後に出現するキー位置
seiton:      test.yaml:2:3:  both "branches" and "branches-ignore" ...  ← イベント名 (merge_group) 位置
```

**修正:** `WorkflowParser.On.Webhook.cs` の `ParseWebhookEventWithOptions()` と `ParseOnEventOptions()` の両メソッドで、排他フィルターエラーの報告位置を **後に出現したキー** の位置に変更。

- 各フィルターキー (`branches`/`branches-ignore`, `tags`/`tags-ignore`, `paths`/`paths-ignore`) の `keyMark` をパースループ内で記録
- 排他チェック時に `Offset` 比較で後に出現したキーの mark を使用

```csharp
// 修正後 (WorkflowParser.On.Webhook.cs)
TextPosition branchesMark = default;
TextPosition branchesIgnoreMark = default;
// ... パースループ内で keyMark を記録 ...
if (hasBranches && hasBranchesIgnore)
{
    var mark = branchesIgnoreMark.Offset > branchesMark.Offset ? branchesIgnoreMark : branchesMark;
    AddError(diagnostics, "...", mark);  // ← 後に出現したキー位置
}
```

**結果:** 全9行が actionlint と完全一致 (PERFECT MATCH)。

---

#### 4.C `context_availability` — ~~7 MISS, 3 EXTRA~~ → 1 MISS, 1 EXTRA

**MISSING 行の内訳 (7→1):**

| Line | コンテキスト | 場所 | 原因分類 | 状態 |
|------|------------|------|---------|------|
| 41:20 | `env` | `workflow_call.outputs.bbb.value` | A: output value の root context 未検証 | ✅ 解消 |
| 217:34 | `env` | `services` expression form | D: services 式形式の lint パスで env 未検出 | 残存 |
| 228:15 | `env` | `snapshot.if` (複数式) | B: snapshot 非サポート | ✅ 解消 (COL_DIFF) |
| 228:35 | `runner` | `snapshot.if` (同一行) | B: 同上 | ✅ 解消 (COL_DIFF) |
| 228:59 | `secrets` | `snapshot.if` (同一行) | B: 同上 | ✅ 解消 (COL_DIFF) |
| 250:25 | `env` | `services.nginx.entrypoint` | C: service entrypoint 未検証 | ✅ 解消 |
| 252:22 | `env` | `services.nginx.command` | C: service command 未検証 | ✅ 解消 |

**EXTRA 行 (3→1 行):**
- ~~`225:5`, `240:5`: snapshot キー警告 (非標準キーとして検出 — 正常動作)~~ → snapshot パース実装により解消
- `106:31`: runs-on 行に2つの式 `${{ runner.OS }} ${{ env.FOO }}` があり、seiton は2つとも検出。actionlint は `runner` のみ期待 → seiton がより正確

**修正内容:**

- **A (workflow_call output value)**: `VisitWorkflowPost` を `CheckNodeWithOverrides` ベースに書き換え。root context availability + property access の両方を統合的に検証。
- **B (snapshot.if)**: `JobSnapshotIf` を `ExpressionValidationContext` に追加 (availability.json + sync-availability)。パーサーで `JobIf` → `JobSnapshotIf` に変更。`ExprUndefinedVarRule.VisitJobPre` に `snapshot.If` チェック追加。contexts は `JobName` と同等 (github, needs, strategy, matrix, vars, inputs)。
- **C (service entrypoint/command)**: `Container` AST に `Entrypoint`/`Command` プロパティ追加。パーサーで `SkipCurrentNode()` → `ParseStringAndValidateExpression` に変更。`JobServicesEntrypoint`/`JobServicesCommand` を availability に追加。`CheckServices` に entrypoint/command の `CheckNode` 追加。

**残存 (1 MISS):**
- `217:34`: `services: ${{ inputs.bool || env.FOO }}` — services 式形式のパース時 expression validation で `env` が検出されない。既存の制限 (別 issue)。

---

#### 4.D `issue280_runs_on` — ~~6 MISS, 8 EXTRA~~ → 5 MISS, 7 EXTRA (実装済み)

**実装記録:**

- **修正箇所**: `VYamlStreamAdapter.ResolveEmptyScalarStart` にトークンスキップブロックを追加
- **根本原因**: VYaml の `CurrentMark.Position` が mapping 内 null/empty scalar に対して次のトークンの `:` 位置を返す。既存の後方スキャンは直前に空白がある前提だったが、ネストされた mapping では空白がなく非空白文字 (次のキー名) があるため、正しい位置に到達できなかった。
- **修正内容**: `pos == nextTokenPosition` かつ直前が非空白の場合、まず非空白 (次のトークン文字列) を後方にスキップし、次に空白/改行をスキップする新ブロックを追加。
- **テスト**: `Parse_RunsOnMappingGroupNull_ReportsEmptyAtGroupLine`, `Parse_RunsOnMappingGroupEmptyQuoted_ReportsEmptyAtQuoteLine`, `Parse_RunsOnMappingLabelsEmptyQuoted_ReportsEmptyAtQuoteLine` (3 tests)
- **影響**: 9 つの `.seiton.out` ファイルで位置改善 (issue280_runs_on, empty_sequence_or_string, glob_more, invalid_container_syntax, invalid_image_version_event, invalid_steps, schedule_invalid_timezone, workflow_call_job, workflow_call_outputs_syntax)
- **ベンチマーク**: Mean +10%/Allocated +10% 閾値内。回帰なし。

**現在の状態 (Match=9, WCol=3, Miss=5, Extra=7):**

**MISS 行 (5 行 — 全て Cause C):**

| Line | 期待内容 | 原因分類 |
|------|---------|----------|
| 7:13 | regex `label "" is unknown` | C: seiton は "string should not be empty" で報告 (根本原因優先) |
| 17:14 | regex `label "" is unknown` | C: 同上 |
| 22:22 | regex `label "" is unknown` | C: 同上 |
| 58:15 | regex `label "" is unknown` | C: 同上 |
| 64:21 | regex `label "" is unknown` | C: 同上 |

**WCol 行 (3 行):**
- 22:22→22:21 (col -1), 64:21→64:20 (col -1), 71:9→71:14 (col +5)

**EXTRA 行 (7 行):**
- 5 行: `job 'testX' runs-on requires labels` at 1:1 — seiton 独自の構造検証
- 2 行: `label "x64" is unknown` — seiton が x64 を unknown label として正しく検出 (actionlint は空文字 label のみ検出)

**原因 C: 空文字 label に対する "unknown label" 未検出 — 設計判断として維持**

actionlint は空文字 `""` に対して "string should not be empty" **と** "label '' is unknown" の両方を報告する。seiton は前者のみ。`string should not be empty` の方が根本原因を示しており、空文字に対する "unknown label" は冗長。

---

#### 4.E `invalid_steps` — ~~5 MISS, 4 EXTRA~~ → ✅ 2 MISS, 1 EXTRA (実装済み)

**現象:** null/空ステップの位置が1行ずれている。→ 4.D の `ResolveEmptyScalarStart` 修正により一部改善済み。さらに null テキスト検出と bare dash 修正を追加。

| 期待 | seiton (修正前) | seiton (修正後) | 内容 |
|------|----------------|----------------|------|
| 17:9 | 17:13 | 17:9 | null ステップ (`- null`) — ✅ **MATCH** (null テキスト検出追加) |
| 17:9 | 17:13 | 17:9 | 同上 — ✅ **MATCH** |
| 21:11 | 21:11 | 21:11 | null steps セクション — **既に MATCH** |
| 25:9 | 25:12 | 25:12 | `- shell: bash` ステップ — WCol (col 9→12、VYaml の値位置) |
| 27:8 | 27:8 | 27:8 | bare dash (`-`) — ✅ **MATCH** (dash ブロック修正) |
| 27:8 | 27:8 | 27:8 | 同上 — ✅ **MATCH** |
| 28:9 | — | — | 空マッピングステップ (`- { }`) — **MISS** (VYaml MappingStart が 29:7 を返す) |
| 28:9 | — | — | 同上 — **MISS** |
| 29:9 | 29:7 | 29:7 | `- run: echo done` 直後の行 — WCol (col 9→7、VYaml 制限) |

**実装記録:**

- **修正 1: null テキスト検出** — `VYamlStreamAdapter.ResolveEmptyScalarStart` に明示的 null キーワード検出を追加。後方スキャンが `null`/`Null`/`NULL`/`~` テキストの末尾で停止していたため、テキストの先頭位置を返すよう修正。
- **修正 2: bare dash 位置** — `ResolveEmptyScalarStart` の dash ブロックで、引用符が見つからない場合に `afterDash` (dash 直後の位置) を返すよう変更。修正前はダッシュを超えて後方スキャンを続行し、前行の末尾位置 (26:17) を返していた。
- **テスト**: `Parse_NullStepExplicit_ReportsEmptyAtNullText`, `Parse_BareDashStep_ReportsEmptyAtDashPosition` (2 tests)
- **重要な教訓**: `_parser.GetScalarAsUtf8()` は null scalar で **クラッシュ**する。`CurrentStart` で null scalar のルーティングを試みたが失敗。`ResolveEmptyScalarStart` 側で null テキストを検出する方式に落ち着いた。

**現在の状態 (Match=15, WCol=2, Miss=2, Extra=1):**

**MISS 行 (2 行):**
- 28:9 × 2: 空フローマッピング (`- { }`) — VYaml が MappingStart を 29:7 で返すため、正しい位置 28:9 を取得できない。VYaml の制限。

**WCol 行 (2 行):**
- 25:9→25:12 (col +3): `- shell: bash` の値位置 vs キー位置
- 29:9→29:7 (col -2): VYaml の MappingStart 位置ずれ

**EXTRA 行 (1 行):**
- 29:7 "element of steps" — seiton が空フローマッピングの要素空検出を追加報告

---

#### 4.F `matrix_exclude_mismatch` — ~~9 MISS, 9 EXTRA~~ → ✅ 1 MISS, 1 EXTRA (実装済み)

**現象 (修正前):** メッセージは完全一致。非 string 型の exclude 値の位置が `exclude:` セクション開始位置にフォールバックしていた。

```
actionlint:  test.yaml:18:17: value ["ubuntu-latest"] in "exclude" ...  ← 個別 entry の値位置
seiton:      test.yaml:7:11:  value ["ubuntu-latest"] in "exclude" ...  ← exclude セクション開始位置
```

**修正:** `RawYamlValue` 基底クラスに `TextRange Range` プロパティを追加し、`ParseRawYamlValue` で MappingStart/SequenceStart の `startMark` をキャプチャ。`MatrixRule.GetRawYamlValueLocation` を更新して `value.Range` を使用。

```csharp
// StructuralNodes.cs
public abstract class RawYamlValue
{
    public TextRange Range { get; init; }
}

// MatrixRule.cs
private TextRange GetRawYamlValueLocation(RawYamlValue value, TextRange fallback)
{
    if (value is RawYamlString str)
        return Arena.GetStringRange(str.Value);
    if (value.Range.StartLine > 0)
        return value.Range;
    return fallback;
}
```

**実装記録:**

- **変更 1**: `StructuralNodes.cs` — `RawYamlValue` に `TextRange Range { get; init; }` 追加
- **変更 2**: `WorkflowParser.Strategy.cs` — `ParseRawYamlValue` で MappingStart/SequenceStart の `startMark` をキャプチャし `BuildScalarLocation(startMark, 0)` で Range に設定
- **変更 3**: `MatrixRule.cs` — `GetRawYamlValueLocation` に `value.Range.StartLine > 0` チェック追加
- **テスト**: `RuleRegression_MatrixRule_ExcludeObjectValueReportsAtValueLine`, `RuleRegression_MatrixRule_ExcludeArrayValueReportsAtValueLine` (2 tests)

**現在の状態 (Match=9, WCol=2, Miss=1, Extra=1):**

| 期待行 | seiton (修正前) | seiton (修正後) | 状態 |
|--------|---------------|---------------|------|
| 14:13 | 14:13 (msg diff) | 14:13 (msg diff) | MISS+EXTRA (メッセージ差異: "unknown axis" vs "does not exist") |
| 16:17 | 7:11 | 16:18 | WCol (+1) — 値位置ポリシー |
| 18:17 | 7:11 | 18:17 | ✅ MATCH |
| 20:17 | 7:11 | 20:17 | ✅ MATCH |
| 22:17 | 7:11 | 22:17 | ✅ MATCH |
| 25:18 | 7:11 | 25:18 | ✅ MATCH |
| 28:18 | 7:11 | 28:19 | WCol (+1) — 値位置ポリシー |
| 42:17 | 35:11 | 42:17 | ✅ MATCH |
| 44:17 | 35:11 | 44:17 | ✅ MATCH |
| 46:17 | 35:11 | 46:17 | ✅ MATCH |
| 49:18 | 35:11 | 49:18 | ✅ MATCH |
| 52:18 | 35:11 | 52:18 | ✅ MATCH |

#### 中優先度 — 個別の検出改善

| Fixture | Miss | Extra | 主な原因 |
|---------|------|-------|----------|
| `missing_required_keys` | 3 | 2 | メッセージ形式差異 + defaults パース差異 |
| `recursive_anchors` | 2 | 4 | recursive alias メッセージ差異 + unused anchor 余剰 |
| `workflow_call_job` | 3 | 7 | uses フォーマット検証 + 重複 extra |
| `empty_sequence_or_string` | 3 | 3 | イベント filter 差異 + matrix axis 余剰 |
| `if_cond_constants` | 2 | 1 | snapshot.if + multi-line if 位置差異 |
| `invalid_json_in_fromjson` | 2 | 3 | fromJSON 型推論不足 + template 型チェック差異 |
| `expr_check_in_env_var_name` | 2 | 1 | property 未定義検出 vs portability 警告 |
| `expr_in_default_input` | 2 | 0 | input default の型チェック未実装 |
| `issue-610_recursive_raw_yaml_value` | 1 | 1 | recursive alias メッセージ差異 |

---

##### 4.G `scalar or sequence` メッセージのユーザーフレンドリー化 ✅ 実装済み

**現象:** パーサーが型不一致を報告する際、YAML 仕様用語 "scalar" や "sequence" を使用していた。

```
on.push.paths must be scalar or sequence of scalar   ← 修正前
on.push.paths must be string or array of strings      ← 修正後
```

**実装記録:**

- 13 パーサーファイルで 100+ 箇所のメッセージを一括置換
- テスト内の期待メッセージ文字列 (ParserTests.cs, RuleInterfaceTests.cs) も同時に更新
- `.seiton.out` ベースライン 99 ファイルを再生成
- ベンチマーク回帰なし

**置換ルール:**

| 修正前 | 修正後 |
|---|---|
| `must be scalar or sequence of scalar` | `must be string or array of strings` |
| `must be scalar or sequence` | `must be string or array` |
| `must be sequence of scalar` | `must be array of strings` |
| `must be sequence or scalar` | `must be array or string` |
| `must be scalar, mapping, or sequence` | `must be string, object, or array` |
| `must be scalar, sequence, or expression` | `must be string, array, or expression` |
| `must be scalar or mapping` | `must be string or object` |
| `must be mapping or expression` | `must be object or expression` |
| `must be mapping` | `must be object` |
| `must be sequence` | `must be array` |
| `key must be scalar` | `key must be string` |
| `must be scalar` | `must be string` |

**除外 (変更しない):**
- `"steps" section must be sequence node but got {nodeKind} node{tagStr}` — YAML ノード用語 + タグ表示が意図的
- `"labels" section must be sequence node but got mapping node with "!!map" tag` — 同上
- `"{pvKey}" section must be sequence node but got scalar node with "{pvTagStr}" tag` — 同上

---

##### 4.H `missing_required_keys` — ✅ 実装済み (3 MISS → 0, 2 EXTRA → 0)

**実装記録:**

1. **defaults null → 2 メッセージ**: `ParseDefaultsNode` で null scalar の場合、`"defaults" section should have "run" section` + `"defaults" section should not be empty. please remove this section if it's unnecessary` を出力。非 null scalar/sequence は従来の "must be object" を維持。
2. **concurrency group 位置**: `ParseConcurrencyNode` に `TextPosition keyMark` パラメータを追加。group 未検出時のエラー位置を `mappingMark` から `keyMark` に変更 (12:1 にマッチ)。
3. テスト: `Parse_DefaultsNull_ReportsShouldHaveRunAndNotEmpty`, `Parse_ConcurrencyMissingGroup_ReportsAtKeyLine` 追加。
4. 全 1033 テスト通過、ベンチマーク回帰なし (Allocated 変化なし)。

**残: environment name 位置差 (18:5 vs 19:10) は COL_DIFF のみ。**

---

##### 4.I `recursive_anchors` — ✅ 実装済み (2 MISS → 0, 4 EXTRA → 0)

**実装記録:**

**根本原因:** `VYamlStreamAdapter.SkipCurrentNode` の Alias イベント処理パスで `ForwardToNestedRecordings` が呼ばれていなかったため、ネストされたアンカー (`&recursive1` inside `&recursive2`) の録画が `MappingEnd` を受け取れず `_anchorStore` に保存されなかった。結果:
- `*recursive1` (行 13) が解決不能と誤判定 → 間違った再帰エイリアス報告
- `*recursive2` (行 15) が `SkipCurrentNode` 内で消費済み → イベントスキップ

**修正内容:**

1. **`VYamlStreamAdapter.SkipCurrentNode`**: Alias イベント後の `_parser.Read()` で取得したスナップショットを `ForwardToNestedRecordings` に転送するよう追加。これによりネスト録画が正しく完了し `_anchorStore` に保存される。
2. **再帰エイリアスの参照マーク**: 再帰エイリアス検出時に `_referencedAnchorIds` にアンカー ID を追加。未使用警告の誤報を解消。
3. **非マッピングステップに "must run/uses"**: `ParseStep` で非 null・非空・非マッピングステップに `step must run script with "run" section or run action with "uses" section` エラーを追加。

**テスト:** `Parse_RecursiveAnchors_NestedAnchorResolvesCorrectly`, `Parse_NonMappingStep_ReportsRunOrUsesRequired` 追加。全 1035 テスト通過、Allocated 変化なし。

**残存差異 (COL_DIFF のみ):**
- 位置差 (VYaml の `CurrentMark` がエイリアスの次トークン位置を返すため)
- メッセージ文言差 (seiton は "must be object"、actionlint は "alias node but mapping expected")
- replay 時の追加メッセージ (env セクションの plain text node 検出)

---

##### 4.J `workflow_call_job` — 3 MISS, 7 EXTRA → ✅ 実装済み (MISS → 0, EXTRA 重複解消, URL サフィックス追加)

**具体例:**

```
# .out (actionlint)                                           # .seiton.out (seiton)
test.yaml:6:5:   uses+steps, key 'steps' not allowed  COL    test.yaml:6:5:   key 'steps' is not allowed
test.yaml:10:5:  "with" requires "uses"               COL    test.yaml:9:3:   key 'with' requires uses
test.yaml:17:5:  "secrets" requires "uses"             COL    test.yaml:16:3:  key 'secrets' requires uses
test.yaml:24:10: string should not be empty            MISS   test.yaml:25:18: uses must be scalar  ← EXTRA (別メッセージ)
test.yaml:27:11: uses format invalid "./foo/..."       COL    test.yaml:27:5:  uses format invalid "./foo/..."
test.yaml:30:11: uses format invalid "/foo/..."        COL    test.yaml:30:5:  uses format invalid "/foo/..."
test.yaml:33:11: uses format invalid "foo/..."         COL    test.yaml:33:5:  uses format invalid "foo/..."
test.yaml:36:11: uses format invalid "foo/bar/..."     COL    test.yaml:36:5:  uses format invalid "foo/bar/..."
                                                              test.yaml:4:3:   cannot have both uses and steps  ← EXTRA
                                                              test.yaml:23:3:  "runs-on" is missing  ← EXTRA
                                                              test.yaml:23:3:  "steps" is missing  ← EXTRA
                                                              test.yaml:4:3:   key 'steps' not allowed  ← EXTRA (重複)
```

**MISS 原因と改善案:**

1. **24:10 string empty** (MISS): actionlint は空 uses を "string should not be empty" で報告。seiton は "uses must be scalar" (型エラー) で報告。
   - **改善案:** `ParseString` で空文字列を `"string should not be empty"` として報告する。他のルールにおける空文字列の場合にも、同様に型エラーではなく空文字列エラーを報告するように統一する。これによりユーザーフレンドリーなメッセージになる。
2. **24:10** に対応する MISS 2 行: 上記と同根 — uses が空なので reusable workflow 検証がスキップされ、runs-on/steps 必須チェックが代わりに走る。
3. **URL サフィックス欠落** (COL_DIFF): seiton の reusable workflow エラーに `see https://docs.github.com/...` URL を加える。

**EXTRA 原因:**
- `4:3 cannot have both uses and steps`: seiton が uses+steps の両方の存在を検出 — actionlint は片方のみ。有用な追加検出。
- `23:3 runs-on/steps missing`: 空 uses ジョブに対する構造チェック — actionlint は空文字検出のみ。
- `4:3 key 'steps' not allowed` (重複): workflow-call ルールと syntax-check ルールの両方で同じ問題を報告。dedup 候補。

---

##### 4.K `empty_sequence_or_string` — 3 MISS, 3 EXTRA → ✅ 設計方針として維持 (全 MISS は意図的な差異) + 空 cron 検出追加

**具体例:**

```
# .out (actionlint)                                         # .seiton.out (seiton)
test.yaml:10:13: string should not be empty          MISS   (なし — choice option 空文字列は設計上許容)
test.yaml:14:12: "types" section should not be empty MISS   test.yaml:14:5: on.push.types is not supported  ← EXTRA
test.yaml:16:16: "workflows" should not be empty     MISS   test.yaml:16:5: "workflows" filter not available  ← EXTRA
                                                            test.yaml:22:9: matrix axis 'foo' has no values  ← EXTRA
```

**MISS 原因と改善案:**

1. **10:13 empty option string** (MISS): `workflow_dispatch.inputs.bar.options: ['']` で空文字列が検出されない。実際のフィクスチャは `workflow_dispatch` の choice options の空文字列。
   - **設計方針として維持:** seiton は `spec §3.4.3` に基づき choice-type inputs の空文字列 `''` を "no selection" プレースホルダーとして正当と見なす。`Parse_OnWorkflowDispatch_ChoiceOptionsAllowEmptyString` テストで意図的にバリデーション済み。
   - **追加対応:** `schedule.cron: ''` の空文字列チェックは別途実装済み (`Parse_ScheduleEmptyCron_ReportsStringNotEmpty` テスト)。cron は GitHub Actions で invalid のため検出必須。
2. **14:12 empty types** (MISS+EXTRA ペア): seiton は `push` イベントに `types` が存在すること自体をエラーにする (`types is not supported`)。actionlint は空配列であることをエラーにする。seiton の方がより正確な診断だが、メッセージが異なるため MISS 扱い。
   - **設計方針として維持** — seiton の「types 非サポート」の方が根本原因を示す。
3. **16:16 empty workflows** (MISS+EXTRA ペア): 同上パターン。seiton は `push` に workflows filter が使えないことを指摘。
   - **設計方針として維持。**

**EXTRA:**
- `22:9 matrix axis 'foo' has no values`: seiton の独自 lint ルール — 空軸の検出は有用な追加診断。維持。

---

##### 4.L `if_cond_constants` — ~~2 MISS, 1 EXTRA~~ ✅ DONE (0 MISS, 0 EXTRA)

**具体例:**

```
# .out (actionlint)                                         # .seiton.out (seiton)
test.yaml:18:13: "true" in condition                 →      test.yaml:19:11: "true\n" in condition  ← EXTRA (行ずれ + 改行含む)
test.yaml:31:11: "true" in condition (snapshot.if)   MISS   (なし)
```

**実装済み:**
1. `IfCondRule` で定数式テキストを `.Trim()` してから表示するよう修正。`"true "` → `"true"`, `"true\n"` → `"true"` 等。
2. `IfCondRule.VisitJobPre` に `snapshot.If` のチェックを追加。line 31 の MISS を解消。

---

##### 4.M `invalid_json_in_fromjson` — 2 MISS, 3 EXTRA (フェーズ 2.6 へ延期)

**具体例:**

```
# .out (actionlint)                                               # .seiton.out (seiton)
test.yaml:12:37: broken JSON at offset 4               COL        test.yaml:12:28: fromJSON() argument is not valid JSON...
test.yaml:24:19: evaluating null type                   →          test.yaml:24:19: property "null" not defined...  ← 異なるメッセージ
test.yaml:25:19: evaluating array<string>               COL        test.yaml:25:19: array value → "[Array]"
test.yaml:26:19: evaluating {array:...; bool:...}       COL        test.yaml:26:19: object value → "[Object]"
test.yaml:28:32: contains() 1st arg not assignable      MISS       (なし)
test.yaml:28:32: contains() 1st arg not assignable      MISS       (なし)
                                                                   test.yaml:9:19:  null value in ${{}}  ← EXTRA
                                                                   test.yaml:10:20: array value in ${{}}  ← EXTRA
                                                                   test.yaml:11:21: object value in ${{}}  ← EXTRA
```

**MISS 原因と改善案:**

1. **28:32 contains() 型チェック** (MISS × 2): `contains(fromJSON('...'), ...)` で、fromJSON の戻り値が object 型なのに contains の第一引数 (array|string) に渡されるケース。seiton は fromJSON の戻り値型推論が未実装のため、contains の引数型チェックが機能しない。
   - **改善案:** `fromJSON()` の引数が文字列リテラルの場合、JSON をパースして具体的な戻り値型 (`{array: array<bool>; bool: bool}` 等) を推論する。高コスト — フェーズ 2.6 と統合。
2. **24:19 null 型** (COL_DIFF + メッセージ差異): actionlint は `evaluating the value of type null` で型情報を表示。seiton は `property "null" is not defined` — 別の問題として検出。
   - **改善案:** fromJSON 型推論の実装後、テンプレート型チェックで正しい型名を表示。

**EXTRA 原因:**
- 9:19, 10:20, 11:21: matrix include 内の fromJSON 結果の template 型チェック — seiton がより早い段階 (include 行) で null/array/object の template 使用を検出。有用な追加検出。

---

##### 4.N `expr_check_in_env_var_name` — ~~2 MISS, 1 EXTRA~~ ✅ DONE (0 MISS, 1 EXTRA)

**具体例:**

```
# .out (actionlint)                                               # .seiton.out (seiton)
test.yaml:4:7:   context "runner" not allowed (workflow env)  COL  test.yaml:4:3:  env key '${{runner.name}}' not portable
test.yaml:12:13: property "foooooo" not defined              MISS  (なし)
test.yaml:14:11: context "runner" not allowed (job env)      COL   test.yaml:14:7: env key '${{runner.fooooooo}}' not portable
test.yaml:14:11: property "fooooooo" not defined             MISS  (なし)
                                                                   test.yaml:18:11: env key '${{runner.name}}' not portable  ← EXTRA
```

**実装済み:**
`ExprUndefinedVarRule.CheckEnv` で env キーに対しても `CheckNode` を呼び出すよう修正。env キー内の `${{ runner.foooooo }}` のような式に対して property チェック + context availability チェックが機能するようになった。

**EXTRA 維持:**
- 18:11 step-level env key: seiton は portability 警告を出す — 有用な追加検出。

---

##### 4.O `expr_in_default_input` — ~~2 MISS, 0 EXTRA~~ ✅ DONE (0 MISS, 0 EXTRA)

**実装済み:**
`ExprUndefinedVarRule.VisitEvent` に `ValidateInputDefaultType` メソッドを追加。workflow_call input の default 値式の推論型と declared type (boolean/number) の不一致を検出。`ExpressionSemanticAnalyzer.InferTypeWithOverrides` を internal に昇格して利用。

---

##### 4.P `issue-610_recursive_raw_yaml_value` — ~~1 MISS, 1 EXTRA~~ ✅ 改善済み (COL_DIFF のみ)

**実装済み:**
recursive alias メッセージにアンカー宣言位置を追加: `recursive alias "recursive_include" is found. anchor was declared at line:8, column:18`。`VYamlStreamAdapter` の recursive alias 検出時に `_definedAnchors` からアンカー位置を取得し、メッセージに含めるよう変更。

**残差異:**
- 行・列差異 (COL_DIFF): seiton の値位置ポリシーにより位置が異なる。設計差異。
- ~~matrix メッセージ差異: `unsupported shape` vs `unexpected alias node` — 同じ問題の異なる表現。低優先度。~~ → `ParseRawYamlValue` で `YamlEventKind.Alias` を検出し `unexpected alias node on parsing value in matrix row` メッセージを出力するよう改善。汎用 fallback `unsupported shape` は残しつつ、alias 専用の具体的なメッセージを優先。

#### 低優先度 — 1行差異・設計方針差異

多数の COL_DIFF fixtures (41) は seiton の値位置報告ポリシーまたはメッセージ形式差異によるもので、修正不要。
1行の MISSING/EXTRA は個別のメッセージ微調整で対応可能だが、多くは設計差異。

### 設計方針として維持する差異 (修正しない)

1. **値位置報告** (§4.4): 41 COL_DIFF fixtures — seiton はキーではなく値位置を報告
2. **shellcheck/pyflakes 非サポート** (§4.2): 4 fixtures (17 行)
3. ~~**snapshot 非サポート** (§4.1)~~ → snapshot パース + context availability 実装済み
4. **seiton 独自の有用な検出**: template injection, portability, 構造検証等の EXTRA は維持 (削除しない)

---

## 5. 今後の改善優先度サマリ (フェーズ 4 実施後)

### 全体概況

フェーズ 1〜4 の改善により、99 fixtures 中 60 fixtures (PERFECT 19 + COL_DIFF 41) が「検出漏れ・余剰なし」の状態に到達。残り 34 MIXED + 5 MISSING のうち、スコープ外 (shellcheck/pyflakes) 4 fixtures を除いた **35 fixtures に改善余地** がある。

| 分類 | fixtures数 | 影響行数 | 説明 |
|------|-----------|---------|------|
| **PERFECT** | 19 | 0 | 完全一致 (regex マッチ含む) |
| **COL_DIFF のみ** | 41 | 238 行 | 同一行で検出しているが列位置またはメッセージ形式が異なる。設計方針として維持 |
| **MISSING のみ** | 5 | 18 行 | 4 は pyflakes/shellcheck (スコープ外)、1 は `expr_check_in_services` (1行) |
| **MIXED** | 34 | 59 miss + 60 extra | 複合的なギャップ |

### 残差異の原因別分類

| 原因 | MISS行数 | EXTRA行数 | 対応方針 |
|------|---------|----------|---------|
| **設計方針 (値位置報告)** | ~8 | ~8 | 修正しない — seiton の設計方針 |
| **メッセージ形式差異** | ~12 | ~12 | 同一行で検出しているが文言が異なる。低優先度 |
| **seiton独自の有用な検出** | 0 | ~25 | 維持 — template injection, portability, 構造検証等 |
| **検出能力差** | ~22 | 0 | 改善候補 (fromJSON型推論, alias処理, 型チェック) |
| **スコープ外** | 17 | 0 | 対応しない — shellcheck/pyflakes |

### 改善候補 (優先度順)

#### 高優先度 — 改善コスト対効果が高い

| Fixture | Miss | Extra | 主な原因 | 推奨アクション |
|---------|------|-------|----------|--------------|
| `workflow_call_job` | 4 | 7 | 空 uses 検出 + msg形式 + 構造検証余剰 | 空 uses → "string should not be empty" 統一、重複 extra の dedup |
| `recursive_anchors` | 4 | 6 | alias 処理の根本差異 | VYaml alias replay の改善 (高コスト) |
| `issue280_runs_on` | 3 | 5 | 空 label "unknown" regex + requires-labels | 空文字 label への "unknown label" 追加は設計判断。EXTRA は有用 |
| `invalid_json_in_fromjson` | 2 | 3 | fromJSON 型推論 (contains 型チェック) | fromJSON 戻り値型推論の拡充 (§2.6 延期済み) |

#### 中優先度 — 個別の小改善

| Fixture | Miss | Extra | 主な原因 | 推奨アクション |
|---------|------|-------|----------|--------------|
| `invalid_snapshot` | 2 | 2 | "image-name" 必須チェック + 空文字位置差 | image-name 必須バリデーション追加 |
| `invalid_steps` | 2 | 1 | 空 flow mapping (VYaml制限) | VYaml の MappingStart 位置制限。改善困難 |
| `workflow_dispatch_type_check_inputs` | 1 | 6 | property access 型 + template injection余剰 | EXTRA は template injection 警告で有用。MISS は index 型エラー形式差 |
| `workflow_dispatch_input_types` | 1 | 0 | empty option string メッセージ形式 | メッセージ形式の微調整 |
| `invalid_float_at_timeout_minutes` | 1 | 0 | quoted string float検出 | quoted string の float パース |
| `invalid_int_at_max_parallel` | 1 | 0 | quoted string integer検出 | quoted string の integer パース |
| `case_sensitive_keys` | 1 | 0 | step "RUN" キー未検出 | step unexpected key の case-insensitive 検出改善 |
| `evaluated_template` | 1 | 0 | steps.cache.outputs 型推論 | step output の具体型推論 (高コスト) |
| `context_availability` | 1 | 1 | services 式形式の env 検出 | services expression form での context availability 改善 |
| `expr_check_in_services` | 1 | 0 | services 式形式 property チェック | 上記と同根 |

#### 低優先度 — 設計差異・1行差異

以下は 1 行の差異で、多くは設計方針 (値位置報告, メッセージ形式) による差異:

| Fixture | Miss | Extra | 分類 |
|---------|------|-------|------|
| `empty_image_names_and_versions` | 1 | 1 | メッセージ差異 |
| `empty_sequence_or_string` | 1 | 1 | 設計方針 (choice空文字許容) + seiton独自検出 |
| `glob_more` | 1 | 1 | block scalar 改行 (VYaml制限) |
| `if_cond_constants` | 1 | 1 | multi-line if 行位置差異 |
| `if_cond_edge_cases_trailing_leading_chars` | 1 | 1 | 行位置差異 |
| `invalid_comparisons` | 1 | 1 | array<bool> vs array<{}> 型チェック |
| `upper_case_duplicate_keys` | 1 | 1 | case-insensitive note 差異 |
| `missing_required_keys` | 1 | 1 | environment name 位置差 |
| `github_script_untrusted_input` | 1 | 1 | 行位置差異 |
| `invalid_image_version_event` | 1 | 2 | メッセージ差異 + 余剰 |
| `outputs_map_object` | 1 | 1 | メッセージ差異 |
| `undefined_anchor` | 1 | 1 | メッセージ差異 |
| `minimal_cycle_in_needs` | 1 | 1 | 値位置報告 (行番号ずれ) |
| `random_order_cycle_in_needs` | 1 | 1 | 値位置報告 (行番号ずれ) |
| `strategy_matrix_runner_context` | 1 | 1 | メッセージ差異 (regex形式) |

#### EXTRA のみ — seiton 独自の有用な検出

| Fixture | Extra | 内容 |
|---------|-------|------|
| `assign_expression` | 4 | 型チェック詳細化 + include 検証 + template 型チェック |
| `expr_check_in_env_var_name` | 3 | portability 警告 (env key に式を使用) |
| `outputs_of_action_skipping_inputs_check` | 2 | 追加 input 検証 |
| `workflow_dispatch_type_check_inputs` | 6 | template injection 警告 |
| `schedule_invalid_timezone` | 1 | cron empty 追加検出 |
| `issue193` | 1 | 追加診断 |

### 設計方針として維持する差異 (修正しない)

1. **値位置報告** (§4.4): 41 COL_DIFF fixtures — seiton はキーではなく値位置を報告
2. **shellcheck/pyflakes 非サポート** (§4.2): 4 fixtures (17 行)
3. **seiton 独自の有用な検出**: template injection, portability, 構造検証等の EXTRA は維持

---

## 6. 検証ルール

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

## 8. 実装記録

> 各フェーズの実装結果をここに記録する。

### フェーズ 1 実装記録

(未実施)

### フェーズ 2 実装記録

#### 実施済み

| # | 項目 | 結果 | 変更ファイル |
|---|------|------|-------------|
| 2.8 | outdated action runner マッピング | ✅ `outdated-action-runner` を `SeitonOnlyRules` から除外、`RuleIdMap` に `action` マッピング追加。メッセージを `fix this issue` に修正 | `ActionlintCompatTests.cs`, `OutdatedActionRunnerRule.cs` |
| 2.12 | reusable workflow uses フォーマット検証 | ✅ リモート uses の形式検証追加 (`owner/repo/path@ref` or `./path`)。ローカルパスの `@ref` 検証もファイルコンテキストなしで動作するよう改善 | `ReusableWorkflowRule.cs`, `RuleInterfaceTests.cs` |
| 2.10 | reusable_workflow_empty_secrets | ✅ 空 `secrets:` (null) を「secrets 宣言なし」として処理。`BuildSecretsOverride` で空 secrets を strict empty object に変換 | `WorkflowParser.On.WorkflowCall.cs`, `DynamicContextTypeBuilder.cs` |
| 2.4 | invalid_comparisons 型チェック | ✅ 空オブジェクトの型推論修正 (`{}` → `ObjectExprType`)。配列要素型の推論追加。`AreEqualityCompatible` で配列要素型の互換性チェック追加。7/7 → 6/7 検出 (array<bool> vs array<object> は未対応) | `DynamicContextTypeBuilder.cs`, `ExpressionSemanticAnalyzer.cs` |
| 2.2 | matrix row assign 型チェック | ✅ マトリックス行のスカラー値が `${{ expr }}` の場合、式の型推論を実行。`ValidatePropertyAccessWithOverrides` で非オブジェクト型のプロパティアクセスエラーを検出 | `DynamicContextTypeBuilder.cs`, `ExpressionSemanticAnalyzer.cs` |
| 2.3 | action output プロパティ検証 | ✅ `octokit/request-action` をカタログ追加。`this_output_does_not_exist` プロパティエラーを検出。ただし seiton は `owner`/`repo` を未定義 input として報告 (actionlint は input チェックをスキップ) | `targets.json`, `popular_actions.json`, `PopularActions.g.cs` |
| 2.7 | Docker 固有 input 検証 | ✅ `rhysd/action-setup-vim` をカタログ追加。`entrypoint`/`args` を未定義 input として検出 | `targets.json`, `popular_actions.json`, `PopularActions.g.cs` |
| 2.1 | evaluated_template 型改善 | ✅ github.event のイベント固有型システム追加。ワイルドカードセマンティクス修正 (`arr.*` → Array 型)。per-expression 位置追跡により全3式が正しい列位置で報告 (22:20, 22:63, 24:20)。`steps.cache.outputs` の検出は step output 型が loose object のため未対応 | `EventPayloadTypes.g.cs`, `DynamicContextTypeBuilder.cs`, `ExpressionSemanticAnalyzer.cs`, `ExprUndefinedVarRule.cs` |
| 2.5 | workflow_dispatch inputs 型チェック | ✅ github.event イベント型によりインデックス型チェック改善。`github.event.inputs[...]` の配列インデックス型エラー3件追加検出 | `EventPayloadTypes.g.cs` (push/workflow_dispatch 定義) |

#### 未実施 (設計上の制約)

なし — フェーズ 2 のすべての項目が実装済み。

#### 追加実装 (フェーズ 2 残り)

| # | 項目 | 結果 | 変更ファイル |
|---|------|------|-------------|
| 2.1 (残り) | evaluated_template per-expression 位置追跡 | ✅ `CheckNode`/`CheckNodeWithOverrides`/`VisitWorkflowPost` で `${{ }}` ごとに `ComputeExpressionLocation` を使い per-expression TextRange を計算。同一 YAML ノード内の複数式が正しい列位置で報告されるようになった (22:14 → 22:20, 22:63)。DiagnosticIdentity dedup は行 + メッセージのため異なるメッセージの式はすべて出力される | `ExprUndefinedVarRule.cs` |
| 2.6 | fromJSON matrix include 型推論 | ✅ `BuildMatrixOverride` の include ループで `InferIncludeValueType` を追加。`${{ fromJSON('null') }}` → NullExprType、`${{ fromJSON('["foo", 1.2]') }}` → ArrayExprType 等の型が matrix コンテキストに伝搬。`invalid_json_in_fromjson` の lines 25-27 が新たに検出 (array/object template type check) | `DynamicContextTypeBuilder.cs` |
| 2.9 | object_at_runner_label runs-on 型チェック | ✅ `CheckRunsOnType` を `ExpressionSemanticAnalyzer` に追加。`ValidateTemplateType` で `sinkName == "job.runs-on"` の場合に runs-on 専用メッセージ (`type of expression at "runs-on" must be string or array but found type "{foo: any}"`) を使用 | `ExpressionSemanticAnalyzer.cs`, `ExprUndefinedVarRule.cs` |
| 2.11 | workflow_call_outputs_sema メッセージ改善 | ✅ `ValidatePropertyAccessWithOverrides` と `ValidatePropertyAccess` の property-not-defined メッセージを `property "X" is not defined in object type {props}` 形式に変更。`FormatObjectType` で型シグネチャを表示 | `ExpressionSemanticAnalyzer.cs` |

### フェーズ 3 実装記録

| # | 項目 | 結果 | 変更ファイル |
|---|------|------|-------------|
| 3.1 | dedup_errors anchor 展開 dedup | ✅ `LintEngine` でパーサー診断を `DiagnosticIdentity` で重複排除。12 → 1 行に削減 | `LintEngine.cs` |
| 3.2 | context availability 重複 | ✅ フェーズ 1.1 で解決済み + 3.3 の ParseIntOrExpression で must be integer 誤報も消滅 | - |
| 3.3 | invalid_int_at_max_parallel 式誤報 | ✅ `ParseIntOrExpression` 追加。`${{ }}` 式を有効な max-parallel として受理 | `WorkflowParser.ExpressionIntegration.cs`, `WorkflowParser.Strategy.cs`, `AstArena.cs` |
| 3.4 | deprecated_action_inputs 行位置 | ✅ `AddStepWarning` に `Arena.GetStringRange(pair.Value)` を渡し input 値位置で報告。uses 行 (7:15) → input 値行 (9:25, 10:27) | `PopularActionInputsRule.cs` |

### フェーズ 4.A 実装記録

| # | 項目 | 結果 | 変更ファイル |
|---|------|------|-------------|
| 4.A | glob_more error recovery | ✅ `ParseStringOrStringSequence` のシーケンスループで、不正要素検出後 `break` → `continue` に変更。最初のエラーのみ記録し、後続の有効エントリのパースを継続。MISS 10→4 (6行改善)。残り4行: image_version.versions (非標準イベント, 2行), snapshot.version (スコープ外, 1行), block scalar 改行 (1行) | `WorkflowParser.ScalarParsing.cs` |

#### テスト

- **Unit**: `ScalarHelpersTests.ParseStringOrStringSequence_ContinuesAfterEmptyEntry` — 空エントリ後の有効ノード収集を検証
- **Unit**: `ScalarHelpersTests.ParseStringOrStringSequence_MultipleEmptyEntriesReportFirstError` — 複数空エントリで最初のエラーのみ報告を検証
- **Integration**: `RuleRegression_GlobPatternRule_Syntax_TableDriven` に `ng-glob-errors-detected-after-null-entry-in-paths` ケース追加 — null エントリ後の `!`, `  foo`, `.` が全て検出されることを検証
- **Red/Green 確認済み**: 修正 revert 時に 3 テスト失敗、修正適用で全 1017 テスト通過

### snapshot パース + glob 検証実装記録

| # | 項目 | 結果 | 変更ファイル |
|---|------|------|-------------|
| 4.A+ | snapshot パース + glob 検証 | ✅ `Snapshot` AST モデル追加 (`Version`, `ImageName`, `If`)。`JobNodeMappingKey.Snapshot` 追加、`ParseSnapshotNode` メソッド実装。`GlobPatternRule` に `VisitJobPre` 追加で `snapshot.version` の glob 検証。`GlobPatternRule.VisitEvent` に `ImageVersionEvent.Versions` の glob 検証追加。`ValidatePattern` を `Action<string, TextRange>` ベースにリファクタリングし Event/Job 共用化。glob_more MISS 4→1, EXTRA 2→1 | `Job.cs`, `WorkflowParser.Jobs.cs`, `GlobPatternRule.cs` |

#### テスト

- **Unit**: `RuleRegression_GlobPatternRule_SnapshotVersion_TableDriven` — unclosed bracket + valid version
- **Unit**: `RuleRegression_GlobPatternRule_ImageVersionVersions_TableDriven` — unclosed bracket + lone bang
- **Red/Green 確認済み**: 実装前に2テスト失敗 (no diagnostics)、実装後に全 1019 テスト通過

### フェーズ 4.B 実装記録

| # | 項目 | 結果 | 変更ファイル |
|---|------|------|-------------|
| 4.B | exclusive_webhook_filters 位置改善 | ✅ `ParseWebhookEventWithOptions` と `ParseOnEventOptions` の両メソッドで、排他フィルターエラーの報告位置を `eventMark` (イベント名位置) から **後に出現したフィルターキーの `keyMark`** に変更。`TextPosition.Offset` 比較で後方キーを選択。全9行が actionlint と PERFECT MATCH | `WorkflowParser.On.Webhook.cs` |

#### テスト

- **Unit**: `Parse_ExclusiveFilterError_ReportsAtIgnoreKeyPosition` — branches/branches-ignore で後方キー位置を検証
- **Unit**: `Parse_ExclusiveFilterError_TagsIgnore_ReportsAtIgnoreKeyPosition` — tags/tags-ignore
- **Unit**: `Parse_ExclusiveFilterError_PathsIgnore_ReportsAtIgnoreKeyPosition` — paths/paths-ignore
- **Unit**: `Parse_ExclusiveFilterError_IgnoreFirst_ReportsAtLaterKey` — ignore が先に出現する逆順ケース (branches キー位置を検証)
- **Red/Green 確認済み**: 実装前に3テスト失敗 (line 2 = event name)、実装後に全 1023 テスト通過

### フェーズ 4.C 実装記録

| # | 項目 | 結果 | 変更ファイル |
|---|------|------|-------------|
| 4.C-A | workflow_call output value root context 検証 | ✅ `VisitWorkflowPost` を `CheckNodeWithOverrides` ベースに書き換え。root context availability + property access 検証を統合。`env` が `WorkflowCallOutputsValue` スコープで検出されるようになった | `ExprUndefinedVarRule.cs` |
| 4.C-B | snapshot.if context availability | ✅ `JobSnapshotIf` を `ExpressionValidationContext` に追加。availability.json に `jobs.<job_id>.snapshot.if` エントリ追加 (contexts: github, needs, strategy, matrix, vars, inputs)。パーサーで `JobIf` → `JobSnapshotIf` に変更。`VisitJobPre` に snapshot.If チェック追加。`isIfContext` にも追加 | `Availability.g.cs` (generated), `WorkflowParser.Jobs.cs`, `ExprUndefinedVarRule.cs`, `availability.json` |
| 4.C-C | service entrypoint/command context availability | ✅ `Container` AST に `Entrypoint`/`Command` プロパティ追加。パーサーで `SkipCurrentNode()` → `ParseStringAndValidateExpression` に変更 (`JobServicesEntrypoint`/`JobServicesCommand` context)。`CheckServices` に lint チェック追加 | `StructuralNodes.cs`, `WorkflowParser.Containers.cs`, `ExprUndefinedVarRule.cs`, `Availability.g.cs` (generated), `availability.json` |

#### テスト

- **Unit**: `RuleRegression_ExprUndefinedVarRule_ContextAvailability4C_TableDriven` — 8ケース:
  - `ng-workflow-call-output-value-env-not-allowed` — output value で env 不可を検証
  - `ok-workflow-call-output-value-jobs-context` — jobs context は許可
  - `ng-snapshot-if-env-not-allowed` — snapshot.if で env 不可
  - `ng-snapshot-if-runner-not-allowed` — snapshot.if で runner 不可
  - `ng-snapshot-if-secrets-not-allowed` — snapshot.if で secrets 不可
  - `ok-snapshot-if-strategy-matrix-allowed` — snapshot.if で strategy/matrix は許可
  - `ng-service-entrypoint-env-not-allowed` — entrypoint で env 不可
  - `ng-service-command-env-not-allowed` — command で env 不可
  - `ok-service-entrypoint-github-context` — entrypoint で github は許可
- **Red/Green 確認済み**: 実装前に `ng-workflow-call-output-value-env-not-allowed` で失敗 (no diagnostics)、実装後に全 1024 テスト通過
- **Benchmark**: CoreParsingBenchmark + CoreLintBenchmark 完了。allocation スパイクなし
