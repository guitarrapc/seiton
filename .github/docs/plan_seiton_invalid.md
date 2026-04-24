# Seiton vs actionlint — 検出ギャップ分析と対処計画

> **対象**: actionlint `testdata/examples/` の 51 YAML ファイル (155 expected errors)
> **日付**: 2026-04-24
> **目的**: actionlint が検出するのに seiton が検出しないパターン、seiton のメッセージ/位置がずれているパターンを洗い出し、対処優先度を決定する。

---

## 1. サマリー

| カテゴリ | 件数 | 説明 |
|---|---|---|
| **A. actionlint で検出 / seiton で未検出** | 29 | actionlint が出すエラーに相当する検出が seiton に無い |
| **B. 両方で検出 (OK)** | 87 | 同等の検出あり |
| **C. seiton のメッセージ/位置ずれ** | 9 | 検出はあるが行・列・メッセージに改善余地 |
| **D. seiton のみ検出 (追加チェック)** | 多数 | seiton 独自ルール (job-permissions-required, runner-no-latest, unpinned-uses, etc.) — 対応不要 |
| **E. スコープ外 (外部ツール連携)** | 5 | pyflakes/shellcheck 連携 — seiton のスコープ外 |

---

## 2. A: actionlint で検出 / seiton で未検出 — 詳細一覧

### A-1: `comparison_strict_checks` — bool > number 比較

| | 内容 |
|---|---|
| **actionlint** | `test.yaml:16:17: "bool" value cannot be compared to "number" value with ">" operator [expression]` |
| **seiton** | 未検出 |
| **原因** | 式セマンティクス分析で `>` 演算子の型不一致チェックが未実装。`==` は実装済み。 |
| **対処** | `ExpressionSemanticAnalyzer` に `>`, `>=`, `<`, `<=` の型不一致チェックを追加。 |
| **優先度** | **高** — 基本的な式チェック。 |

### A-2: `builtin_func_special_checks` — property undefined on object

| | 内容 |
|---|---|
| **actionlint** | `test.yaml:6:18: property "mac" is not defined in object type {linux: string; win: string} [expression]` |
| **seiton** | 未検出 |
| **原因** | matrix 値のオブジェクト型を推論して property チェックする機能が不足。 |
| **対処** | matrix オブジェクト型のプロパティ推論を強化し、存在しないプロパティアクセスを検出。 |
| **優先度** | **中** — matrix 型推論の精度向上。 |

### A-3: `contextual_matrix_values` — 未定義 matrix プロパティ (3件)

| | 内容 |
|---|---|
| **actionlint** | `matrix.platform` 未定義、`matrix.package.dev` 未定義、`matrix.os` 空 matrix |
| **seiton** | いずれも未検出 |
| **原因** | `ExprUndefinedVarRule` は matrix プロパティの未定義チェックをサポートしているが、包含オブジェクト型の構造体推論 (nested object property) や他ジョブの matrix アクセスに対応していない。 |
| **対処** | matrix axis 名の推論精度を向上。nested object property (e.g. `matrix.package.dev`) の型チェックを追加。空 matrix (別ジョブの matrix 参照) の検出。 |
| **優先度** | **高** — ユーザーの typo 発見に直結。 |

### A-4: `contextual_needs_object` — 未定義 needs プロパティ (4件)

| | 内容 |
|---|---|
| **actionlint** | `needs.prepare` 未定義 (needs に未宣言)、`needs.install.outputs.foo` 未定義、`needs.some_job` 未定義、`needs.build` 未定義 (other ジョブから) |
| **seiton** | いずれも未検出 |
| **原因** | `ExprUndefinedVarRule` が needs コンテキストのプロパティレベルチェック (どのジョブの outputs にどのキーがあるか) を実装していない。 |
| **対処** | needs コンテキストの型を各ジョブの outputs 宣言から構築し、未定義プロパティを検出。 |
| **優先度** | **高** — needs 参照ミスは実行時エラーに直結。 |

### A-5: `contextual_steps_outputs` — 未定義 steps outputs (2件)

| | 内容 |
|---|---|
| **actionlint** | `steps.get_value` 未定義 (step id が存在しない) |
| **seiton** | 未検出 |
| **原因** | `ExprUndefinedVarRule` が steps コンテキストの step id 存在チェックを行っていない。 |
| **対処** | 同一ジョブ内の step id を収集し、`steps.<id>` 参照が存在する step id か検証。 |
| **優先度** | **高** — step output 参照ミスは頻出バグ。 |

### A-6: `contexts_special_functions_availability` — runner コンテキスト利用不可

| | 内容 |
|---|---|
| **actionlint** | `test.yaml:14:17: context "runner" is not allowed here` (workflow-level if) |
| **seiton** | 未検出 (env コンテキストは検出済み) |
| **原因** | context availability チェックで `runner` コンテキストの可用性制限が漏れている。 |
| **対処** | context availability テーブルに `runner` の利用不可スコープを追加。 |
| **優先度** | **中** — あまり頻繁には発生しない。 |

### A-7: `cron_schedule_check` — invalid timezone

| | 内容 |
|---|---|
| **actionlint** | `test.yaml:9:17: invalid timezone "Asia/Somewhere"` |
| **seiton** | 未検出 |
| **原因** | `ScheduleEventRule` にタイムゾーン検証は実装済みだが、YAML パースで schedule の timezone フィールドが正しく取得されていない可能性。テストデータの構造を確認要。 |
| **対処** | schedule パーサーで timezone フィールドの取得を確認・修正。 |
| **優先度** | **中** |

### A-8: `deprecated_inputs` — deprecated input 警告

| | 内容 |
|---|---|
| **actionlint** | `avoid using deprecated input "fail_on_error" in action "reviewdog/action-actionlint@v1"` |
| **seiton** | 未検出 |
| **原因** | `PopularActionInputsRule` は popular action の deprecated input チェックを持っているが、`reviewdog/action-actionlint` が popular actions カタログに含まれていないか、deprecated フラグが設定されていない。 |
| **対処** | popular actions カタログの deprecated フラグ整備を確認。カタログに無い action は対象外であるためスコープ検討。 |
| **優先度** | **低** — カタログ依存。 |

### A-9: `detect_outdated_popular_actions` — outdated runner

| | 内容 |
|---|---|
| **actionlint** | `the runner of "actions/checkout@v3" action is too old to run on GitHub Actions` |
| **seiton** | 未検出 |
| **原因** | seiton には「action の runner (node16) が古すぎる」検出ルールが存在しない。 |
| **対処** | 新ルール `OutdatedActionRunner` を追加。popular actions カタログの runs.using 情報から node16/node12 を検出。 |
| **優先度** | **高** — node16 deprecation は 2024 年以降の最重要課題。 |

### A-10: `expand_object` — env に string 型を展開

| | 内容 |
|---|---|
| **actionlint** | `type of expression at "env" must be object but found type string` |
| **seiton** | 未検出 |
| **原因** | `env:` セクションに `${{ matrix.env_string }}` のような式を展開した際、式の型が object であるべきところ string になるチェックが未実装。 |
| **対処** | env セクションの式展開時に型チェック (object 必須) を追加。 |
| **優先度** | **中** — 実行時 silent failure になるパターン。 |

### A-11: `glob` — invalid branch 名文字 `^`、invalid `+` 構文、`.` / `..` パス (3件)

| | 内容 |
|---|---|
| **actionlint** | `'^'` invalid char、`'+' after '*'` invalid pattern、`'.' and '..' not allowed` |
| **seiton** | `v[9-1]` のみ検出。他 3 件未検出。 |
| **原因** | `GlobPatternRule` の glob 検証が不完全。`^` などの git-check-ref-format 違反文字、`*+` パターン、`.` / `..` パス検証が未実装。 |
| **対処** | glob パターン検証を強化: (1) ref 名禁止文字チェック、(2) `*+` `**+` 連続特殊文字チェック、(3) `.` `..` セグメントチェック。 |
| **優先度** | **高** — glob パターンのバグは頻出。 |

### A-12: `hardcoded_credentials` — ハードコード password (2件)

| | 内容 |
|---|---|
| **actionlint** | `"password" section in "container" section should be specified via secrets`、同 services |
| **seiton** | 未検出 |
| **原因** | seiton の `CredentialsRule` はレジストリに credentials が未設定の場合を検出するが、password がハードコードされている (secrets 経由でない) 検出は未実装。 |
| **対処** | `CredentialsRule` または新ルールで credentials.password が `${{ secrets.* }}` でない場合を警告。 |
| **優先度** | **高** — セキュリティリスク。 |

### A-13: `invalid_action_format` — Docker empty tag

| | 内容 |
|---|---|
| **actionlint** | `tag of Docker action should not be empty: "docker://image"` |
| **seiton** | `unpinned-image` で検出するが「empty tag」とは異なるメッセージ。 |
| **原因** | Docker action の空タグ検出は `unpinned-image` ルールでカバーしているが、メッセージが「not pinned by digest」であり「empty tag」というエラーとは意味が異なる。 |
| **対処** | Docker action の空タグ (`docker://image:`) を明示的に検出するメッセージを追加検討。 |
| **優先度** | **低** — 一応検出はしている。 |

### A-14: `invalid_ids_in_needs` — needs 重複

| | 内容 |
|---|---|
| **actionlint** | `job ID "BAR" duplicates in "needs" section` |
| **seiton** | 未検出 (`unknown` job 参照は検出済み) |
| **原因** | `NeedsGraphRule` が needs 内の重複 job id チェック (case-insensitive) を未実装。 |
| **対処** | `NeedsGraphRule` に needs リスト内の重複チェックを追加。 |
| **優先度** | **中** |

### A-15: `local_action_outputs` — local action output 未定義 (2件)

| | 内容 |
|---|---|
| **actionlint** | `property "my_action" is not defined`、`property "some-value" is not defined in object type {some_value: string}` |
| **seiton** | 未検出 |
| **原因** | local action の output 定義を読み取り、`steps.<id>.outputs.<name>` の存在チェックを行う機能がない。 |
| **対処** | `LocalActionInputsRule` を拡張、または新ルール `LocalActionOutputsRule` で local action の outputs 宣言と実際の参照を照合。 |
| **優先度** | **中** — local action 使用時のバグ発見に有用。 |

### A-16: `matrix_checks` — exclude 値が matrix にマッチしない

| | 内容 |
|---|---|
| **actionlint** | `value "13" in "exclude" does not match in matrix "node" combinations` |
| **seiton** | 未検出 (exclude 内の unknown axis と duplicate value は検出済み) |
| **原因** | `MatrixRule` が exclude 値と matrix 軸の実際の値セットを照合していない。 |
| **対処** | exclude 内の値が対応する matrix 軸の値に含まれるかチェック。 |
| **優先度** | **低** — exclude のマッチ外れは silent failure だが実害少。 |

### A-17: `missing_required_keys` — 重複検出

| | 内容 |
|---|---|
| **seiton** | `job 'test' requires runs-on (or uses)` が parser と job-structure rule の両方から出力 |
| **原因** | parser-level と lint-level で同一チェックが重複。 |
| **対処** | → **C (メッセージ/位置ずれ)** カテゴリで対処。 |

### A-18: `not_persistent_matrix_values` — array を template 展開

| | 内容 |
|---|---|
| **actionlint** | `object, array, and null values should not be evaluated in template with ${{ }}` |
| **seiton** | 未検出 |
| **原因** | 式セマンティクス分析で template 展開 (`${{ }}`) 時に object/array/null 型を警告する機能がない。 |
| **対処** | template 展開式の結果型が string/number/bool 以外の場合に警告。 |
| **優先度** | **中** — 実行時に `[object Object]` や空文字列になるパターン。 |

### A-19: `popular_action_outputs` — popular action output 未定義 (2件)

| | 内容 |
|---|---|
| **actionlint** | `property "cache" is not defined`、`property "cache_hit" is not defined in {cache-hit: string}` |
| **seiton** | 未検出 |
| **原因** | popular action の output 定義を型情報として持ち、`steps.<id>.outputs.<name>` を検証する機能がない。 |
| **対処** | popular actions カタログに outputs 情報を追加し、`ExprUndefinedVarRule` で output 参照を検証。 |
| **優先度** | **高** — popular action の output typo は非常に頻出。 |

### A-20: `reusable_workflow_outputs` — (部分的に検出)

| | 内容 |
|---|---|
| **actionlint** | `property "imagetag" is not defined in object type {image_tag: string}` |
| **seiton** | `property 'imagetag' is not defined in 'jobs' object` — 検出済みだがメッセージが曖昧 |
| **対処** | → **C カテゴリ** でメッセージ改善。 |

### A-21: `runner_label_check` — unknown runner label (2件不足)

| | 内容 |
|---|---|
| **actionlint** | `linux-latest` unknown、`gpu` unknown、`macos-10.13` unknown |
| **seiton** | `macos-10.13` のみ検出。`linux-latest` と `gpu` 未検出。 |
| **原因** | `runs-on: ${{ matrix.runner }}` で matrix 展開後のラベル検証ができない。matrix の各値を展開してラベル検証する必要がある。`gpu` は self-hosted preset label として除外されている。 |
| **対処** | matrix 展開後のラベルチェック対応。self-hosted preset (`arm64`, `gpu` 等) の扱い検討。 |
| **優先度** | **中** — matrix 経由の runner label はエッジケース。actionlint も config ファイルでカスタムラベルを許容しており、`gpu` は actionlint.yaml で設定するケース。seiton でも同様にconfig対応で十分。 |

### A-22: `type_checks` — template 展開時の object 型警告

| | 内容 |
|---|---|
| **actionlint** | `object, array, and null values should not be evaluated in template with ${{ }}` (line 13) |
| **seiton** | 未検出 (A-18 と同じ根本原因) |
| **対処** | A-18 と同じ — template 展開式の型チェック追加。 |

### A-23: `untrusted_input` — 複数の untrusted input 検出不足 (2件)

| | 内容 |
|---|---|
| **actionlint** | `github.event.head_commit.author.name` untrusted (line 19)、object filter `github.event.*.body` untrusted (line 22) |
| **seiton** | line 10 の `github.event.pull_request.title` は検出するが、action の `with:` 内 script (github-script) と object filter は未検出。 |
| **原因** | `TemplateInjectionRule` が `with:` の `script` キー (github-script 特有) をチェックしていない。object filter (`.*`) による untrusted 推論もない。 |
| **対処** | (1) `with.script` への template injection チェック追加 (github-script 向け)、(2) `github.event.*.body` 等の object filter 式を untrusted として検出。 |
| **優先度** | **高** — セキュリティリスク。 |

### A-24: `webhook_checks` — tags filter not available for release

| | 内容 |
|---|---|
| **actionlint** | `"tags" filter is not available for release event` |
| **seiton** | `on.release does not support option: ` (空文字列) — 検出しているがメッセージが壊れている。 |
| **対処** | → **C カテゴリ** でメッセージ修正。 |

### A-25: `workflow_call_definitions` — required + default 競合

| | 内容 |
|---|---|
| **actionlint** | `input "path" of workflow_call event has the default value "", but it is also required` |
| **seiton** | 未検出 |
| **原因** | `WorkflowCallInputDefaultRule` が required + default の競合チェックを実装していない。 |
| **対処** | workflow_call input が required かつ default を持つ場合に警告。 |
| **優先度** | **中** |

### A-26: `workflow_dispatch_input_types` — input type "text" 不正

| | 内容 |
|---|---|
| **actionlint** | `input type of workflow_dispatch event must be one of "string", "number", "boolean", "choice", "environment" but got "text"` |
| **seiton** | parser が `type: text` を含む行で別のエラーを出すが、`id` input の `type: text` は検出しない（`kind` の行 8 で検出している）。 |
| **原因** | パーサーが `type: text` をエラーとして報告する位置が off-by-one。 |
| **対処** | → 確認が必要。testdata の YAML 構造を見ると `id` input の `type: text` (line 6) がパーサーで検出されていない。 |
| **優先度** | **中** |

### A-27: `workflow_dispatch_input_types` — expression property 未定義 (4件)

| | 内容 |
|---|---|
| **actionlint** | `inputs.massage` 未定義 (line 33)、`inputs.verbose` の bool を key に (line 35)、`inputs.age` の number を key に (line 37)、`github.event.inputs.massage` 未定義 (line 39) |
| **seiton** | いずれも式レベルでは未検出 (`run-inputs-context-direct-use` で `${{ inputs.* }}` 使用は検出するが、プロパティ名の正当性や型は未チェック) |
| **原因** | `ExprUndefinedVarRule` が `inputs` コンテキストのプロパティ名を workflow_dispatch input 宣言と照合していない。また object key の型チェックも未対応。 |
| **対処** | inputs コンテキストの型を workflow_dispatch 宣言から構築し、未定義プロパティと型不一致を検出。 |
| **優先度** | **中** — `run-inputs-context-direct-use` で代替的に検出はしている。 |

### A-28: `workflow_inputs_secrets_types` — inputs.uri 未定義

| | 内容 |
|---|---|
| **actionlint** | `property "uri" is not defined in object type {lucky_number: number; url: string}` |
| **seiton** | 未検出 (`secrets.credentials` は検出済み) |
| **原因** | A-27 と同根。workflow_call inputs の型推論が不十分。 |
| **対処** | A-27 と同じ。 |

### A-29: `yaml_anchor_usage` — anchor/alias 高度な検証 (4件)

| | 内容 |
|---|---|
| **actionlint** | unused anchor、env に plain text alias、mapping 期待で alias、recursive alias |
| **seiton** | VYaml パーサーが `Cannot detect a scalar value as utf8` でクラッシュし、1件のパースエラーのみ。 |
| **原因** | VYaml が recursive alias や一部の anchor/alias パターンをサポートしていない。 |
| **対処** | VYaml の制約。recovery が難しいため低優先度。ドキュメントに制限事項として記載。 |
| **優先度** | **低** — YAML ライブラリの制約。 |

---

## 3. C: seiton のメッセージ/位置ずれ — 詳細一覧

### C-1: `broken_yaml` — 行番号が 1 行ずれ

| | 内容 |
|---|---|
| **actionlint** | `test.yaml:6:16` |
| **seiton** | `broken_yaml.yaml:7:17` |
| **原因** | VYaml のエラー報告位置が 0-indexed で、seiton が +1 している。結果的に 1 行ずれている。 |
| **対処** | YAML パースエラーの位置を VYaml のエラーメッセージから直接取得し、正確に変換。 |
| **優先度** | **高** — パースエラーの位置ずれはユーザー体験に直結。 |

### C-2: `comparison_strict_checks` — `==` 検出は warning、actionlint は error 相当

| | 内容 |
|---|---|
| **seiton** | `warning [parse] object value cannot be compared to string value with '==' operator` |
| **actionlint** | expression error |
| **対処** | severity を検討。actionlint と合わせて error にするか、現状の warning を維持するか。 |
| **優先度** | **低** |

### C-3: `if_cond_always_true` — `if: false` の行番号

| | 内容 |
|---|---|
| **actionlint** | `test.yaml:9:13` |
| **seiton** | `if_cond_always_true.yaml:9:13` — **一致 ✓** |
| **対処** | 不要。 |

### C-4: `if_cond_always_true` — multiline `if:` の行番号

| | 内容 |
|---|---|
| **actionlint** | `test.yaml:19:13` (if: キーの行) |
| **seiton** | `if_cond_always_true.yaml:20:11` (値の行) |
| **原因** | seiton が `if:` の値の位置を報告するのに対し、actionlint は `if:` キーの位置。multiline 値の場合にずれる。 |
| **対処** | if condition 検出時の位置を `if:` キーの位置に統一するか、値の開始位置を正確に報告。 |
| **優先度** | **中** |

### C-5: `webhook_checks` — `on.release does not support option: ` (空文字列)

| | 内容 |
|---|---|
| **seiton** | `on.release does not support option: ` — option 名 (`tags`) が表示されない |
| **actionlint** | `"tags" filter is not available for release event` |
| **原因** | パーサーが release イベントの `tags` を event option として認識できず、空文字列を渡している。`tags` は push イベント専用の filter key だが、release にも記述されたケース。 |
| **対処** | event option パーサーで `tags`/`branches` 等の filter key 名を正しく取得し、メッセージに含める。 |
| **優先度** | **高** — ユーザーがエラー原因を特定できない。 |

### C-6: `missing_required_keys` — parser と lint rule の重複診断

| | 内容 |
|---|---|
| **seiton** | `job 'test' requires runs-on (or uses)` が `[parse]` と `[job-structure]` の 2 件出力 |
| **原因** | parser-level と lint-level rule で同一チェックが重複。 |
| **対処** | parser で検出した場合は lint rule でスキップするか、重複排除ロジックを追加。 |
| **優先度** | **中** |

### C-7: `webhook_checks` — parser と glob-pattern rule の重複

| | 内容 |
|---|---|
| **seiton** | `paths/paths-ignore cannot be used together` と `activity type 'created'` が parser と glob-pattern の 2 箇所から出力 |
| **対処** | C-6 と同じアプローチ。 |
| **優先度** | **中** |

### C-8: `workflow_call_jobs` — 大量の重複診断

| | 内容 |
|---|---|
| **seiton** | `job 'job1' cannot have both uses and runs-on` が `[parse]` と `[job-structure]` と `[reusable-workflow]` の 3 箇所から出力 |
| **対処** | C-6 と同じ。 |
| **優先度** | **中** |

### C-9: `unexpected_mapping_values` — `timeout-minutes` の行番号 `14:0`

| | 内容 |
|---|---|
| **seiton** | `unexpected_mapping_values.yaml:14:0: error [parse] job 'test' step[1] timeout-minutes must be number or expression` |
| **actionlint** | `test.yaml:13:26` |
| **原因** | col が 0 になっているのはパーサーが正しい位置を取得できていない。 |
| **対処** | timeout-minutes フィールドのパース時の位置取得を修正。 |
| **優先度** | **高** |

---

## 4. E: スコープ外 (対応不要)

| ファイル | actionlint チェック | 理由 |
|---|---|---|
| `pyflakes_integration` | pyflakes 連携 (3件) | 外部ツール連携。seiton のスコープ外。 |
| `shellcheck_integration` | shellcheck 連携 (2件) | 外部ツール連携。seiton のスコープ外。 |

---

## 5. 対処優先度まとめ

### P0: 最優先 (メッセージ/位置の品質問題)

| ID | 内容 | 対処 |
|---|---|---|
| C-1 | broken_yaml 行番号ずれ | パースエラー位置の変換修正 |
| C-5 | webhook option 名が空 | event option パーサー修正 |
| C-9 | timeout-minutes col:0 | パース位置取得修正 |
| C-6,C-7,C-8 | parser/lint 重複診断 | 重複排除ロジック追加 |

### P1: 高優先度 (検出漏れ — セキュリティ/頻出バグ)

| ID | 内容 | 対処 |
|---|---|---|
| A-12 | hardcoded credentials | 新チェック: password が secrets 経由でないことを検出 |
| A-23 | untrusted input (with.script, object filter) | TemplateInjectionRule 拡張 |
| A-9 | outdated action runner (node16) | 新ルール: OutdatedActionRunner |
| A-3 | matrix property 未定義 | ExprUndefinedVarRule 強化 |
| A-4 | needs property 未定義 | needs コンテキスト型構築 |
| A-5 | steps output 未定義 | steps コンテキスト型構築 |
| A-19 | popular action output 未定義 | カタログに outputs 追加 |
| A-1 | comparison `>` 型不一致 | 式分析拡張 |
| A-11 | glob パターン検証強化 | `^` 文字、`*+` パターン、`.`/`..` パス |

### P2: 中優先度

| ID | 内容 | 対処 |
|---|---|---|
| A-2 | builtin_func matrix object property | matrix 型推論強化 |
| A-6 | runner context availability | context availability テーブル修正 |
| A-7 | cron timezone 検出漏れ | schedule パーサー確認 |
| A-10 | env に string 型展開 | env 式型チェック |
| A-14 | needs 重複 | NeedsGraphRule 拡張 |
| A-15 | local action output 未定義 | LocalActionOutputsRule 新設 |
| A-18/A-22 | template 展開時 object/array 警告 | 式型チェック追加 |
| A-25 | workflow_call required + default | WorkflowCallInputDefaultRule 拡張 |
| A-26 | workflow_dispatch type:text 位置ずれ | パーサー確認 |
| A-27/A-28 | inputs/secrets property 未定義 | 型推論強化 |
| C-4 | if multiline 行番号ずれ | 位置報告改善 |

### P3: 低優先度

| ID | 内容 | 理由 |
|---|---|---|
| A-8 | deprecated inputs | カタログ依存 |
| A-13 | Docker empty tag | 既に別メッセージで検出 |
| A-16 | matrix exclude 値不一致 | 実害少 |
| A-29 | YAML anchor 高度検証 | VYaml 制約 |
| C-2 | comparison severity | ポリシー判断 |

---

## 6. 実装アクションプラン

### Phase 1: 品質修正 (P0)

1. **パースエラー位置の修正** — VYaml エラーの行/列を正確に seiton 座標に変換
2. **event option パーサー** — `tags`/`branches` 等の filter key 名をメッセージに含める
3. **重複診断排除** — parser 診断と lint rule 診断の重複を排除するメカニズムの導入

### Phase 2: 検出強化 (P1)

4. **hardcoded credentials 検出** — container/services の password が `${{ secrets.* }}` でない場合を警告
5. **template injection 拡張** — `with.script` (github-script)、object filter (`.*`) の untrusted 推論
6. **outdated action runner** — runs.using が node16/node12 の action を検出
7. **expression 型チェック強化** — `>`, `>=`, `<`, `<=` の型不一致、template 展開時の object/array 警告
8. **glob パターン検証強化** — ref 名禁止文字、連続特殊文字、`.`/`..` パス
9. **コンテキスト型推論** — matrix/needs/steps/inputs の property-level 未定義チェック
10. **popular action outputs** — カタログに outputs 情報を追加し検証

### Phase 3: 精度向上 (P2)

11. context availability テーブル補完
12. schedule timezone パーサー確認
13. env 式型チェック
14. needs 重複チェック
15. local action outputs
16. workflow_call required + default
17. workflow_dispatch input 型推論

---

## 7. 既検出パターン一覧 (B: 両方で検出 OK)

以下は actionlint と seiton の両方で適切に検出できているパターン:

| ファイル | actionlint チェック | seiton ルール | 備考 |
|---|---|---|---|
| broken_yaml | YAML parse failure | [parse] | 位置ずれあり (C-1) |
| builtin_func_special_checks | format placeholder (2件) | [parse] | OK |
| builtin_func_special_checks | fromJSON 不正 | [parse] | OK |
| comparison_strict_checks | object == string | [parse] | severity 差 (C-2) |
| contexts_and_builtin_funcs | undefined context (5件) | [parse] | OK — 全 5 件検出 |
| contexts_special_functions_availability | env not available、success() scope | [parse] + [expr-undefined-var] | OK — 2/3 件検出 |
| cron_schedule_check | invalid cron (2件) | [schedule-event] | OK |
| cyclic_deps_needs | cyclic needs | [needs-graph] | OK |
| dangling_alias | unknown anchor | [parse] | OK |
| deprecated_workflow_commands | set-output deprecated | [deprecated-commands] | OK |
| env_var_names | invalid env name (2件) | [env-var] | OK |
| expression_syntax_error | expression errors (4件) | [parse] | OK |
| id_naming_convention | invalid job/step ID (4件) | [id-naming] | OK |
| if_cond_always_true | always true/false (4件) | [if-cond] | OK (位置ずれ C-4) |
| invalid_action_format | missing ref (3件) | [unpinned-uses] | OK (メッセージ差あるが機能的に同等) |
| invalid_ids_in_needs | unknown job in needs | [needs-graph] | OK (1/2 件) |
| job_step_ids_duplicate | duplicate step ID、duplicate job key | [parse] + [id-naming] | OK |
| local_action_inputs | missing required、unknown input | [local-action-inputs] | OK |
| main | unexpected key、untrusted input、unknown input、undefined matrix、receiver type | [parse] + [template-injection] + [popular-action-inputs] + [expr-undefined-var] | 5/7 件検出 |
| matrix_checks | duplicate value、unknown axis in exclude | [matrix] | 2/3 件検出 |
| missing_required_keys | missing runs-on、duplicate matrix key | [parse] + [job-structure] | OK (重複あり C-6) |
| permissions | invalid permissions (4件) | [permissions] | OK |
| popular_action_inputs | missing required、unknown input | [popular-action-inputs] | OK |
| runner_label_check | unknown label (1/3件) | [runner-label] | 部分的 |
| runner_label_conflict | conflicting labels | [runner-label] | OK |
| shell_name_validation | invalid shell (4件) | [shell-name] | OK — 全 4 件検出 |
| type_checks | property access type、undefined property、receiver type (3/4件) | [parse] | OK |
| unexpected_keys | unexpected job key、unexpected step key | [parse] | OK |
| unexpected_mapping_values | type mismatch (3件) | [parse] | OK (位置ずれ C-9) |
| untrusted_input | untrusted input (1/3件) | [template-injection] | 部分的 |
| webhook_checks | unexpected key、paths conflict、activity type、unknown event (5件) | [parse] + [glob-pattern] | OK (重複あり C-7、メッセージ壊れ C-5) |
| workflow_call_definitions | invalid type、non-numeric default (2/3件) | [parse] + [workflow-call-input-default] | OK |
| workflow_call_jobs | reusable workflow issues (4件) | [parse] + [reusable-workflow] + [job-structure] | OK (重複あり C-8) |
| workflow_dispatch_input_types | choice no options、bad default (4/9件) | [parse] + [dispatch-inputs] | 部分的 |
| workflow_inputs_secrets_types | undefined secrets property (1/2件) | [expr-undefined-var] | 部分的 |
| yaml_anchors | unexpected credentials key | [parse] | OK (2/3件) |

## Implementation Priority Roadmap

### Verification Requirements (All Phases)

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
