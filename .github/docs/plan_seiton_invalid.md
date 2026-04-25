# Seiton vs actionlint 検出ギャップ分析・対処計画

## 概要

actionlint の `testdata/examples/` にある 51 の YAML サンプルを seiton で検査し、以下を洗い出す。

1. **検出漏れ**: actionlint が検出しているのに seiton が検出していないパターン
2. **検出品質の問題**: seiton が検出しているがメッセージ・行列・内容に問題があるパターン
3. **対処方針と優先度**

## 検証手順（各フェーズ共通）

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

## 1. 検出漏れ一覧（actionlint が検出、seiton が未検出）

### 1-1. [P1: 高] Glob パターン: バックスラッシュエスケープ検証

- **対象ファイル**: `main.yaml` (line 5)
- **actionlint**: `character '\' is invalid for branch and tag names. only special characters [, ?, +, *, \, ! can be escaped with \` [glob]
- **seiton**: 未検出
- **原因分析**: `GlobPatternRule` がバックスラッシュ (`\`) のエスケープ対象文字の検証を行っていない。`^`, `[9-1]`, `./` などは検出しているが、`\` + 無効文字の組み合わせチェックが不足。
- **対処方針**: `GlobPatternRule` にバックスラッシュエスケープ検証ロジックを追加。GitHub Actions のフィルターパターン仕様では `\` でエスケープできる特殊文字は `[`, `?`, `+`, `*`, `\`, `!` のみ。それ以外の文字の前の `\` はエラー。
- **テストデータ**: `testdata/examples/main.yaml` line 5
- **実装結果**: ✅ Phase 2-a で実装済み。`GlobPatternRule.TryGetInvalidReason` にバックスラッシュエスケープ検証を追加。

### 1-2. [P1: 高] needs の重複 ID 検出（case-insensitive）

- **対象ファイル**: `invalid_ids_in_needs.yaml` (line 4)
- **actionlint**: `job ID "BAR" duplicates in "needs" section. note that job ID is case insensitive` [job-needs]
- **seiton**: 未検出（unknown job 参照は検出している）
- **原因分析**: `NeedsGraphRule` が needs 配列内の重複チェック（case-insensitive）を行っていない。unknown job の参照チェックはあるが、同一 job の重複参照チェックがない。
- **対処方針**: `NeedsGraphRule` に needs 配列内の case-insensitive 重複チェックを追加。
- **テストデータ**: `testdata/examples/invalid_ids_in_needs.yaml` line 4 (`needs: [bar, BAR]`)
- **実装結果**: ✅ Phase 2-b で実装済み。`NeedsGraphRule` の重複チェックを case-insensitive に変更。

### 1-3. [P1: 高] OS 固有シェル検証（`sh` on Windows）

- **対象ファイル**: `shell_name_validation.yaml` (line 27)
- **actionlint**: `shell name "sh" is invalid on Windows. available names are "bash", "cmd", "powershell", "pwsh", "python"` [shell-name]
- **seiton**: 未検出（`dash`, `fish` など無効シェルは検出、`powershell` on Linux は検出、`sh` on Windows は未検出）
- **原因分析**: `ShellNameRule` で OS 固有の不正シェルチェックが `powershell` on Linux のみ実装されており、`sh` on Windows のチェックが欠落。
- **対処方針**: `ShellNameRule` に runs-on ラベルからの OS 推定ロジックを拡張し、Windows runner での `sh` を不正として報告。
- **テストデータ**: `testdata/examples/shell_name_validation.yaml` line 27
- **実装結果**: ✅ Phase 2-c で確認済み。既存コードで正しく検出。テストケース追加。

### 1-4. [P2: 中] 非スカラー型の `${{ }}` 展開警告

- **対象ファイル**: `not_persistent_matrix_values.yaml` (line 22), `type_checks.yaml` (line 13)
- **actionlint**: `object, array, and null values should not be evaluated in template with ${{ }} but evaluating the value of type array<any>` [expression]
- **seiton**: `not_persistent_matrix_values.yaml` は完全に未検出。`type_checks.yaml` line 13 は `[expr-undefined-var]` で部分的に検出（`object value in ${{ }} will be converted to string "[Object]"`）
- **原因分析**: パーサーの式型推論で matrix 値の型を追跡しているが、配列型の `${{ }}` 展開に対する「文字列化不可」チェックが不足。
- **対処方針**: 式解析の型チェック層で、式の結果型が object/array/null の場合に `${{ }}` コンテキストで警告を出す。これはパーサーの式解析 (`ExpressionAnalyzer`) またはリンタールールとして実装。
- **テストデータ**: `testdata/examples/not_persistent_matrix_values.yaml` line 22
- **実装結果**: ✅ Phase 3-a で実装済み。`CheckTemplateTypeWithOverrides` で動的コンテキストオーバーライドを使った型推論を実行。`InferMatrixRowType` を拡張し配列型の matrix 行を `ArrayExprType` として推論。

### 1-5. [P2: 中] env マッピングの型チェック（object expected, got string）

- **対象ファイル**: `expand_object.yaml` (line 19)
- **actionlint**: `type of expression at "env" must be object but found type string` [expression]
- **seiton**: 未検出
- **原因分析**: `env:` キーに `${{ }}` 式が指定された場合、式の結果型が object（mapping）であることの検証が未実装。
- **対処方針**: env/with マッピング位置で `${{ }}` 式が使われた場合、式の結果型が object/mapping であることをチェック。パーサーの `EnvParser` またはリンタールールで実装。
- **テストデータ**: `testdata/examples/expand_object.yaml` line 19
- **実装結果**: ✅ Phase 3-b で実装済み。`CheckEnvMappingType` で `env:` 展開式がオブジェクト型でない場合に警告。`InferMatrixRowType` を拡張しスカラー型の matrix 行を `StringExprType` として推論。

### 1-6. [P2: 中] 式のプロパティアクセス型チェック（property access must be string）

- **対象ファイル**: `workflow_dispatch_input_types.yaml` (lines 35, 37), `type_checks.yaml` (line 7)
- **actionlint**: `property access of object must be type of string but got "bool"/"number"` [expression]
- **seiton**: `type_checks.yaml` line 7 は検出（`index of object must be string, but got number`）。`workflow_dispatch_input_types.yaml` lines 35, 37 は未検出。
- **原因分析**: 基本的なプロパティアクセス型チェックは実装されているが、`inputs` 定義から型推論した場合の `env[inputs.verbose]`（bool 型）の検出が不足。
- **対処方針**: 式解析で `env[expr]` のインデックスアクセス時に、`expr` の型がstring以外の場合にエラーを出す。`inputs` の型情報を活用。
- **テストデータ**: `testdata/examples/workflow_dispatch_input_types.yaml` lines 35, 37
- **実装結果**: ✅ Phase 3-c で実装済み。`ValidateIndexAccessWithOverrides` で動的コンテキストを使ったインデックス型チェックを実行。`env[inputs.verbose]`（bool）を検出。

### 1-7. [P2: 中] deprecated action input の検出

- **対象ファイル**: `deprecated_inputs.yaml` (line 9)
- **actionlint**: `avoid using deprecated input "fail_on_error" in action "reviewdog/action-actionlint@v1": Deprecated, use 'fail_level' instead` [action]
- **seiton**: 未検出（`unpinned-uses` のみ検出）
- **原因分析**: `PopularActionInputsRule` が required/unknown input のみチェックしており、deprecated input のメタデータを持っていない。
- **対処方針**: popular actions のメタデータに `deprecated` フラグと代替 input 情報を追加し、`PopularActionInputsRule` で deprecated input 使用時に警告。ただし、popular actions の metadata に deprecated 情報を持たせる必要があり、`Seiton.Update` パイプラインの拡張が必要。
- **テストデータ**: `testdata/examples/deprecated_inputs.yaml`
- **実装結果**: (未実施)

### 1-8. [P2: 中] outdated action runner の検出漏れ

- **対象ファイル**: `detect_outdated_popular_actions.yaml` (line 8)
- **actionlint**: `the runner of "actions/checkout@v3" action is too old to run on GitHub Actions` [action]
- **seiton**: `OutdatedActionRunnerRule` は存在するが、`actions/checkout@v3` に対して発火しなかった
- **原因分析**: `OutdatedActionRunnerRule` は `PopularActions.TryGet()` で action を検索し、`GetRunsUsing()` で runtime を取得する。`actions/checkout@v3` の metadata が popular actions データに含まれていないか、`runs.using` が deprecated リストにマッチしていない可能性。
- **対処方針**: `PopularActions` データで `actions/checkout@v3` の `runs.using` が `node16` であることを確認し、ルールが正しく発火するようデバッグ。popular actions データのバージョン粒度問題の可能性もある（`@v3` と `@v4` で異なる runtime を持つが、データは最新バージョンのみ保持）。
- **テストデータ**: `testdata/examples/detect_outdated_popular_actions.yaml`
- **実装結果**: ✅ Phase 2-d で実装済み。`PopularActions` にバージョン認識を追加し `actions/checkout@v3` を検出。

### 1-9. [P3: 低] YAML アンカーのエッジケース（再帰、未使用、env alias）

- **対象ファイル**: `yaml_anchor_usage.yaml` (lines 18-22)
- **actionlint**: 4 件の詳細エラー（未使用アンカー、env に plain text、alias ノードだが mapping 期待、再帰 alias）
- **seiton**: parse failure 1 件のみ（`Cannot detect a scalar value as utf8`）
- **原因分析**: VYaml パーサーがアンカーのエッジケース（env に直接アンカー定義、再帰アンカー）でうまく処理できず、パース失敗に陥る。actionlint は Go の yaml.v3 を使っており、より寛容。
- **対処方針**: VYaml のパースエラー時のリカバリを改善。現時点では優先度低。再帰アンカー検出は VYaml レベルの対応が必要な可能性あり。
- **テストデータ**: `testdata/examples/yaml_anchor_usage.yaml`
- **実装結果**: (未実施)

### 1-10. [P3: 低] 深い action metadata 検証

- **対象ファイル**: `action_metadata_syntax_validation.yaml`
- **actionlint**: 6 件（env not allowed in runs、description 必須、ファイル存在チェック、branding color/icon 不正、invalid runner name）
- **seiton**: 1 件（`invalid runs.using 'node14'`）
- **原因分析**: seiton の `local-action-inputs` ルールは runs.using の検証のみ行い、description の有無、ファイル存在チェック、branding 検証は未実装。
- **対処方針**: ローカルアクションの metadata 検証を段階的に拡充。ただし branding やファイル存在チェックはユーザーの使用頻度から見て優先度は低い。runs.using の検証（JavaScript action で env が使えない等）は有用。
- **テストデータ**: `testdata/examples/action_metadata_syntax_validation.yaml`
- **実装結果**: (未実施)

### 1-11. [対象外] pyflakes / shellcheck 連携

- **対象ファイル**: `pyflakes_integration.yaml`, `shellcheck_integration.yaml`
- **actionlint**: pyflakes / shellcheck を外部ツール呼び出しで統合
- **seiton**: 未検出（設計方針として外部ツール連携は行わない）
- **対処方針**: **対応不要**。seiton はスタンドアロンの静的解析ツールとして設計されており、外部ツール依存は意図的に排除している。

---

## 2. 検出品質の問題一覧（seiton が検出しているが改善が必要）

### 2-1. [P1: 高] 重複診断（同一問題が複数ルールから報告）

- **対象ファイル**: `comparison_strict_checks.yaml`, `contexts_and_builtin_funcs.yaml`, `webhook_checks.yaml`, `workflow_call_jobs.yaml`
- **問題**: 同じエラーが `[parse]` と `[expr-undefined-var]` の両方から報告される
  - 例: `comparison_strict_checks.yaml` line 13 で `[parse]` と `[expr-undefined-var]` が同じ「object を string と比較」エラーを出す
  - 例: `contexts_and_builtin_funcs.yaml` で undefined context や undefined property が `[parse]` と `[expr-undefined-var]` で重複報告
  - 例: `webhook_checks.yaml` で同じ問題が `[parse]` と `[glob-pattern]` で重複報告
  - 例: `workflow_call_jobs.yaml` で reusable workflow の問題が `[parse]`, `[job-structure]`, `[reusable-workflow]` で三重報告
- **原因分析**: パーサーが式の意味解析まで行い diagnostics を出しつつ、リンターのルールでも同等のチェックを行っている。
- **対処方針**:
  - 方策A: パーサー diagnostics とリンター diagnostics の重複排除（dedup）ロジックを `LintEngine` に追加。同一行・同一種別の診断を1つにまとめる。
  - 方策B: リンタールール側でパーサー diagnostics がカバーしている領域をスキップ。
  - **推奨**: 方策A。出力段階で位置＋メッセージ類似度による dedup が最も安全。
- **実装結果**: ✅ 実装済み。`LintEngine.cs` の `DiagnosticIdentity` を 8 フィールドから 3 フィールド（Severity, Message, StartLine）に緩和。列位置の差異を無視して同一行・同一メッセージの診断を重複排除する。

### 2-2. [P1: 高] 列オフセットの不一致

- **対象ファイル**: `expression_syntax_error.yaml`, `broken_yaml.yaml`, `builtin_func_special_checks.yaml`
- **問題**: actionlint と比較して列が 1 ずれている
  - `expression_syntax_error.yaml` line 11: actionlint=65, seiton=64
  - `expression_syntax_error.yaml` line 13: actionlint=38, seiton=37
  - `broken_yaml.yaml` line 6: actionlint=16, seiton=17
  - `builtin_func_special_checks.yaml` line 14: actionlint=31 (式中の位置), seiton=13 (行頭位置)
- **原因分析**:
  - 式パーサーのオフセット計算で `${{ }}` 内のカーソル位置が 1 ずれている可能性
  - VYaml の列は 0-based、actionlint は 1-based の違い（seiton は +1 して 1-based にしているが端のケースで不一致）
  - `fromJSON()` エラーで式中の位置ではなく行頭位置を報告している
- **対処方針**:
  - 式パーサーの位置計算を見直し、`${{ }}` 内のオフセットが正しく計算されているか確認
  - `fromJSON()` エラーの位置を式中の関数呼び出し位置にする
- **実装結果**: ⏭️ スキップ。詳細分析の結果、seiton は式本体末尾を指し、actionlint は `}}` デリミタを指す慣例の差異であり、バグではない。式先頭でのエラー（line 7, 9）は一致しており、末尾のエラーのみ 1 列の差がある。

### 2-3. [P1: 高] 行オフセットの不一致

- **対象ファイル**: `if_cond_always_true.yaml`
- **問題**: multiline if 条件で行がずれる
  - actionlint: line 19, seiton: line 20（改行を含む if 値）
- **原因分析**: `if:` の値が複数行にまたがる場合、seiton はフォールディング後の行位置を報告している可能性。
- **対処方針**: `if:` 値の開始位置を正確に記録するようにパーサーを修正。
- **実装結果**: ⏭️ スキップ。seiton は値内容の行（line 20）を指し、actionlint は `if:` キーの行（line 19）を指す。seiton の方がユーザーにとって有用（問題の実体がある行を示す）。

### 2-4. [P2: 中] invalid action format が warning [unpinned-uses] で報告される

- **対象ファイル**: `invalid_action_format.yaml`
- **問題**: `actions/checkout`（ref なし）、`checkout@v2`（owner なし）、`docker://image:`（空タグ）、`.github/my-actions/do-something`（ref なし）が actionlint では error [action] だが、seiton では warning [unpinned-uses] として報告
- **原因分析**: `UnpinnedUsesRule` が invalid format を「ピン留めされていない」として検出するが、本質的には「フォーマット不正」。actionlint は明確に「invalid format because ref is missing」と報告。
- **対処方針**:
  - `UnpinnedUsesRule` で format 自体が不正な場合は severity を error にし、メッセージを「invalid format」に変更
  - または、パーサー段階で uses の形式チェックを行い `[parse]` diagnostics として error で報告
- **実装結果**: ✅ 実装済み。`UnpinnedUsesRule.cs` で invalid format のケースを `AddStepWarning`/`AddJobWarning` から `AddStepError`/`AddJobError` に変更。空 docker 参照、owner なし、ref なしのケースが error severity で報告される。

### 2-5. [P2: 中] comparison_strict_checks で `>` 演算子の bool 比較メッセージが不十分

- **対象ファイル**: `comparison_strict_checks.yaml` (line 16)
- **actionlint**: `"bool" value cannot be compared to "number" value with ">" operator`
- **seiton**: `operator '>' does not support bool type`
- **問題**: seiton は比較相手の型（number）を示していない。actionlint は両辺の型と演算子を明示。
- **対処方針**: 比較演算子のエラーメッセージに左辺型・右辺型・演算子を含める形に改善。
- **実装結果**: ✅ 実装済み。`ExpressionSemanticAnalyzer.cs` の `ValidateCompareOp` および `ValidateCompareOpWithOverrides` のメッセージを `"{leftType} value cannot be compared to {rightType} value with '{op}' operator"` に変更。actionlint と同等のメッセージ形式。

### 2-6. [P3: 低] matrix_checks の行・列の違い

- **対象ファイル**: `matrix_checks.yaml`
- **問題**: `"platform" in "exclude" section does not exist in matrix` について
  - actionlint: line 12, seiton: line 6（exclude セクションの行 vs matrix 全体の開始行）
- **原因分析**: seiton の `MatrixRule` が exclude キー自体の位置ではなく matrix 全体の位置で報告している。
- **対処方針**: exclude 内の具体的なキー位置で報告するよう改善。
- **実装結果**: ✅ 実装済み。`MatrixRule.cs` で unknown axis 報告時に `matrix.Range`（matrix 全体の位置）の代わりに `pair.Key`（exclude エントリのキー位置）から `BuildKeyLocation` で正確な `TextRange` を計算して使用。`SpanHelpers.ComputeLineColumn` を利用。

---

## 3. 完全一致・問題なしの項目（参考）

以下の example は seiton が actionlint と同等（または上位互換）の検出を行っており、対処不要:

| Example | 状態 | 備考 |
|---------|------|------|
| `broken_yaml` | ✅ 同等 | 列ずれ 1 あり（軽微） |
| `builtin_func_special_checks` | ✅ 同等 | 4/4 検出、fromJSON 位置ずれあり |
| `contexts_special_functions_availability` | ✅ 同等 | 3/3 検出 |
| `contextual_matrix_values` | ✅ 同等 | 3/3 検出 |
| `contextual_needs_object` | ✅ 同等 | 4/4 検出 |
| `contextual_steps_outputs` | ✅ 同等 | 2/2 検出 |
| `cron_schedule_check` | ✅ 同等 | 3/3 検出 |
| `cyclic_deps_needs` | ✅ 同等 | サイクル検出 |
| `dangling_alias` | ✅ 同等 | 不明アンカー検出 |
| `deprecated_workflow_commands` | ✅ 同等 | deprecated command 検出 |
| `env_var_names` | ✅ 同等 | 不正環境変数名検出 |
| `glob` | ✅ 同等 | 4/4 検出 |
| `hardcoded_credentials` | ✅ 同等 | 2/2 検出 |
| `id_naming_convention` | ✅ 同等 | 4/4 検出 |
| `job_step_ids_duplicate` | ✅ 同等 | 重複検出 |
| `local_action_inputs` | ✅ 同等 | required/unknown 検出 |
| `local_action_outputs` | ✅ 同等 | steps 出力参照検出 |
| `missing_required_keys` | ✅ 同等 | runs-on 欠落、重複 matrix key |
| `permissions` | ✅ 同等 | 4/4 検出 |
| `popular_action_inputs` | ✅ 同等 | required/unknown 検出 |
| `popular_action_outputs` | ✅ 同等 | steps 出力参照検出 |
| `reusable_workflow_outputs` | ✅ 同等 | jobs 出力参照検出 |
| `runner_label_check` | ✅ 同等 | 3/3 検出 |
| `runner_label_conflict` | ✅ 同等 | OS ファミリ衝突検出 |
| `unexpected_keys` | ✅ 同等 | 2/2 検出 |
| `unexpected_mapping_values` | ✅ 同等 | 3/3 検出 |
| `untrusted_input` | ✅ 同等 | 3/3 検出 |
| `webhook_checks` | ✅ 同等 | 5/5 検出（重複あり） |
| `workflow_call_definitions` | ✅ 同等 | 3/3 検出 |
| `workflow_call_jobs` | ✅ 同等 | 4/4 検出（重複あり） |
| `workflow_dispatch_input_types` | ✅ 同等 | 7/9 検出（Phase 3-c でインデックス型チェック追加） |
| `workflow_inputs_secrets_types` | ✅ 同等 | 2/2 検出 |
| `yaml_anchors` | ✅ 同等 | 3/3 検出 |

seiton 独自の追加検出（actionlint にない）:
- `[job-permissions-required]`: 全ファイルで job レベル permissions チェック
- `[job-timeout-minutes-required]`: 全ファイルで timeout-minutes チェック
- `[runner-no-latest]`: latest ラベル使用チェック
- `[unpinned-uses]`: SHA ピン留めチェック
- `[unpinned-image]`: Docker イメージ digest ピン留めチェック
- `[checkout-persist-credentials]`: persist-credentials 設定チェック
- `[template-injection]`: untrusted input の template injection リスク検出
- `[run-env-context-direct-use]`: env の直接参照チェック
- `[run-inputs-context-direct-use]`: inputs の直接参照チェック

---

## 4. 実装フェーズ計画

### Phase 1: 検出品質の改善（重複排除・位置精度）

seiton の既存検出が正確に動作するための品質改善。新規ルール不要。

| # | 項目 | 対象 | 難易度 | 状態 |
|---|------|------|--------|------|
| 1-a | 重複診断の排除 | `LintEngine` 出力段階での dedup | 中 | ✅ |
| 1-b | 式パーサーの列オフセット修正 | `ExpressionParser` 位置計算 | 低 | ⏭️ |
| 1-c | multiline if の行オフセット修正 | `IfCondRule` / パーサー | 低 | ⏭️ |
| 1-d | invalid action format の severity 修正 | `UnpinnedUsesRule` | 低 | ✅ |
| 1-e | 比較演算子エラーメッセージ改善 | 式解析のエラー生成 | 低 | ✅ |
| 1-f | matrix exclude 位置精度改善 | `MatrixRule` | 低 | ✅ |

### Phase 2: 既存ルールの検出漏れ修正

既にルールが存在するが、チェック不足の箇所を補完。

| # | 項目 | 対象ルール | 難易度 | 状態 |
|---|------|-----------|--------|------|
| 2-a | glob バックスラッシュエスケープ検証 | `GlobPatternRule` | 低 | ✅ |
| 2-b | needs 重複 ID チェック（case-insensitive） | `NeedsGraphRule` | 低 | ✅ |
| 2-c | OS 固有シェル検証（`sh` on Windows） | `ShellNameRule` | 低 | ✅ |
| 2-d | outdated action runner 検出の修正 | `OutdatedActionRunnerRule` | 中 | ✅ |

Phase 2 実装結果

**全 4 項目完了。テスト 665 件全パス。ベンチマーク回帰なし。**

| # | 変更内容 | テスト |
|---|---------|--------|
| 2-a | `GlobPatternRule.TryGetInvalidReason` にバックスラッシュエスケープ検証を追加。`\` の後にグロブメタ文字（`*`, `?`, `[`, `]`, `\`, `!`, `+`, `#`）以外が続く場合エラー。末尾 `\` もエラー。`IsGlobEscapable` ヘルパー追加 | `ng-backslash-regex-escape-in-tags`, `ng-trailing-backslash-in-branches`, `ok-valid-backslash-escape-star` |
| 2-b | `NeedsGraphRule.VisitJobPre` の重複チェックを `SequenceEqual`（case-sensitive）から `EqualsAsciiIgnoreCase` に変更。`needs: [bar, BAR]` を検出 | `ng-duplicate-needs-case-insensitive` |
| 2-c | `ShellNameRule` は既存コードで `sh` on Windows を正しく検出（`Shells.IsAvailableOnWindows` に `sh` 未含）。テストケースを追加して確認 | `ng-sh-on-windows`, `ok-sh-on-ubuntu` |
| 2-d | `PopularActions` データパイプラインを拡張: `targets.json` に `maxDeprecatedMajorVersion` フィールド追加 → `popular_actions.json` → 生成コードに `GetMaxDeprecatedMajorVersion()` メソッド追加。`OutdatedActionRunnerRule` でバージョンタグ抽出 (`TryExtractMajorVersion`) し、閾値以下ならエラー。`actions/checkout@v3` を node16 使用として検出 | `ng-outdated-checkout-v3`, `ng-outdated-checkout-v2`, `ng-docker-login-v2` |

**ベンチマーク（Phase 1 基準との比較）:**
- Parsing Mean: Small +7.2%, Medium +3.7%, Large +7.2%（許容範囲 +10%）
- Allocated: Small +8.6%, Medium -0.4%, Large -1.9%（許容範囲 +20%）

**Lessons learned:**
- YAML single-quoted 文字列では `\` はエスケープされない（リテラル）。`\\` は 2 文字のバックスラッシュ。テストでトレーリングバックスラッシュを表現するには YAML double-quoted `"feature\\"` を使う
- `PopularActions` の `MatchesActionReference` はバージョンタグを無視してアクション名のみマッチする。バージョン認識には追加ロジックが必要
- `maxDeprecatedMajorVersion` を `targets.json`（手動管理）に配置し、merge パイプラインで `popular_actions.json` に伝播させることで、fetch→parse→merge サイクルでも値が保持される

### Phase 3: 新規チェックの追加（式の型検査）

式の型推論と型チェックの拡充。パーサーまたはリンターの拡張が必要。

| # | 項目 | 対象 | 難易度 | 状態 |
|---|------|------|--------|------|
| 3-a | 非スカラー型の `${{ }}` 展開警告 | 式型チェック | 高 | ✅ |
| 3-b | env マッピングの型チェック | 式型チェック / パーサー | 中 | ✅ |
| 3-c | プロパティアクセスのインデックス型チェック | 式型チェック | 中 | ✅ |

#### Phase 3 実装結果

**3-a: 非スカラー型の `${{ }}` 展開警告**
- `ExpressionSemanticAnalyzer.CheckTemplateTypeWithOverrides` を新設。動的コンテキストオーバーライド（matrix/inputs/needs/steps）を使った型推論でテンプレート型チェックを実行
- `ExprUndefinedVarRule.ValidateTemplateType` にコンテキスト引数を追加し、オーバーライド対応版を呼び出すよう変更
- `DynamicContextTypeBuilder.InferMatrixRowType` を拡張: 全値が `RawYamlArray` の場合は `ArrayExprType` を返すよう追加
- 例: `matrix.bar: [[42], [true]]` → `${{ matrix.bar }}` で「array value in ${{ }}」警告を検出

**3-b: env マッピングの型チェック**
- `ExpressionSemanticAnalyzer.CheckEnvMappingType` を新設。`env:` に展開される式がオブジェクト型でない場合に警告
- `ExprUndefinedVarRule.ValidateEnvMappingType` を追加し `CheckEnv` から呼び出し
- `InferMatrixRowType` を拡張: 全値が `RawYamlString`（スカラー）の場合は `StringExprType` を返すよう追加
- 例: `env: ${{ matrix.env_string }}` で「string value cannot be expanded as mapping」を検出

**3-c: プロパティアクセスのインデックス型チェック**
- `ExpressionSemanticAnalyzer.ValidateIndexAccessWithOverrides` を新設。動的コンテキストを使った型推論でインデックス型チェックを実行
- `ValidateNodePropertyAccess` の `IndexAccess` ケースで呼び出しを追加
- 例: `env[inputs.verbose]`（verbose は boolean 型）→「index of object must be string, but got bool」を検出

### Phase 4: メタデータ拡充

外部データ（popular actions metadata）の拡張が必要な項目。

| # | 項目 | 対象 | 難易度 | 状態 |
|---|------|------|--------|------|
| 4-a | deprecated action input 検出 | `PopularActionInputsRule` + データ拡張 | 高 | |
| 4-b | 深い action metadata 検証 | `LocalActionInputsRule` 拡張 | 中 | |

### Phase 5: YAML パーサーの改善（低優先度）

| # | 項目 | 対象 | 難易度 | 状態 |
|---|------|------|--------|------|
| 5-a | YAML アンカーのエッジケース改善 | パーサーのエラーリカバリ | 高 | ✅ 完了 |

**5-a の実装内容:**
- `VYamlStreamAdapter` の `GetScalarUtf8()`, `GetScalarSlice()`, `GetScalarString()`, `SnapshotCurrentEvent()` で VYaml の null scalar（`env: &anchor` のように値なしでアンカーを定義するケース）に対してガードを追加
- VYaml の `GetScalarAsUtf8()` は null scalar に対して例外をスローするため、事前に `IsNullScalar()` チェックで空スパン/null を返すように修正
- `_definedAnchors` の位置記録を `_parser.CurrentMark` から `CurrentStart` に変更し、null scalar の位置補正を活用
- テスト 3 件追加: `Parse_NullScalarAnchor_DoesNotCrash`, `Parse_NullScalarAnchorRedefined_DoesNotCrash`, `Parse_YamlAnchorUsageFixture_DoesNotCrash`
- `testdata/examples/yaml_anchor_usage.yaml` が fatal crash せずに 4 errors, 7 warnings を検出するようになった

---

## 5. テストデータの活用

actionlint の example YAML は `.references/actionlint/testdata/examples/` にある。

seiton のテストでこれらを活用するにあたり:
1. seiton 固有のテストデータは `testdata/` 配下に配置する（既存の `ok/`, `err/` パターン）
2. actionlint の example は参照用として `.references/` に保持
3. 各 Phase で修正する項目ごとに、`testdata/` に `ng-*` / `ok-*` のテストケースを追加する

---

## 6. 対処不要の判断根拠

| 項目 | 理由 |
|------|------|
| pyflakes 連携 | seiton は外部ツール依存を排除する設計方針。Python linting は専用ツールで行うべき |
| shellcheck 連携 | 同上。shell script linting は専用ツールで行うべき |
| broken_yaml の列ずれ (1) | VYaml の列表現（0-based vs 1-based）に起因し、大きな影響なし |
