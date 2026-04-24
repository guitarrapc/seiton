# Seiton vs Actionlint Detection Gap Analysis

> Generated from comparison of `.references/actionlint/testdata/examples/` against `seiton check` output.
> Each example file has a `.yaml` (workflow) and `.out` (expected actionlint errors).

---

## Summary

| Category | Count |
|---|---|
| Actionlint detects, Seiton does NOT (detection gaps) | 30+ patterns across 35 examples |
| Seiton detects but with wrong position/message/false positive | 8 patterns |
| Fully covered by Seiton | 15 examples |
| Intentionally out of scope (external tools) | 2 examples (pyflakes, shellcheck) |

---

## Part 1: Detection Gaps (Actionlint Finds, Seiton Does Not)

### P0 — Must Fix (False Positives / Wrong Results Users Will See)

#### 1. `contextual_matrix_values` — False positive on `matrix.npm` + missing real errors

- **Actionlint**: Detects `matrix.platform`, `matrix.dev`, `matrix.os` (in test2 without matrix) as undefined.
- **Seiton**: Falsely flags `matrix.npm` as undefined. `npm` IS defined in the `include:` section and should be valid. The real errors (`platform`, `dev`, `os` in test2) are all missed.
- **Rule**: `expr-undefined-var`
- **Root cause**: ExprUndefinedVarRule does not merge `include:`-only axes into the matrix type, and does not scope `matrix.*` availability to jobs that actually define a matrix.
- **Fix**: When building the matrix property set for a job, union all keys from `include:` rows with the main axes. For jobs without a matrix, `matrix.*` should resolve to empty.

#### 2. `expand_object` — False positive (both steps flagged, only one is wrong)

- **Actionlint**: Only flags step[2] (`env: ${{ matrix.env_string }}`) because the value is a string, not an object.
- **Seiton**: Flags BOTH step[1] (`env: ${{ matrix.env_object }}`) and step[2] as `env must be mapping`. Step[1] uses `matrix.env_object` which IS an object — this is a false positive.
- **Rule**: `parse` (parser-level check)
- **Root cause**: Parser treats any `${{ ... }}` in `env:` as invalid non-mapping, without considering that the expression might evaluate to a mapping.
- **Fix**: When `env:` value is a single `${{ ... }}` expression, allow it (defer to runtime type resolution). Only error if the value is a plain string or literal non-mapping.

#### 3. `permissions` — Error on wrong line (points to comment)

- **Actionlint**: `test.yaml:4:14: "write" is invalid for permission for all the scopes`
- **Seiton**: `permissions.yaml:3:54` — points to column 54 of line 3, which is inside a COMMENT (`# ERROR: Available values for whole permissions are "write-all"...`), not on the actual `permissions: write` value on line 4.
- **Rule**: `permissions`
- **Root cause**: The diagnostic position is incorrectly resolved to the comment text rather than the YAML scalar node.
- **Fix**: Ensure PermissionsRule uses the span of the actual scalar value node, not surrounding text.

#### 4. `permissions` — Missing `unknown scope "check"` and `models: write` restriction

- **Actionlint**: Detects unknown scope `check` (line 11) and `models` scope only allows `read`/`none` (line 15).
- **Seiton**: Detects `write` scalar and `readable` value but misses the unknown scope and write-restricted scope.
- **Rule**: `permissions`
- **Root cause**: PermissionsRule may not have the full scope allowlist or per-scope access restrictions.
- **Fix**: Add `check` → unknown scope error; validate per-scope allowed values (e.g., `models` only accepts `read`/`none`).

---

### P1 — High Priority (Common Checks Seiton Must Have)

#### 5. Expression type checking — comparison type mismatch

- **Example**: `comparison_strict_checks`
- **Actionlint**: `"object" value cannot be compared to "string" value with "==" operator`, `"bool" value cannot be compared to "number" value with ">" operator`
- **Seiton**: No detection.
- **Rule**: Needs new check in expression semantic analysis
- **Root cause**: Seiton's expression evaluator does not perform cross-type comparison validation.
- **Fix**: Add type-aware comparison checking in expression semantic analysis. When both sides of `==`, `!=`, `<`, `>`, `<=`, `>=` have known concrete types that are incompatible, emit a warning.

#### 6. Expression type checking — object/array/null in `${{ }}` template

- **Example**: `not_persistent_matrix_values` (array in template), `type_checks` (env object in template)
- **Actionlint**: `object, array, and null values should not be evaluated in template with ${{ }}`
- **Seiton**: No detection.
- **Rule**: Needs new check
- **Root cause**: Seiton doesn't validate whether the inferred type of an expression is safe for string interpolation.
- **Fix**: When an expression in `${{ }}` resolves to an object, array, or null type, emit a warning. String, number, and boolean are safe.

#### 7. Expression type checking — string dereference as object

- **Example**: `type_checks` line 11, `main` line 22
- **Actionlint**: `receiver of object dereference "owner" must be type of object but got "string"`, `receiver of object dereference "permissions" must be type of object but got "string"`
- **Seiton**: No detection.
- **Rule**: Needs new check in expression semantic analysis
- **Fix**: When `.property` access is applied to a type known to be `string`, emit an error.

#### 8. Contextual `needs.*` output validation

- **Example**: `contextual_needs_object`
- **Actionlint**: Detects `needs.prepare.outputs.prepared` not available (job has no `needs: [prepare]`), `needs.install.outputs.foo` undefined, `needs.some_job` undefined, `needs.build.outputs.built` not available.
- **Seiton**: No detection.
- **Rule**: Needs new rule or enhancement to `expr-undefined-var`
- **Root cause**: Seiton does not build per-job `needs` scope from job dependency graph and validate output property access against declared outputs.
- **Fix**: Build needs scope per job from `needs:` array. Validate that `needs.<jobid>` exists as a dependency, and that `needs.<jobid>.outputs.<name>` matches declared `outputs:` of that job.

#### 9. Contextual `steps.*` output validation

- **Example**: `contextual_steps_outputs`, `local_action_outputs`, `popular_action_outputs`
- **Actionlint**: Detects step outputs referenced before the step has run, outputs referenced from a different job, and typos in output names.
- **Seiton**: No detection.
- **Rule**: Needs new rule or enhancement to `expr-undefined-var`
- **Root cause**: Seiton does not track step execution order or build per-step output sets.
- **Fix**: Build an ordered step output registry per job. Validate that `steps.<id>` exists and has been executed before the current step. Validate output property names against known outputs (from action metadata or `$GITHUB_OUTPUT` patterns).

#### 10. Popular action required input validation

- **Example**: `popular_action_inputs`
- **Actionlint**: `missing input "key" which is required by action "actions/cache@v4"`
- **Seiton**: Only detects unknown input `keys`, but not missing required `key`/`path`.
- **Rule**: `popular-action-inputs`
- **Root cause**: PopularActionInputsRule validates unknown inputs but doesn't check for missing required inputs.
- **Fix**: Add required-input validation to PopularActionInputsRule. When a popular action is used, check that all required inputs are provided in `with:`.

#### 11. Glob pattern syntax validation

- **Example**: `glob`
- **Actionlint**: 4 checks — invalid `^` character, `+` after non-special, range `[9-1]` reversed, `.`/`..` path component.
- **Seiton**: No detection for any of these.
- **Rule**: `glob-pattern` (rule exists but doesn't validate glob syntax)
- **Root cause**: GlobPatternRule validates event option legality, types, and mutual exclusion, but does not parse/validate the actual glob pattern strings.
- **Fix**: Add glob pattern string validation to GlobPatternRule. Check: invalid ref characters (`^`, `:`, `~`, `[`, `?`, `*`, spaces), invalid escape sequences, reversed character ranges, `.`/`..` path segments.

#### 12. Matrix duplicate value and exclude mismatch

- **Example**: `matrix_checks`
- **Actionlint**: duplicate value `14` in matrix `node`, value `13` in exclude doesn't match combinations.
- **Seiton**: Only detects unknown axis `platform` in exclude (1 of 3).
- **Rule**: `matrix`
- **Root cause**: MatrixRule checks for unknown axes in exclude but doesn't check for duplicate values within an axis or exclude values that don't match any combination.
- **Fix**: Add duplicate-value detection within each axis array. Add exclude-value validation: for each exclude row, check that each value matches at least one value in the corresponding axis.

#### 13. `workflow_call` input default type validation

- **Example**: `workflow_call_definitions`
- **Actionlint**: `input "port" typed as number but default ":1234" cannot be parsed`, `input "path" has default but is also required`
- **Seiton**: Only detects invalid input type `object` (1 of 3).
- **Rule**: Needs enhancement to parser or new rule
- **Root cause**: Parser validates input type keyword but not default-vs-type consistency or required+default conflict.
- **Fix**: Validate that `default:` value is compatible with declared `type:`. Warn when `required: true` and `default:` is set (default will never be used).

#### 14. `workflow_call`/`workflow_dispatch` expression-level input property validation

- **Example**: `workflow_dispatch_input_types` (massage typo, bool/number indexing), `workflow_inputs_secrets_types` (uri typo, credentials typo)
- **Actionlint**: Detects typos in `inputs.*` and `secrets.*` property access against declared inputs/secrets.
- **Seiton**: No detection.
- **Rule**: Needs enhancement to `expr-undefined-var` or expression evaluator
- **Root cause**: Expression evaluator does not resolve `inputs.*` and `secrets.*` properties against the workflow's declared inputs/secrets.
- **Fix**: When `on.workflow_call` or `on.workflow_dispatch` inputs/secrets are declared, build a typed property map and validate all `inputs.*` / `secrets.*` expression accesses against it.

---

### P2 — Medium Priority

#### 15. If condition "always true" for trailing characters around `${{ }}`

- **Example**: `if_cond_always_true`
- **Actionlint**: Detects `${{ expr }}\n`, `${{ expr }} ` (trailing space), `${{ expr }} && ${{ expr }}` as always true because GitHub coerces the non-empty string to true.
- **Seiton**: Detects `if: false` as always false ✅. Reports `${{ }} && ${{ }}` as "syntax errors" rather than "always true". Does not detect trailing whitespace/newline cases.
- **Rule**: `if-cond`
- **Fix**: Detect when an if condition contains `${{ }}` wrapped by extra characters (including newlines, spaces, `&&`, etc.). Report as "always evaluated to true" with explanation.

#### 16. Duplicate job ID in needs array

- **Example**: `invalid_ids_in_needs`
- **Actionlint**: `job ID "BAR" duplicates in "needs" section`
- **Seiton**: Not detected (only unknown job reference).
- **Rule**: `needs-graph`
- **Fix**: Check for case-insensitive duplicates in the `needs:` array of each job.

#### 17. Runner label conflict

- **Example**: `runner_label_conflict`
- **Actionlint**: `label "windows-latest" conflicts with label "ubuntu-latest"`
- **Seiton**: Not detected.
- **Rule**: `runner-label`
- **Fix**: When multiple runner labels are specified in `runs-on:` array, check for OS-family conflicts (Ubuntu vs Windows vs macOS).

#### 18. Unexpected mapping value type validation

- **Example**: `unexpected_mapping_values`
- **Actionlint**: `fail-fast: off` not boolean, `max-parallel: 1.5` not integer, `timeout-minutes: "two minutes"` not float.
- **Seiton**: Only detects `max-parallel` (1 of 3).
- **Rule**: `parse`
- **Fix**: Validate `fail-fast` is boolean (true/false only), `timeout-minutes` is numeric (integer or float).

#### 19. OS-specific shell validation

- **Example**: `shell_name_validation`
- **Actionlint**: `powershell` invalid on macOS/Linux, `sh` invalid on Windows.
- **Seiton**: Validates shell names globally but does not consider OS-specific availability.
- **Rule**: `shell-name`
- **Fix**: Cross-reference shell name with the job's `runs-on` labels. When OS can be inferred (e.g., `ubuntu-*` → Linux, `windows-*` → Windows, `macos-*` → macOS), validate shell availability per OS.

#### 20. `format()` excess arguments

- **Example**: `builtin_func_special_checks`
- **Actionlint**: `format string "{0}{1}" does not contain placeholder {2}. remove argument which is unused`
- **Seiton**: Not detected (only detects missing arguments).
- **Rule**: `parse` (expression evaluator)
- **Fix**: When evaluating `format()`, check that all argument indices are referenced in the format string.

#### 21. Broken JSON in `fromJSON()`

- **Example**: `builtin_func_special_checks`
- **Actionlint**: `broken JSON string is passed to fromJSON() at offset 23`
- **Seiton**: Not detected.
- **Rule**: Needs expression evaluator enhancement
- **Fix**: When `fromJSON()` is called with a string literal, validate it as JSON.

#### 22. `fromJSON()` property validation

- **Example**: `builtin_func_special_checks`
- **Actionlint**: `property "mac" is not defined in object type {linux: string; win: string}`
- **Seiton**: Not detected.
- **Rule**: Needs expression evaluator enhancement
- **Fix**: When `fromJSON()` is called with a literal string, parse the JSON and use the resulting type for property validation.

#### 23. Runner context not available in matrix scope

- **Example**: `contexts_special_functions_availability`
- **Actionlint**: `context "runner" is not allowed here` (in matrix strategy section)
- **Seiton**: Not detected (detects `env` and `success()` but not `runner`).
- **Rule**: `expr-undefined-var` or `parse`
- **Fix**: Validate context availability more comprehensively. In `strategy.matrix` scope, only `github`, `inputs`, `needs`, `vars` are available.

#### 24. Cron timezone validation

- **Example**: `cron_schedule_check`
- **Actionlint**: `invalid timezone "Asia/Somewhere"`
- **Seiton**: Detects field count and frequency but not invalid timezone.
- **Rule**: `schedule-event`
- **Fix**: Validate `timezone:` value against IANA timezone database.

#### 25. Reusable workflow output property validation

- **Example**: `reusable_workflow_outputs`
- **Actionlint**: `property "imagetag" is not defined in object type {image_tag: string}`
- **Seiton**: Not detected.
- **Rule**: Needs enhancement
- **Fix**: When `${{ jobs.<id>.outputs.<name> }}` references a job output in workflow_call outputs, validate the output name against the job's declared outputs.

#### 26. Unused YAML anchor detection

- **Example**: `yaml_anchor_usage`
- **Actionlint**: `anchor "credentials" is defined but not used`
- **Seiton**: Not detected (YAML parse failure instead).
- **Rule**: `parse` or new rule
- **Status**: Low priority informational check.

#### 27. Recursive alias detection

- **Example**: `yaml_anchor_usage`
- **Actionlint**: `recursive alias "recursive" is found`
- **Seiton**: YAML parse failure (VYaml may not handle this).
- **Status**: Depends on VYaml capability.

#### 28. `env:` section expression type validation

- **Example**: `yaml_anchor_usage`
- **Actionlint**: `"env" section is alias node but mapping node is expected`, `expecting a single ${{...}} expression or mapping value for "env" section, but found plain text node`
- **Seiton**: Not detected.
- **Rule**: `parse`
- **Fix**: Validate that `env:` at workflow/job/step level is either a mapping or a single `${{ }}` expression that resolves to an object.

---

### P3 — Low Priority / Out of Scope

#### 29. External tool integration (pyflakes, shellcheck)

- **Examples**: `pyflakes_integration`, `shellcheck_integration`
- **Status**: Intentionally out of scope. Seiton does not integrate external linting tools.

#### 30. Outdated action runner detection

- **Example**: `detect_outdated_popular_actions`
- **Actionlint**: `the runner of "actions/checkout@v3" action is too old`
- **Seiton**: Not detected.
- **Status**: Low priority. Users should update actions anyway.

#### 31. Deprecated popular action input detection

- **Example**: `deprecated_inputs`
- **Actionlint**: `avoid using deprecated input "fail_on_error" in action "reviewdog/action-actionlint@v1"`
- **Seiton**: Not detected.
- **Status**: Could enhance popular-action-inputs to include deprecation metadata.

#### 32. Deep action metadata validation (branding, description, file existence)

- **Example**: `action_metadata_syntax_validation`
- **Actionlint**: Validates branding colors/icons, description presence, JS entry file existence.
- **Seiton**: Only validates `runs.using` value.
- **Status**: Can be incrementally enhanced in `local-action-inputs` rule.

#### 33. Expression string literal delimiter

- **Example**: `expression_syntax_error`
- **Actionlint**: `got unexpected character '"' while lexing expression... only single quotes are available`
- **Seiton**: Not detected (expression parser may silently skip).
- **Fix**: Improve expression lexer error messages for double-quote strings.

---

## Part 2: Seiton Issues (Detects but Wrong Position/Message/Content)

### Issue A: YAML parse error always points to line 1:1

- **Examples**: `broken_yaml`, `dangling_alias`, `yaml_anchor_usage`
- **Problem**: When VYaml reports a parse failure, seiton always emits the diagnostic at `1:1` (file start) even though the error message contains the actual line/column.
- **Root cause**: The parse failure handler doesn't extract position from the VYaml exception message.
- **Fix**: Parse VYaml's `Line:` and `Col:` from the exception message and use them as the diagnostic position.

### Issue B: `expand_object` — False positive on object-type expression in `env:`

- **Problem**: Seiton flags `env: ${{ matrix.env_object }}` as "env must be mapping" but this is an object expression that evaluates to a mapping at runtime.
- **(Same as P0 #2 above)**

### Issue C: `if_cond_always_true` — "syntax errors" instead of "always true"

- **Problem**: For `${{ expr }} && ${{ expr }}`, seiton reports "step if condition contains syntax errors" which is misleading. The condition is syntactically valid but semantically always true.
- **Fix**: Detect the `${{ }} text ${{ }}` pattern and report as "always evaluated to true because extra characters are around ${{ }}".

### Issue D: `contextual_matrix_values` — False positive on `include:`-only axes

- **Problem**: `matrix.npm` is valid (defined only in `include:`) but seiton flags it.
- **(Same as P0 #1 above)**

### Issue E: `webhook_checks` — Activity type error points to wrong event

- **Problem**: Seiton reports `on.issues.types contains unsupported activity type: created` at line 11:9 which is the `release:` key, not the `issues:` section.
- **Root cause**: Event type validation may be associating the error with the wrong YAML node.
- **Fix**: Ensure the diagnostic span points to the actual `types:` value within the correct event section.

### Issue F: `runner_label_check` — Matrix-expanded labels not validated

- **Problem**: When `runs-on: ${{ matrix.runner }}`, seiton cannot validate the labels because they're expression-based. Actionlint resolves matrix values and validates each expanded label.
- **Root cause**: RunnerLabelRule only validates literal labels, not expression-expanded ones.
- **Fix**: When `runs-on` is `${{ matrix.runner }}` and the matrix has a `runner` axis with literal values, resolve and validate each value.

### Issue G: `shell_name_validation` — Custom shell template detected as invalid

- **Problem**: `shell: 'perl {0}'` is a valid custom shell template per GitHub Actions docs, but seiton flags it as invalid.
- **Fix**: Allow custom shell names that contain `{0}` placeholder as valid custom shell templates.

### Issue H: `action_metadata_syntax_validation` — Limited local action validation

- **Problem**: Actionlint validates 6 aspects of local action metadata; seiton only validates `runs.using`.
- **Fix**: Incrementally add validation for: `description` required, `runs.main` file exists, branding color/icon values.

---

## Part 3: Fully Covered Examples

These examples are fully covered by seiton (all actionlint errors detected):

| Example | Seiton Rule(s) |
|---|---|
| `contexts_and_builtin_funcs` | parse (expr evaluator) |
| `cyclic_deps_needs` | needs-graph |
| `dangling_alias` | parse (YAML failure) |
| `deprecated_workflow_commands` | deprecated-commands |
| `env_var_names` | env-var |
| `hardcoded_credentials` | credentials |
| `id_naming_convention` | id-naming |
| `invalid_action_format` | unpinned-uses, unpinned-image |
| `job_step_ids_duplicate` | id-naming, parse |
| `local_action_inputs` | local-action-inputs |
| `missing_required_keys` | parse, job-structure |
| `unexpected_keys` | parse |
| `webhook_checks` | parse, glob-pattern |
| `workflow_call_jobs` | parse, job-structure, reusable-workflow |
| `yaml_anchors` | parse, credentials |

---

## Implementation Priority Roadmap

### Phase 1: Fix False Positives & Wrong Positions (P0)

1. Fix `permissions` error position → points to YAML value node, not comment
2. Fix `expand_object` false positive → allow `${{ }}` expression in `env:`
3. Fix `contextual_matrix_values` → merge `include:` keys into matrix type
4. Fix YAML parse error position → extract line/col from VYaml exception
5. Fix `webhook_checks` activity type error → associate with correct event
6. Fix `if_cond_always_true` message → "always true" not "syntax errors"
7. Fix `shell_name_validation` → allow custom `{0}` shell templates

### Phase 2: Core Expression Type System (P1, high impact)

8. Add comparison type mismatch checking (#5)
9. Add object/array/null in `${{ }}` template warning (#6)
10. Add string-as-object dereference checking (#7)
11. Add `inputs.*`/`secrets.*` property resolution from workflow declarations (#14)
12. Add `format()` excess argument checking (#20)
13. Add `fromJSON()` literal validation (#21, #22)

### Phase 3: Contextual Validation (P1, medium-high impact)

14. Add `needs.*` output contextual validation (#8)
15. Add `steps.*` output contextual validation (#9)
16. Add popular action required input checking (#10)
17. Add reusable workflow output property validation (#25)
18. Add runner context availability in matrix scope (#23)

### Phase 4: Pattern Validation (P1-P2)

19. Add glob pattern syntax validation (#11)
20. Add matrix duplicate value + exclude mismatch (#12)
21. Add workflow_call input default validation (#13)
22. Add if condition "always true" trailing char detection (#15)
23. Add needs array duplicate detection (#16)
24. Add runner label conflict detection (#17)
25. Add fail-fast/timeout-minutes type validation (#18)
26. Add OS-specific shell validation (#19)
27. Add cron timezone validation (#24)
