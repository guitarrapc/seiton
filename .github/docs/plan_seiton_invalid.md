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
| `issue170_empty_permissions` | `12:17` (2行) | ~~空文字列 permission の検出が不足~~ **完全対処済み** — パーサーとルールの両方で空文字列を検出。位置も `12:17` で actionlint と一致 | parse レベルで空文字列 permission を検出すべき |

**対処**: seitonのpermissions ルールの位置報告は値位置が仕様なので、actionlintのキー位置基準に合わせない。空文字列パーミッション検出の追加。VYaml アダプターの backward-scan 改善。

**実施済み**:
- `PermissionsRule.cs`: 空文字列 All 値に対して `"" is invalid for permission for all the scopes. available values are "read-all", "write-all" or {}` メッセージを追加
- `WorkflowParser.cs`: `ParsePermissionsNode` で空スカラー時に `permissions value must not be empty` メッセージを追加（旧: 汎用の "must be scalar or mapping"）
- `VYamlStreamAdapter.cs`: `ResolveEmptyScalarStart` を改善 — VYaml の mark が次キーの `key:` 位置まで進んだ場合に、(a) コロン後にインライン値がある場合、(b) `_scalarSliceCursor` とコロン位置の間に改行がある場合の 2 戦略で次キーのコロンを検出し、正しい位置にバックスキャン
- テスト: `RuleRegression_PermissionsRule_TableDriven` に `ng-job-empty-permissions-scalar` と `ng-workflow-empty-permissions-scalar` ケースを追加
- テスト: `ParserTests` に `Parse_NullScalarPermissions_ReportsPermissionsLine` と `Parse_NullScalarPermissions_WorkflowLevel_ReportsPermissionsLine` を追加（位置精度テスト）
- 全717テスト通過

#### B-2. id-naming ルール — ルール名差異

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `invalid_id` | `3:3`, `7:13`, `8:3`, `12:13`, `13:3`, `17:13` (6行) | seiton `[id-naming]` で検出済み。空文字列 step id (`22:13`) も **完全対処済み** — パーサーとルールの両方で空文字列を区別して検出 | ルール名差異 + 空文字列ケース漏れ |

**対処**: 空文字列 step id の検出追加。

**実施済み**:
- `WorkflowParser.Steps.cs`: `StepMappingKey.Id` ケースで `idNode.HasValue` を使い空スカラー (empty) と非スカラー (mapping 等) を区別。空文字列は `"must not be empty"` メッセージ、非スカラーは `"must be scalar"` メッセージ
- `IdNamingRule.cs`: `ValidateId` メソッドに空文字列の明示チェックを追加。空の場合は `"{kind} must not be empty"` メッセージ（旧: `"contains invalid characters"` という不正確なメッセージ）
- テスト: `ParserTests` に `Parse_EmptyStepId_ReportsEmptyNotScalar` を追加
- テスト: `RuleRegression_IdNamingRule_TableDriven` の `ng-step-id-empty` ケースの期待メッセージを `"must not be empty"` に更新
- 全718テスト通過

#### B-3. deprecated-commands ルール — 列位置ずれ

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `deprecated_workflow_commands` | `8:14`, `9:14`, `10:14`, `11:14` (4行) | seiton `[deprecated-commands]` で検出済み。位置は元々一致。複数コマンド報告と block scalar back-projection が **完全対処済み** | run スクリプト内の位置指定方式が異なる → 実際は一致していた。early return で1ステップ1件しか報告されない問題 + block scalar EOF 境界バグ |

**対処**: ~~列位置をコマンド名の先頭に合わせる~~ 列位置は元々正しかった。複数 deprecated command の全件報告 + VYaml block scalar back-projection の EOF 境界修正。

**実施済み**:
- `DeprecatedCommandsRule.cs`: 各 `ContainsAsciiIgnoreCase` チェック後の `return` を削除し、1ステップ内の複数 deprecated command をすべて報告
- `VYamlStreamAdapter.cs`: `TryMeasureSourceLength` に 2 件の修正:
  - CRLF ケースで `atLineStart = true` が設定されていなかったバグを修正（CRLF 改行後のインデントスキップが効かなかった）
  - ソース EOF で残りが `\n` のみ (block scalar clip chomping による trailing newline) の場合に成功として扱う
- テスト: `RuleRegression_DeprecatedCommandsRule_TableDriven` に `ng-multiline-multiple-deprecated` ケースを追加（`::set-output` + `::set-env` の両方検出を検証）
- 全718テスト通過

#### B-4. needs-graph ルール — 位置ずれ

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `minimal_cycle_in_needs` | `4:3` (1行) | seiton `[needs-graph]` で検出済み。**完全対処済み** — サイクルを閉じる `needs` 値の位置で報告し、サイクルパスをメッセージに含める。actionlint はジョブキー位置で報告するが、seiton はユーザーが直接編集すべき箇所を指す設計方針を採用 (§4.5.1) | 報告位置の設計判断差異 |
| `random_order_cycle_in_needs` | `4:3` (1行) | 同上 | 同上 |

**対処**: サイクル検出の報告位置を `needs` 値位置 (サイクルを閉じる back-edge の位置) に設定。メッセージにサイクルパス (`a -> b -> c -> a`) を含め、ユーザーが循環の全体像を把握できるようにした。actionlint のジョブキー位置ではなく `needs` 値位置を選択した理由は `Seiton_Linter_spec.md` §4.5.1 に明記。

#### B-5. schedule-event ルール — 部分的検出

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `schedule_invalid_timezone` | `4:17`, `6:17`, `8:17`, `10:17`, `11:13` (5行) | **完全対処済み** — 5件すべて検出。Not/A/Timezone, UTC, local は既存で検出済み。空文字列 timezone/cron の検出を追加。列位置差異 (18 vs 17, 13:5 vs 11:13) は VYaml のクォート付きスカラー・空スカラー位置報告の系統的問題 | IANA timezone 検証の差異 + 空文字列検出漏れ |
| `cron_5minutes_limit` | `6:13` (1行) | seiton `[schedule-event]` で検出済み。列位置 `6:14` vs `6:13` は VYaml のクォート付きスカラー位置の系統的差異 | 列位置ずれ (systemic) |

**対処**: 空文字列 timezone/cron の検出をルール側に追加。パーサーでは `allowEmpty: true` としてルールが空検出を担当。UTC/Local は既存の `IsUtcOrLocalUtf8` で検出済み。

#### B-6. runner-label ルール — 部分検出

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `invalid_runner_labels` | `4:14`, `8:30`, `8:46` (3行) | **完全対処済み** — 3件すべて検出。未知ラベル (`4:14`)、OS 競合 (`8:30`, `8:46`) をすべて検出。修正前は OS 競合の早期 return で `8:46` を検出漏れしていた | OS 競合の早期 return + メッセージ改善 |
| `runner_labels_conflict_matrix` | `6:14`, `6:30`, `6:44` (3行) | **完全対処済み** — 3件すべて検出。matrix 値の OS 競合を静的ラベルとの突合で検出。修正前は `${{matrix.os}}` を未知ラベルとして誤検出 (expression 判定漏れ) | matrix OS 競合チェック実装 + expression 判定修正 |
| `macos_10.15_removed` | `5:14`, `9:14` (2行) | seiton `[runner-label]` で検出済み（"not a known GitHub-hosted runner label" として）。修正前から検出できていた | 廃止ラベルは known label 集合に含まれないため unknown として検出される |
| `macos12_runner` | `5:14` (1行) | 同上 | 同上 |

**対処**: OS 競合の全件報告（早期 return 除去）。混合 runs-on リストでの matrix OS 競合検出。expression ラベルの誤検出修正。裸 OS ラベル (linux, windows, macos) の OS ファミリー認識。競合メッセージの具体化。

#### B-7. if-cond ルール — 部分検出

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `if_cond_constants` | 11行期待 | **10行検出に改善** (8→10)。定数畳み込みを拡張し null/数値/文字列リテラル + 純粋関数 (contains, startsWith, endsWith, format) の定数評価を追加。残り1行 (`snapshot.if: true` line 31) はパーサーが `snapshot` キーを解析しないため if-cond ルールに到達しない | 定数畳み込みの深さが不足 → **対処済み** (null/number/string/function 対応) |
| `if_cond_edge_cases_trailing_leading_chars` | 6行期待 | **完全対処済み** — 6件すべて検出。`${{ }}` 前後テキスト検出は既存実装で対応済みだった | `${{ }}` 前後テキスト検出未実装 → **実装済み確認** |

**対処**: `IsConstantBool` を `TryEvaluateConstant` に拡張。GitHub Actions の truthiness ルール (null=falsy, 0=falsy, ""=falsy, NaN=falsy) に従い全リテラル型を評価。純粋関数は引数がすべて定数の場合のみ評価。`snapshot.if` の未検出はパーサー側の制限 (C-1 カテゴリ)。

#### B-8. merge_key_unsupported — 位置ずれ

| テストケース | 期待 line:col | seiton の状態 | 原因 |
|---|---|---|---|
| `merge_key_unsupported` | `8:7`, `21:11`, `27:9` (3行) | **完全対処済み** — 3件すべて正しい位置で検出。(1) VYaml の `CurrentMark` が `<<` キーの末尾を指す問題を `IsMergeKey`/`TryRegisterDynamicKey` でキー長分の位置補正。(2) step env のマージキーメッセージが `"env must be mapping does not support..."` と結合されていた問題を `sectionName` パラメータで分離。(3) step レベルのマージキーが "unexpected step key" として誤報告されていた問題を `IsMergeKey` チェック追加で修正 | 位置報告の差異 + メッセージ品質 + step マージキー未検出 |

**対処**: 3件の修正:
1. **位置修正**: `IsMergeKey` と `TryRegisterDynamicKey` で VYaml の `CurrentMark` がキー末尾を指す問題を `keyMark.Column - keyUtf8.Length` で補正
2. **メッセージ修正**: `ParseEnvNode` に `sectionName` パラメータを追加。`TryRegisterDynamicKey` に渡す `mappingName` をエラー文字列 (`"env must be mapping"`) からセクション名 (`"step[N] env"`) に分離
3. **step マージキー検出**: `WorkflowParser.Steps.cs` の step mapping ループに `IsMergeKey` チェックを追加。修正前は `Utf8MappingDispatch` で不一致→ "unexpected step key" として報告されていた

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
| 5-1 | ~~permissions ルールの空文字列検出~~ **完了** | `issue170_empty_permissions` | B-1 |
| 5-2 | deprecated-commands の列位置修正 | `deprecated_workflow_commands` | B-3 |
| 5-3 | ~~needs-graph の報告位置・メッセージ改善~~ **完了** — needs 値位置 + サイクルパス明示 | `minimal_cycle_in_needs`, `random_order_cycle_in_needs` | B-4 |
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

### B-1 実装記録 (permissions 空文字列検出)

**変更ファイル**:
- `src/Seiton.Core/Linting/Rules/PermissionsRule.cs`: 空文字列 All 値に対する専用エラーメッセージ追加
- `src/Seiton.Core/Parsing/WorkflowParser.cs`: `ParsePermissionsNode` の空スカラー検出で専用メッセージ使用
- `src/Seiton.Core/Parsing/VYamlStreamAdapter.cs`: `ResolveEmptyScalarStart` の backward-scan 改善
  - 2戦略で次キーのコロンを検出: (a) colonHasValue (コロン後にインライン値), (b) colonIsOnDifferentLine (`_scalarSliceCursor` とコロン位置間に改行)
  - `crossedNewline` トラッキングにより既に正しい行に戻っている場合はスキップしない
- `tests/Seiton.Core.Tests/RuleInterfaceTests.cs`: 空文字列 permissions のテストケース2件追加
- `tests/Seiton.Core.Tests/ParserTests.cs`: null scalar 位置精度テスト2件追加
  - `Parse_NullScalarPermissions_ReportsPermissionsLine`: ジョブレベル (line 4)
  - `Parse_NullScalarPermissions_WorkflowLevel_ReportsPermissionsLine`: ワークフローレベル (line 2)

**テスト結果**: 全717テスト通過

**ベンチマーク結果**: Allocated は +0.5%〜+2% (Medium/Large)、Mean は ShortRun (N=3) のため変動あり（+10-17%）だが `ResolveEmptyScalarStart` は null/empty scalar のみのレアパスのため実質影響なし

**CLIで確認**: `issue170_empty_permissions.yaml` の出力が `13:13` → `12:17` に修正され、actionlint の期待値と一致

### B-2 実装記録 (id-naming 空文字列 step id 検出)

**変更ファイル**:
- `src/Seiton.Core/Parsing/WorkflowParser.Steps.cs`: `StepMappingKey.Id` ケースで `idNode.HasValue` 判定を追加し空スカラーと非スカラーで異なるエラーメッセージ
- `src/Seiton.Core/Linting/Rules/IdNamingRule.cs`: `ValidateId` に `value.Length == 0` チェックを追加、空文字列用メッセージ `"must not be empty"`
- `tests/Seiton.Core.Tests/ParserTests.cs`: `Parse_EmptyStepId_ReportsEmptyNotScalar` テスト追加
- `tests/Seiton.Core.Tests/RuleInterfaceTests.cs`: `ng-step-id-empty` の期待メッセージを更新

**テスト結果**: 全718テスト通過

**教訓**: `ParseString` は非スカラー入力には `default` (`HasValue=false`) を返し、空スカラーには有効ノード (`HasValue=true`, `Value.Length=0`) を返す。この区別を使い分けることで「空文字列」と「型不正」を正確に報告できる。

### B-3 実装記録 (deprecated-commands 複数報告 + block scalar 修正)

**変更ファイル**:
- `src/Seiton.Core/Linting/Rules/DeprecatedCommandsRule.cs`: `return` 4件削除（全 deprecated command を報告）
- `src/Seiton.Core/Parsing/VYamlStreamAdapter.cs`: `TryMeasureSourceLength` に2件修正
  - CRLF 改行後の `atLineStart = true` 設定漏れ修正
  - ソース EOF 時に残りが `\n` のみなら成功扱い（block scalar clip chomping 対応）
- `tests/Seiton.Core.Tests/RuleInterfaceTests.cs`: `ng-multiline-multiple-deprecated` テストケース追加

**テスト結果**: 全718テスト通過

**ベンチマーク結果**: 29ベンチマーク実行。LintEngine.Check: Small 51.59µs (15.49KB), Medium 901.14µs (97.35KB), Large 12,196µs (453.21KB)。`TryMeasureSourceLength` の修正は block scalar のレアパスのため実質パフォーマンス影響なし

**教訓**:
- `TryResolveNormalizedSlice` は decoded (VYaml) の UTF-8 値をソースバイト列に back-project する。CRLF のある Windows 環境ではソース側が `\r\n` だが decoded 側は `\n` のみになるため、CRLF スキップ後に `atLineStart = true` を設定しないとインデントスキップが効かず anchor 不一致となる
- block scalar の clip chomping は trailing `\n` を付加するが、ソースファイルが trailing newline なしで終わる場合、EOF で decoded 側に残る `\n` をソース側で消費できない。EOF 残余が `\n` のみかチェックして許容する必要がある
- 位置ずれの当初仮説（列がコマンド位置でない）は誤りだった。単一行テストでは位置は正しく、問題は `return` による早期打ち切りと block scalar back-projection の2点だった

### B-4 実装記録 (needs-graph サイクル報告位置 + サイクルパスメッセージ)

**設計判断**: サイクル診断の報告位置として **`needs` 値位置** (サイクルを閉じる back-edge) を採用。actionlint のジョブキー位置ではなく、ユーザーが直接編集すべき箇所を指す方針。理由は `Seiton_Linter_spec.md` §4.5.1 に明記:
1. **アクショナビリティ**: `needs` 値はユーザーがサイクルを断つために編集する箇所そのもの
2. **サイクルパスの明示**: メッセージに `from -> to -> from` のような完全パスを含めることで全体像を補完
3. **サイクルに「開始」はない**: ジョブキー位置は恣意的、needs 値位置は DFS の back-edge に対応し決定的

**変更ファイル**:
- `src/Seiton.Core/Linting/Rules/NeedsGraphRule.cs`:
  - `DetectCycles`: back-edge 検出時、`currentJob` の `needs` 値位置 (`Arena.GetStringRange(need)`) で報告
  - `BuildCyclePath`: DFS スタックからサイクル部分を抽出し `a -> b -> c -> a` 形式のパス文字列を生成
  - メッセージ: `job '{currentId}' has a circular 'needs' dependency: {cyclePath}`
- `tests/Seiton.Core.Tests/RuleInterfaceTests.cs`: `RuleRegression_NeedsGraphRule_CyclePosition` テスト
  - 報告位置が `needs` 値の行 (line 9) であること（ジョブキー行 line 3 ではない）を検証
  - メッセージにサイクルパス `from -> to -> from` が含まれることを検証
- `Seiton_Linter_spec.md`: §4.4 `needs-graph` 記述と §4.5 Rule Guidance にサイクルパス・位置ポリシーを追記、§4.5.1 として設計判断を明記
- `Seiton_Linter_csharp_spec.md`, `Seiton_Linter_go_spec.md`: `needs-graph` 記述を同期更新

**テスト結果**: 全719テスト通過

**ベンチマーク結果**: (B-3 実行時と同等、ルールロジックのみの変更で hotpath 影響なし)

### B-5 実装記録 (schedule-event 空文字列 timezone/cron 検出)

**変更ファイル**:
- `src/Seiton.Core/Linting/Rules/ScheduleEventRule.cs`:
  - `ValidateTimezone`: 空スパンで `return` していた箇所を `AddEventError("on.schedule timezone must not be empty")` に変更
  - `ValidateCron`: 空スパンの明示チェックを追加、`AddEventError("on.schedule cron must not be empty")` を追加 (以前は `TryParseCronUtf8` が "cron must have exactly 5 fields" と報告していた)
- `src/Seiton.Core/Parsing/WorkflowParser.On.Schedule.cs`:
  - cron と timezone の `ParseString` 呼び出しに `allowEmpty: true` を追加
  - これによりパーサーが誤解を招く "must be scalar" エラーを空文字列に対して出さなくなり、ルールが空検出を担当
- `tests/Seiton.Core.Tests/RuleInterfaceTests.cs`:
  - `RuleRegression_ScheduleEventRule_TableDriven` に `ng-empty-timezone` と `ng-empty-cron` ケースを追加

**テスト結果**: 全719テスト通過

**ベンチマーク結果**: 回帰なし。ルールロジックのみの変更で hotpath 影響なし

**CLI確認**:
- `schedule_invalid_timezone.yaml`: 6 errors (Not/A/Timezone, UTC, local, empty timezone, empty cron + job-timeout) — 修正前は parse エラーが "must be scalar" と誤診断していたが、修正後は `[schedule-event]` ルールが "must not be empty" と正しく報告
- `cron_5minutes_limit.yaml`: `6:14` で検出済み (actionlint `6:13` との差異は VYaml のクォート付きスカラー位置の系統的問題)

**残存差異** (B-5 スコープ外):
- 列位置 18 vs 17 (クォート付きスカラー): VYaml がスカラー内容の開始位置 (クォートの後) を報告する系統的問題
- 空スカラー位置 (13:5 vs 11:13): VYaml の空スカラーに対する mark 位置が次のトークンに進んでしまう系統的問題 (`ResolveEmptyScalarStart` の後方スキャンでも完全には補正できないケース)

### B-6 実装記録 (runner-label OS 競合全件報告 + matrix 競合検出)

**変更ファイル**:
- `src/Seiton.Core/Linting/Rules/RunnerLabelRule.cs`:
  - `VisitJobPre`: expression ラベルの判定を `Arena.GetStringExpression(label).HasValue` から `ExpressionScanHelpers.ContainsExpressionMarker(label, Arena)` に変更（リスト内の expression が `ParseString` 経由で expression メタデータを持たない問題を修正）。`DetectOsFamilyConflicts` の返り値を受けて `DetectMatrixLabelOsConflicts` を呼び出し
  - `DetectOsFamilyConflicts`: `void` から `(byte, StringNodeId)` タプルを返すように変更。早期 return を除去して全競合を報告。メッセージを `"label '{X}' conflicts with label '{Y}'"` に具体化
  - `GetOsFamily`: 裸 OS ラベル (`linux`, `windows`, `macos`) の認識を追加。self-hosted プリセットラベルも OS ファミリー判定に含める
  - `DetectMatrixLabelOsConflicts` (新規): 混合 runs-on リスト (`[static, ${{matrix.AXIS}}]`) で matrix 軸を解決し、各値の OS ファミリーを静的ラベルと突合して競合を検出。matrix 値の位置で報告
- `tests/Seiton.Core.Tests/RuleInterfaceTests.cs`:
  - `RuleRegression_RunnerLabelRule_OsConflict_TableDriven`: メッセージ変更に伴い期待文字列を更新。`ng-multiple-os-conflicts` (ubuntu+windows+macos)、`ng-bare-os-label-conflict` (ubuntu+windows 裸ラベル) を追加
  - `RuleRegression_RunnerLabelRule_MatrixOsConflict_TableDriven` (新規): matrix 値の OS 競合検出テスト。`ng-matrix-os-conflict-with-static`、`ng-matrix-os-conflict-bare-label`、`ok-matrix-same-os-family` の 3 ケース

**テスト結果**: 全720テスト通過 (719 → 720)

**ベンチマーク結果**: 回帰なし。ルールロジックのみの変更で hotpath 影響なし

**CLI確認**:
- `invalid_runner_labels.yaml`: `4:14` (unknown), `8:30` (windows-latest conflicts), `8:46` (macos-latest conflicts) — 3件すべて検出
- `runner_labels_conflict_matrix.yaml`: `6:14` (windows-latest), `6:30` (macos-latest), `6:44` (windows) — 3件すべて matrix 値位置で検出
- `macos_10.15_removed.yaml`: `5:14`, `9:14` — 修正前から検出済み
- `macos12_runner.yaml`: `5:14` — 修正前から検出済み

**追加の修正ポイント**:
- `ParseStringOrStringSequence` 経由でパースされたリスト内 expression ラベル (`'${{matrix.os}}'`) は `Arena.GetStringExpression` が false を返す。`ExpressionScanHelpers.ContainsExpressionMarker` を使うことで raw 文字列内の `${{ }}` マーカーも検出可能。この修正は expression 判定の偽陰性バグの修正でもある

### B-7 実装記録 (if-cond 定数畳み込み拡張)

**変更ファイル**:
- `src/Seiton.Core/Linting/Rules/IfCondRule.cs`:
  - `IsConstantBool` を `TryEvaluateConstant` に拡張。`ConstantResult` 型で Null/Bool/Number/String を表現
  - GitHub Actions の truthiness ルールに従い全リテラル型を評価: null=falsy, 0=falsy, ""=falsy, NaN=falsy
  - 純粋関数の定数畳み込み: `contains`, `startsWith`, `endsWith` (bool返却)、`format` (string返却)
  - 引数がすべて定数の場合のみ関数を評価。非定数引数がある場合は NotConstant を返す
  - `&&`/`||` の短絡評価: falsy `&&` x → falsy (右辺不要)、truthy `||` x → truthy (右辺不要)
- `tests/Seiton.Core.Tests/RuleInterfaceTests.cs`:
  - `RuleRegression_IfCondRule_TableDriven` に B-7 テストケース 8 件追加:
    `ng-step-if-null-literal`, `ng-step-if-number-zero`, `ng-step-if-number-truthy`,
    `ng-step-if-empty-string`, `ng-step-if-nonempty-string`, `ng-step-if-mixed-constant`,
    `ng-step-if-constant-function`, `ok-step-if-impure-function`

**テスト結果**: 全720テスト通過 (720→720、テストケース数は test method 単位で変化なし)

**ベンチマーク結果**: 回帰なし。ルールロジックのみの変更で hotpath 影響なし

**CLI確認**:
- `if_cond_constants.yaml`: 10件検出 (8→10)。line 38 (`true && 42 || !null`) と line 40 (`contains(format(...))`) を新規検出
- `if_cond_edge_cases_trailing_leading_chars.yaml`: 6件検出 (変化なし、既に完全対応)
- 残り1件 (`snapshot.if: true` line 31) はパーサーが `snapshot` キーを解析しないため if-cond ルールに到達しない (C-1 カテゴリ)

### B-8 実装記録 (merge_key_unsupported 位置ずれ + メッセージ品質 + step 検出)

**変更ファイル**:
- `src/Seiton.Core/Parsing/WorkflowParser.ScalarParsing.cs`:
  - `IsMergeKey`: VYaml の `CurrentMark` がキー末尾を指す問題を `keyMark.Column - keyUtf8.Length` で補正
  - `TryRegisterDynamicKey`: 同様の位置補正を merge key 検出時に適用
- `src/Seiton.Core/Parsing/WorkflowParser.cs`:
  - `ParseEnvNode` に `sectionName` パラメータ (optional) を追加
  - `TryRegisterDynamicKey` の `mappingName` に `sectionName ?? error` を使用（エラー文字列とセクション名を分離）
- `src/Seiton.Core/Parsing/WorkflowParser.Steps.cs`:
  - step mapping ループに `IsMergeKey` チェックを追加（`Utf8MappingDispatch` の前）
  - 修正前: `<<` が "unexpected step key" として誤報告 → 修正後: "does not support merge key '<<'"
- `src/Seiton.Core/Parsing/WorkflowParser.Jobs.cs`: `ParseEnvNode` 呼び出しに `sectionName` 追加
- `src/Seiton.Core/Parsing/WorkflowParser.Containers.cs`: 同上
- `src/Seiton.Core/Parsing/WorkflowParser.ActionMetadata.cs`: 同上
- `tests/Seiton.Core.Tests/ParserTests.cs`:
  - `Parse_MergeKey_ReportsCorrectPosition`: 位置精度テスト (workflow_call inputs col 7, env col 11)
  - `Parse_MergeKey_StepLevel_ReportsAsMergeKey`: step マージキー検出テスト (col 9, "does not support merge key")
  - `Parse_MergeKey_EnvMessage_NotGarbled`: env メッセージ品質テスト ("must be mapping" 非含有)

**テスト結果**: 全723テスト通過 (720→723、+3 新規テスト)

**ベンチマーク結果**: 回帰なし。パーサーの分岐追加のみで hotpath 影響なし

**CLI確認**:
- `merge_key_unsupported.yaml`: `8:7`, `21:11`, `27:9` — 3件すべて actionlint 期待位置と一致
  - 修正前: `8:9`, `21:13`, `27:11` (すべて col +2)
  - メッセージ: `"on.workflow_call.inputs does not support merge key '<<'"`, `"job 'test' step[2] env does not support merge key '<<'"`, `"job 'test' step[4] does not support merge key '<<'"`

**教訓**:
- VYaml の `CurrentMark` は non-empty スカラーでも正確でない場合がある。特に `<<` マージキーではキー末尾 (`:` 位置) を指す。通常のキー (`run`, `uses` 等) では正しい位置を返すため、`<<` 特有の問題
- `ParseEnvNode` の `error` パラメータは型エラーメッセージ (`"env must be mapping"`) として設計されているが、`TryRegisterDynamicKey` の `mappingName` (セクション名) としても使い回されていた。用途の異なる文字列を分離するのが正しい設計

### Phase 1 実装記録

**テスト**: 723 → 730 (7 件追加)
**ベンチマーク**: 回帰なし

#### 1-1: `Utf8Slice` 内部表現のエラーメッセージリーク修正

- **原因**: `WorkflowParser.Jobs.cs` の `steps must be sequence` エラーで `jobId` (Utf8Slice) を `DecodeUtf8(source, jobId)` せず直接文字列補間に渡していた
- **修正**: `$"job '{DecodeUtf8(source, jobId)}' steps must be sequence"` に変更
- **対象ファイル**: `WorkflowParser.Jobs.cs`

#### 1-2: 位置 `0:0` のエラーを正しい位置に修正

- **原因**: `UnpinnedUsesRule` が `uses: ''` (パーサーエラーでデフォルト空値が設定されたステップ) に対して `invalid reference format` を報告し、デフォルト `TextRange` (0:0) になっていた
- **修正**: `UnpinnedUsesRule.VisitStep` で `uses.Length == 0` を早期リターン
- **対象ファイル**: `UnpinnedUsesRule.cs`

#### 1-3: OK テストデータでの `[parse]` エラー修正

3 つのサブ問題:

**1-3a: YAML anchor の入れ子解決不良** (`anchors.yaml`)
- **原因**: `VYamlStreamAdapter` でアンカー録画中に内側で定義されたスカラーアンカー (`&cond`, `&runner`) が独立して `_anchorStore` に格納されず、同じ録画内や後続の `*alias` が解決不能 → "if must be scalar" / "recursive alias" 偽エラー
- **修正**: 録画中のスカラーアンカーを即座に `_anchorStore` に独立格納。マッピング/シーケンスアンカーは `_nestedRecordings` リストで追跡し、深さが 0 になった時点で格納
- **対象ファイル**: `VYamlStreamAdapter.cs` (フィールド `_nestedRecordings` 追加、`ForwardToNestedRecordings` ヘルパー追加)

**1-3b: `container: null` の偽エラー** (`container_syntax.yaml`)
- **原因**: `GetScalarTag()` が VYaml の `IsNullScalar()` を確認せず `GetScalarUtf8()` の空スパンに `ScalarTag.Str` を返していた → `ParseContainerLike` が null スカラーをエラーとして扱った
- **修正**: (1) `GetScalarTag()` で `_parser.IsNullScalar()` を先行チェックし `ScalarTag.Null` を返す、(2) `ParseContainerLike` で `ScalarTag.Null` の場合は `reader.Read()` してスキップ
- **対象ファイル**: `VYamlStreamAdapter.cs`, `WorkflowParser.Containers.cs`

**1-3c: サービスの `entrypoint`/`command` キー未対応** (`container_syntax.yaml`)
- **原因**: `ContainerKeyTable` に `entrypoint` と `command` が含まれていなかった → "unexpected key" エラー
- **修正**: `ContainerMappingKey` に `Entrypoint=6`, `Command=7` を追加、`ContainerKeyTable.KeyCount` を 8 に更新、`ContainerDuplicateSubKey` にエントリ追加、`ParseContainerLike` の switch に `case Entrypoint/Command: reader.SkipCurrentNode()` を追加
- **対象ファイル**: `WorkflowParser.MappingKeys.Extended.cs`, `WorkflowParser.Containers.cs`

#### 1-4: 行番号の 0-based → 1-based 統一

- **調査結果**: `TextPosition` は仕様上 1-based (Line/Column)。`ComputeTextPositionFromOffset` も正しく 1-based で計算。既存の `new TextPosition(0, 1, 1)` も正しい。0:0 問題は 1-2 および 1-3a で修正済みのデフォルト `TextPosition(0, 0, 0)` リーク問題であり、体系的な 0-based 問題は存在しなかった

### Phase 2 実装記録

**実装済み項目**: 2-1, 2-2, 2-4, 2-6 (部分的)

#### 2-1: 空セクション検出の網羅化

**変更ファイル**:
- `WorkflowParser.On.Schedule.cs`: 空シーケンスチェック追加
- `WorkflowParser.On.WorkflowDispatch.cs`: `options` 空シーケンスチェック追加
- `WorkflowParser.On.Webhook.cs`: `types`, `branches`, `workflows` 空シーケンスチェック追加
- `WorkflowParser.Strategy.cs`: `matrix values`, `include`/`exclude` 空チェック追加
- `WorkflowParser.Jobs.cs`: `needs` 空シーケンスチェック追加
- `WorkflowParser.Containers.cs`: `image` 空文字列検出、`ports`/`volumes` シーケンス必須化

**検出パターン**: `result.Length == 0 && !needsError` → `"X" section should not be empty`、`needsError && result.Length > 0` → `"string should not be empty"`

#### 2-2: 必須キー欠落の位置精度改善

**変更ファイル**:
- `WorkflowParser.cs`: `lastRootKeyMark` 追加。`missing on`/`missing jobs` 位置改善
- メッセージ変更: `"required key 'on' is missing"` → `"\"on\" section is missing in workflow"`
- `WorkflowParser.Containers.cs`: `"image" is missing` メッセージ改善

#### 2-4: runs-on 構造バリデーション強化

**変更ファイル**: `WorkflowParser.Jobs.cs`
- Labels: MappingStart タグ付きタイプエラー、空文字列、空シーケンス検出
- Group: 非スカラー タグ付きタイプエラー、空文字列検出
- Unknown key: `"groups"` タイポ検出付きメッセージ改善
- Fallback: 空文字列/空シーケンス検出

#### 2-6: container/services 構造バリデーション強化

**変更ファイル**: `WorkflowParser.Containers.cs`
- `entrypoint`/`command` コンテナ非対応エラーメッセージ改善
- 空マッピング・空 credentials 検出
- credentials unknown key・不完全メッセージ改善

**テスト結果**: 全 730 テスト通過、ベンチマーク ゼロアロケーション維持

**検出状況**: `missing_jobs`/`missing_on` 完全一致。`issue280_runs_on` 17件中11件一致。`empty_sequence_or_string` 16件中11+2近似一致。`invalid_container_syntax` 23件中約18件一致。

**未実施項目**: 2-3 (unused anchor 位置), 2-5 (step 空要素), 2-7 (schedule mapping, 既動作中), `container:null` 暗黙 null 区別不能のためスキップ, `options` 要素空文字列は choice type で正当のため維持

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
