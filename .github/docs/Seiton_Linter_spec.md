# Seiton Linter Specification

> This document is language-neutral — it specifies WHAT the Linter does, not HOW a specific implementation achieves it. Defines the linter contract for rule execution, lint configuration, and diagnostic processing. For C#-specific implementation details, see `Seiton_Linter_csharp_spec.md`, For Go-specific implementation details, see `Seiton_Linter_go_spec.md`. Parser and linter behavior are specified in `Seiton_Parser_spec.md` and `Seiton_Linter_spec.md`.

> **Cross-document rule**: This spec is the source of truth. When revised, also review and update `Seiton_Linter_csharp_spec.md`, `Seiton_Linter_go_spec.md` for consistency.


---

## 1. Scope

This specification defines linter responsibilities after parser output is available.

In scope:

- Rule execution model over workflow AST
- Rule traversal hooks and ordering
- Lint configuration surface
- Rule diagnostics aggregation and final output processing

### 1.1 Rule Inclusion Policy

The linter rule catalog must stay within Seiton's linting scope. Rule selection uses the following criteria.

- **Keep** rules that detect mistakes, security risks, incompatibilities, spec traps that are easy to misunderstand, or strongly deprecated APIs/features with concrete operational downside.
- **Allow as opt-in informational** rules only when the downside is still concrete and explainable, even if the rule is advisory rather than correctness-critical.
- **Exclude** rules whose value primarily depends on naming taste, readability preferences, UI presentation, alternative tool preference, or team/culture-specific style.

Implications:

- Default-on rules should normally satisfy the first category.
- Opt-in local/online rules may satisfy either the first category or the second category, but not the third.
- If a rule drifts into the third category, it should be removed rather than kept as a default or opt-in rule.

Out of scope:

- YAML structural parsing algorithms
- AST data model definitions
- Parser-level data extraction for suppression comments (linter consumes parsed suppression directives)

---

## 2. Entry Point Contract

```
Check(utf8Yaml, filePath) -> owned lint result
```

High-level behavior:

1. Classify input document kind (workflow or action-metadata) using parser classification contract (`Seiton_Parser_spec.md` §1.1.2).
2. Call parser entrypoint `Parse(utf8Yaml, filePath)` for the finalized kind.
3. If parser has fatal error or no AST for finalized kind, return parser diagnostics as lint result.
4. Build active rule set for finalized kind.
5. Traverse AST and invoke rule callbacks.
6. Collect rule diagnostics.
7. Sort, deduplicate, and filter diagnostics.
8. Return final `LintResult`.

Document-kind routing:

- If finalized kind is `action-metadata`, the linter traverses the action-metadata AST (`VisitActionMetadataPre` → `runs.steps` via `VisitStep` → `VisitActionMetadataPost`). Rules opt in via document-kind declaration; workflow-only rules are skipped for this input kind.
- Workflow inputs use the workflow traversal sequence in §4.2; action-metadata inputs do not receive `VisitWorkflowPre`/`VisitEvent`/`VisitJobPre`/`VisitJobPost`.

### 2.1. Multi-File Execution Model

`Check` processes a single file and is **safe for per-file parallel execution** under these constraints:

- Each concurrent invocation must use its own engine instance (no shared mutable state between workers).
- Diagnostics returned from each invocation must be owned by the caller (copies, not references into engine-internal storage).
- Final output order must be **deterministic**: diagnostics are aggregated in input-file order regardless of worker completion order.
- When a single file is provided, or when input is read from stdin, parallel dispatch is unnecessary; the implementation may use a sequential fast path.
- `Fix` (§8) remains **sequential-only**; it mutates files and must not be parallelized.

---

## 3. Parser/Linter Boundary

- Parser owns AST construction, expression syntax parsing, and expression-language intrinsic validation (function existence, arity, operator-local type checks).
- Linter owns rule execution, rule-originated diagnostics, and GitHub Actions context-dependent expression semantic validation.
- Linter must consume parser output and must not re-implement YAML structural parsing or expression syntax parsing.
- Rule suppression/exclusion is a linter concern and is specified in this document.

### 3.1 Expression Semantic Validation Ownership

The linter owns the following expression semantic checks via dedicated rules (primarily `expr-undefined-var`):

- Context availability validation by workflow position (which root contexts are valid at each YAML key)
- Function availability validation by workflow position (e.g., `hashFiles` at step-level only, status functions in `if:` only)
- Dynamic property existence and strictness (matrix, steps, needs, inputs)
- Workflow-site-aware type suitability (override-aware type inference)

The parser/linter integration surface includes an optional expression-artifact hook (AST, occurrence metadata, site information). When artifacts are attached to parser output, the linter consumes them without re-parsing; otherwise it falls back to its existing expression parse cache. The parser also provides expression-language intrinsic diagnostics (syntax errors, unknown functions, arity mismatches, operator-local type errors) that are independent of workflow context.

> **Implementation note**: Context-dependent expression validation (context availability, function availability, dynamic property, type suitability) is performed exclusively by the linter. The parser emits only expression-language intrinsic diagnostics. Deduplication handles any overlap in operator-local checks that both layers may emit.

---

## 4. Rule Execution Model

Canonical pass traversal sequences:

- **Workflow document:** `WorkflowPre -> Event -> JobPre -> Step -> JobPost -> WorkflowPost`
- **Action-metadata document:** `ActionMetadataPre -> Step (runs.steps) -> ActionMetadataPost`

### 4.1 Pass Hooks

A pass exposes the following callbacks:

- `VisitWorkflowPre(workflow)`
- `VisitWorkflowPost(workflow)`
- `VisitActionMetadataPre(actionMetadata)`
- `VisitActionMetadataPost(actionMetadata)`
- `VisitEvent(event)`
- `VisitJobPre(job)`
- `VisitJobPost(job)`
- `VisitStep(step)`

### 4.2 Traversal Order

**Workflow**

```
VisitWorkflowPre(workflow)
  for each event in workflow.On:
    VisitEvent(event)
  for each job in workflow.Jobs:
    VisitJobPre(job)
    for each step in job.Steps:
      VisitStep(step)
    VisitJobPost(job)
VisitWorkflowPost(workflow)
```

**Action metadata** (`action.yml` / `action.yaml`)

```
VisitActionMetadataPre(metadata)
  if metadata.Runs.Steps is present:
    for each step in metadata.Runs.Steps:
      VisitStep(step)
VisitActionMetadataPost(metadata)
```

### 4.3 Rule Contract

Rule extends pass callbacks and provides:

- `Id`
- `Name`
- `SetConfig(config)`
- `GetDiagnostics()`

Rules collect diagnostics internally during traversal and return them after traversal completes.

### 4.4 Normative Rule Catalog

All conforming implementations must include the following rule IDs in their default profile.

Column definitions:

- **Default**: `✓` = active with no config (local-AST); `✗` = opt-in only, requires `rules.<id>.enabled: true`.
- **Network**: `—` = local-AST rule, no network access required; `online` = requires network access, activated by `rules.<id>.enabled: true`.

> **Detail policy:** This table provides implementer-level behavior summaries only. For complete user-facing documentation (examples, remediation, edge cases), see [`docs/rules.md`](../../docs/rules.md).

| Rule ID | Default | Network | Required Behavior Summary |
|---|---|---|---|
| `job-structure` | ✓ | — | Validate core job shape constraints: `uses` is mutually exclusive with `steps`/`runs-on`, and each job requires either reusable-call form or executable form. |
| `reusable-workflow` | ✓ | — | Validate reusable workflow call semantics: `with`/`secrets` require `uses`, reusable-call jobs reject incompatible execution keys, and `./` or `$/` same-repository calls validate contracts when statically resolvable. |
| `local-action-inputs` | ✓ | — | Validate `./`, `../`, or `$/` local action `with:` inputs against parsed `action.yml`/`action.yaml`: unknown/missing/deprecated inputs, `runs.using` values, deprecated runners, description presence, env constraints, JS entry-point existence, and branding forwarding. |
| `permissions` | ✓ | — | Validate `permissions` value domain (scalar: `read-all`/`write-all`; scopes: `read`/`write`/`none`). Warn on scalar values recommending explicit per-scope mapping. |
| `popular-action-inputs` | ✓ | — | Validate known action input names against popular-action metadata. Suggest near-matches via edit distance. |
| `outdated-action-runner` | ✓ | — | Error when a popular action's `runs.using` runtime is deprecated (catalog-driven, checks against maintained deprecated-runtime set). |
| `unpinned-uses` | ✓ | — | Warn when remote `uses:` is not pinned to a full commit SHA; accept `$/` self-repository references as implicitly bound to the running commit and validate local action resolvability. |
| `unpinned-image` | ✓ | — | Warn when docker image references are not pinned by digest (`@sha256:<64-hex>`). |
| `dangerous-triggers` | ✓ | — | Warn when dangerous trigger events are used (built-in set plus additive config). |
| `job-permissions-required` | ✓ | — | Warn when a job omits explicit `permissions`. Auto-fix infers minimum scopes from known popular actions. Deliberately applies to reusable-workflow call jobs too: the caller-side `permissions` caps the callee's token, so callers declare scopes matched to the callee (strict `{}` default when unknown). |
| `needs-graph` | ✓ | — | Error on invalid `needs` graph: unknown targets and circular dependencies. Reports full cycle path (see §4.5). |
| `shell-name` | ✓ | — | Error when shell names are outside the supported set for workflow/job defaults and `run` steps. |
| `runner-label` | ✓ | — | Warn on unknown runner labels; error on conflicting OS families (including matrix-expanded labels). Recognizes bare self-hosted preset labels. |
| `runner-no-latest` | ✓ | — | Warn when moving `-latest` runner labels are used; prefer version-pinned labels. Built-in hosted labels are always detected, and `rules.runner-no-latest.fix-mapping` may add custom labels and attach replacement fixes. |
| `id-naming` | ✓ | — | Error when `job.id` or `step.id` contains invalid identifier characters. |
| `glob-pattern` | ✓ | — | Error on invalid event filter configuration: glob syntax errors, ref-name forbidden chars, path segment issues, unsupported options, and incompatible filter combinations. |
| `dispatch-inputs` | ✓ | — | Validate `on.workflow_dispatch.inputs` schema: types, required flags, choice options/defaults, boolean/number literals, duplicates, and max count. |
| `schedule-event` | ✓ | — | Validate `schedule` entries: five-field cron syntax, minimum interval, and timezone strings against IANA TZ database. |
| `workflow-call-input-default` | ✓ | — | Validate `on.workflow_call.inputs` defaults: required inputs reject defaults, type-match enforcement for boolean/number. |
| `deny-write-all` | ✓ | — | Error when workflow/job permissions use `write-all`. |
| `credentials` | ✓ | — | Warn on missing credentials for private registry images; error when `credentials.password` is a hardcoded literal. |
| `template-injection` | ✓ | — | Error when untrusted `github.event`-origin data is interpolated into `run`/`script` sinks. `env:` indirection is not flagged. |
| `unsound-contains` | ✓ | — | Detect bypassable `contains()` conditions (space-separated string lists). Error for user-controllable values; info for other contexts. Dot/bracket styles treated equivalently. |
| `bot-conditions` | ✓ | — | Warn when bot privilege checks (`==`) rely on spoofable actor contexts (name or ID). Exclusion checks (`!=`) are opt-in via `strict-detection: true` (info severity when enabled). Suppressed when a non-spoofable trigger-author context (`github.event.pull_request.user.login`/`.id`) comparison with the same literal and operator (`==` with `==`, `!=` with `!=`) is AND-conjoined. Also suppressed entirely when workflow triggers are not PR-only (e.g. push-only, schedule-only, or mixed triggers such as `push` + `pull_request` where `github.actor` is the only cross-trigger bot check). Uses generated `BotActors` dataset. |
| `expr-undefined-var` | ✓ | — | Error when expressions reference unavailable context roots. Builds strict per-job types for `matrix`, `steps`, `needs`, popular-action outputs, local action outputs, and local reusable workflow outputs. Remote reusable workflows treated as loose. |
| `run-env-context-direct-use` | ✓ | — | Error when `run:` directly references `${{ env.* }}`; shell variable expansion required (except no-expand contexts such as single-quoted shell strings and single-quoted heredocs unless strict mode is enabled). |
| `run-secrets-context-direct-use` | ✓ | — | Error when `run:` directly references `${{ secrets.* }}`; must map via `env`. Shell single-quoted no-expand contexts still emit diagnostics with manual-refactor guidance. |
| `run-inputs-context-direct-use` | ✓ | — | Error when `run:` directly references `${{ inputs.* }}` or `${{ github.event.inputs.* }}`; must map via `env` (except no-expand contexts such as single-quoted shell strings and single-quoted heredocs unless strict mode is enabled). |
| `secrets-whole-context-access` | ✓ | — | Error when an expression references the entire `secrets` context as an object rather than a specific key. |
| `checkout-persist-credentials` | ✓ | — | Warn when `actions/checkout` does not set `persist-credentials: false`. Legacy stores credentials in `.git/config`; v6+ in `$RUNNER_TEMP`. |
| `checkout-unsafe-pr` | ✓ | — | Warn when `actions/checkout` sets `allow-unsafe-pr-checkout` to `true` or to an expression in workflow steps or action-metadata composite steps. Missing, literal `false`, and other static non-`true` values are not reported. Literal `true` values are auto-fixable to `false`. |
| `artipacked` | ✓ | — | Detect credential leakage when checkout (without `persist-credentials: false`) is followed by upload-artifact with a dangerous path that can expose credentials. Covers root-like, parent-directory, workspace-expression, and `_temp` glob paths. Error for non-v6+ checkout; warning for v6+ parent-directory uploads reaching `$RUNNER_TEMP`. Legacy `.git` exclusions must exclude every reachable `.git/config`; v6+ suppression requires a recursive runner-temp subtree exclusion rather than a bare or shallow `_temp` exclusion. Independent of `checkout-persist-credentials`. |
| `background-steps` | ✓ | — | Validate `wait` / `cancel` references to background step ids (existence, forward-reference order, and background eligibility). Warn when more than 10 background steps may run concurrently in a job. Workflow jobs only. Case-insensitive id matching. Peak counting uses constant `if:` folding; non-constant conditions are excluded from the concurrent count. |
| `known-vulnerable-actions` | ✗ | `online` | Error when `uses:` resolves to known vulnerable action versions. |
| `impostor-commit` | ✗ | `online` | Error when a SHA-pinned `uses:` points to a commit not directly targeted by any branch HEAD or tag (fork-origin impostor detection). |
| `ref-confusion` | ✗ | `online` | Error when a symbolic ref in `uses:` is ambiguous (same name in tag and branch namespaces). |
| `stale-action-refs` | ✗ | `online` | Warn when SHA-pinned `uses:` are stale relative to release/tag mapping policy. |
| `deny-read-all` | ✓ | — | Error when workflow/job permissions use `read-all`. |
| `deny-inherit-secrets` | ✓ | — | Error when reusable-workflow call jobs use `secrets: inherit`. |
| `job-timeout-minutes-required` | ✓ | — | Error when executable jobs omit `timeout-minutes`. |
| `github-app-token-inputs` | ✓ | — | Error when `actions/create-github-app-token` is missing permission-limiting or repository-limiting inputs. |
| `workflow-secrets` | ✓ | — | Error when workflow-level `env` assigns `secrets.*`/`github.token` in multi-job workflows. |
| `job-secrets` | ✓ | — | Error when job-level `env` assigns `secrets.*`/`github.token` in multi-step jobs. |
| `action-shell-is-required` | ✓ | — | Error when a composite action `run` step omits explicit `shell`. |
| `cache-poisoning-trigger` | ✓ | — | Warn when write-capable `actions/cache` steps appear in workflows with low-trust triggers that can run on the default-branch cache scope (`pull_request_target`, `workflow_run`, `issue_comment`). |
| `self-hosted-runner-trigger` | ✓ | — | Warn when self-hosted runners are used with untrusted triggers. |
| `unredacted-secrets` | ✓ | — | Warn when secret-derived env vars are printed without redaction-safe handling. |
| `secrets-outside-env` | ✓ | — | Warn when `secrets.*` is referenced in non-`env` sinks (`if`, `uses`, reusable call inputs). |
| `matrix` | ✓ | — | Warn on malformed or suspicious matrix strategy configuration. |
| `env-var` | ✓ | — | Warn on risky or ambiguous environment variable naming/usage patterns. |
| `deprecated-commands` | ✓ | — | Warn on deprecated workflow commands (prefer environment-file mechanisms). |
| `if-cond` | ✓ | — | Warn on malformed, constant, or unsound `if` conditions. |
| `fake-ternary` | ✓ | — | Warn when fake ternary idioms (`cond && a || b`) are used in expressions. |
| `archived-uses` | ✓ | — | Warn when `uses:` references archived repositories. |
| `insecure-commands` | ✓ | — | Warn on unsafe command construction patterns in `run` scripts. |
| `overprovisioned-secrets` | ✓ | — | Warn when secret distribution scope is broader than required. |
| `forbidden-uses` | ✓ | — | Warn/Error when `uses:` references violate configured allow/deny patterns. |
| `ref-version-mismatch` | ✓ | — | Warn when symbolic ref/version intent mismatches resolved commit lineage. |
| `use-trusted-publishing` | ✓ | — | Warn when publishing flows do not use trusted publishing/OIDC-based provenance. |
| `if-expr-wrapper` | ✓ | — | Warn when `if:` is missing `${{ }}` wrapper; auto-fix for single-line scalars. |
| `unsound-condition` | ✓ | — | Warn when `if:` block scalars with fenced expressions have truthy-making newline chomping; auto-fix to `|-`/`>-`. |
| `unpinned-tools` | ✓ | — | Warn when known tool-setup actions use an unpinned tool version. Data-driven via `unpinned_tools.json`. |
| `concurrency-limits` | ✗ | — | Warn when workflows/jobs lack `concurrency` with `cancel-in-progress`. Skips reusable-only workflows. |

Rule set compatibility policy:

- Existing rule IDs are stable once published.
- Adding a new default rule requires this catalog to be updated in the same specification change.
- Removing or renaming a published rule ID is a breaking change and requires explicit migration guidance.
- `online` rules may be emitted by an opt-in post-lint audit entrypoint instead of the default local AST pass, but they still participate in shared rule-id, priority, suppression, and fixability catalogs.

### 4.5 Cross-Runtime Design Decisions

#### 4.5.1 Diagnostic Position Policy — `needs-graph` Cycle Detection

**Design decision**: Seiton reports cycle diagnostics at the **`needs` value position** (the specific dependency entry that closes the cycle), not at the job key position.

This is an intentional divergence from actionlint, which reports at the job key position. The rationale:

1. **Actionability**: The `needs` value is the exact YAML token the user must edit to break the cycle. Pointing at the job key requires the user to scroll down and find the relevant `needs` entry themselves, especially in large job definitions.
2. **Cycle path in message**: Seiton includes the full cycle path in the diagnostic message (e.g., `from -> to -> from`), compensating for the positional specificity by also giving the user the full picture. actionlint's message describes the cycle relationship but without a linear path representation.
3. **No natural "start"**: A dependency cycle has no inherent starting point. Reporting at the back-edge `needs` value is a deterministic choice tied to DFS traversal order, and it points to a directly editable location.

This policy applies only to cycle diagnostics. Other `needs-graph` diagnostics (unknown targets, duplicates) already report at the `needs` value position.

#### 4.5.2 Local Reference Path Resolution

**Design decision**: When a linted file path contains `/.github/` and a local `uses:` reference begins with `./.github/`, path resolution uses the repository root (the directory immediately above `/.github/`) as the base directory.

This applies regardless of whether the analyzed file lives under `.github/workflows/` or `.github/actions/` (composite action metadata). References that do not start with `./.github/` resolve relative to the analyzed file's directory (standard relative-path semantics).

A `uses:` reference beginning with `$/` always resolves from the repository root. The prefix is removed before filesystem lookup, and a path that would escape the repository root is not resolved. Because GitHub binds this syntax to the exact running commit, it is not subject to remote SHA-pinning diagnostics.

Rules that perform filesystem-backed local resolution (`unpinned-uses`, `reusable-workflow`, `local-action-inputs`, and resolvers that depend on the same helper) share this base-directory policy.

---

## 5. Lint Configuration Contract

Current baseline contract:

- Input YAML bytes and file path are available to rules through lint configuration.
- Rule-level option model (enable/disable/severity override) is supported by contract and may be partially implemented per runtime.

Exclusion/suppression contract:

- Rule exclusion is supported by:
  - configuration file rules
  - inline comment directives
- CLI-based exclusion is not part of Seiton's linter contract.

Default behavior and simplicity requirements:

- Full config is never required. All top-level config sections are optional.
- A minimal config containing only keys users want to change must be accepted.
- Omitted keys use built-in defaults.
- Unknown top-level keys/unknown rule IDs are configuration errors.
- Empty config file is valid and equivalent to default behavior.

Default values:

| Setting | Default |
|---|---|
| `rules.<rule-id>.enabled` | `true` (local-AST rules); `false` (online rules) |
| `rules.<rule-id>.severity` | rule-defined default |
| `rules.<rule-id>.<rule-specific-key>` | empty / no additions (see §5.8) |
| `exclusions` | empty |
| `fix.defaults.job-timeout-minutes` | `null` (no timeout auto-fix attachment) |
| `fix.pinning.enable-network` | `false` |
| `fix.pinning.min-age-days` | `14` |
| `fix.pinning.exclude-branches` | `main`, `master` |
| `fix.images.enable-network` | `false` |
| `fix.images.exclude-images` | includes `scratch` |
| `fix.images.exclude-tags` | `latest` |
| `network.on-error` | `skip` |
| `network.timeout-seconds` | `30` |
| `network.max-concurrency` | `min(4, logical processor count)` (at least **`1`**) |
| `network.github.ghes-api-url` | empty (github.com only) |
| `network.github.ghes-fallback` | `false` |
| `output.sort-order` | `location` |

Token resolution order (`SEITON_GITHUB_TOKEN` → `GITHUB_TOKEN`) is hardcoded and not configurable. This prevents malicious config files from redirecting token resolution to unintended environment variables.

### 5.1 Rule Identifier Contract

- Rule identifiers must use semantic IDs (kebab-case, e.g. `job-permissions-required`) as the sole accepted format in configuration and inline directives.
- Any non-semantic or unknown rule ID in config or inline directives is a configuration error.

### 5.2 Priority and Precedence

When both config and inline directives apply, precedence is fixed as follows:

1. Inline comment directive (highest)
2. Configuration file exclusion rule
3. Default rule behavior (lowest)

No CLI precedence level exists for exclusion.

### 5.3 File-Level Exclusion (Configuration)

Configuration file may define file-targeted exclusion entries with path globs.

- Path separator is normalized to `/` before matching.
- Glob matching is case-sensitive.
- Glob base is repository root (workspace root containing the analyzed file).
- Exclusion entries may include optional `jobs` condition (see §5.4).
- An exclusion entry with `file` only (both `rules` and `jobs` omitted, or `rules: ["*"]` with no `jobs`) suppresses the entire file's workflow diagnostics, including parser diagnostics.
- Exclusion entries that specify `rules` and/or `jobs` suppress only matching rule diagnostics; parser diagnostics remain visible.
- Configuration diagnostics raised while normalizing `rules` or `exclusions` are never suppressed, even when a file-level exclusion matches.

### 5.4 Job-Level Exclusion (Configuration)

- Job scoping uses `job.id` only via the `jobs` list field.
- Job `name` is not a matching key for exclusion.
- For reusable workflow call jobs (`uses:` at job level), matching is evaluated only against the caller workflow job in the current file.
- Seiton does not traverse into the referenced reusable workflow file for caller-file exclusion matching.
- When validating `jobs` entries in configuration, unknown `job.id` checks apply **only while linting a workflow file whose path matches the exclusion `file` glob**. Exclusions targeting other files must not produce configuration errors on unrelated workflows.
- Configuration diagnostics for invalid exclusion `jobs` entries report against the configuration file path when available.

### 5.5 Inline Exclusion Directive

Inline suppression supports file/job/step/next-line scopes.

- `disable-next-line` applies only to the immediately following YAML line.
- `disable-step` applies to diagnostics inside the next step item in the same `steps` sequence. Blank lines, non-`seiton` comments, and other inline suppression directives between the directive and the step item are ignored. Any intervening YAML content makes the directive invalid.
- `disable-job` applies to diagnostics inside the specified `job.id` scope.
- `disable-file` applies to all diagnostics in the current workflow file.

Inline suppression has the following constraints.

- A directive can target one or multiple rule IDs.
- Multiple rule IDs may be separated by commas and/or ASCII whitespace (spaces or tabs); semantic IDs (kebab-case) are required per §5.1.
- `disable-step` requires at least one rule ID. If no following step item exists in the same `steps` sequence, implementations must report a configuration diagnostic against the directive.
- `disable-step` is available for workflow steps and composite action steps. It is inline-only; configuration exclusions do not define step-level scope.

Inline directive format:

```
# seiton: disable-next-line job-permissions-required
# seiton: disable-step unredacted-secrets
# seiton: disable-job build job-permissions-required,credentials
# seiton: disable-file dangerous-triggers,job-permissions-required
```

Use `disable-step` when a diagnostic belongs to a step as a whole or may be reported inside a multi-line `run:` block. Use `disable-next-line` only when the rule reports on a specific YAML key line.

Non-normative note: parsers may allow optional spaces after commas and repeated separators (comma plus whitespace), but normalized output must preserve rule-id matching behavior.

### 5.6 Audit Metadata Policy

- Suppression reason text is optional and not required by contract.
- Expiration (`expires`) is not required by contract.
- Implementations may support optional metadata fields, but must not require them for valid exclusion entries.

### 5.7 Rule Configurability Policy

All rules are fully configurable by the user.

- All rules are disableable by user configuration.
- All rules allow user-specified severity override via config.
- Severity order is `Error > Warning > Info`.

### 5.7.1 Default Severity Criteria

Each rule's default diagnostic severity is determined by the following criteria. When a user config provides `rules.<rule-id>.severity`, that override applies to **all** diagnostics from the rule regardless of these criteria.

| Severity | Criteria | Examples |
|---|---|---|
| **Error** | The workflow will **fail at runtime**, violates a hard correctness constraint, or represents an **active security vulnerability** (injection, undefined var, credential misuse). The user must fix this to have a working/safe workflow. | Invalid job structure, unknown needs target, template injection, deprecated runtime that will fail. |
| **Warning** | A **best-practice violation** or **potential risk** that does not break execution but degrades security posture, maintainability, or reliability. The workflow runs but is suboptimal. | Unpinned action refs, missing explicit permissions, deprecated commands, dangerous triggers. |
| **Info** | **Informational notice** with no correctness or security impact. Emitted only in verbose/observability contexts. | Suppression acknowledgements, ignored-action notifications. |

**Mixed-severity rules**: Some rules emit diagnostics at different severities depending on the specific condition detected within a single rule. For example, `permissions` emits Error for invalid values but Warning for overly-broad valid scalars. When a user overrides severity via config, the override applies uniformly to all diagnostics from that rule.

### 5.7.2 Per-Rule Default Severity

The following table defines the normative default severity for each rule. Implementations must emit diagnostics at these levels when no user override is configured.

| Rule ID | Default Severity | Notes |
|---|---|---|
| `job-structure` | error | |
| `reusable-workflow` | error | |
| `local-action-inputs` | mixed | error (invalid/missing inputs, invalid metadata), warning (deprecated inputs) |
| `permissions` | mixed | error (invalid values), warning (overly-broad valid scalars) |
| `popular-action-inputs` | warning | |
| `outdated-action-runner` | error | |
| `unpinned-uses` | mixed | error (invalid Docker ref format), warning (unpinned SHA), info (ignored-action verbose) |
| `unpinned-image` | warning | |
| `dangerous-triggers` | warning | |
| `job-permissions-required` | warning | |
| `needs-graph` | error | |
| `shell-name` | mixed | error (invalid shell name), warning (shell-OS incompatibility) |
| `runner-label` | mixed | warning (unknown labels), error (conflicting OS families), info (additional-known-label verbose) |
| `runner-no-latest` | warning | |
| `id-naming` | error | |
| `glob-pattern` | error | |
| `dispatch-inputs` | error | |
| `schedule-event` | error | |
| `workflow-call-input-default` | error | |
| `deny-write-all` | error | |
| `credentials` | mixed | warning (missing credentials), error (plaintext password) |
| `template-injection` | error | |
| `unsound-contains` | mixed | error (user-controllable values), info (other contexts) |
| `bot-conditions` | mixed | warning (equality checks). info (inequality/exclusion checks) only when `strict-detection: true`. Suppressed entirely when AND-conjoined with non-spoofable trigger-author context, when no workflow trigger provides PR context, or when any non-PR trigger is present (mixed-trigger workflows). |
| `expr-undefined-var` | error | |
| `run-env-context-direct-use` | error | |
| `run-secrets-context-direct-use` | error | |
| `run-inputs-context-direct-use` | error | |
| `secrets-whole-context-access` | error | |
| `checkout-persist-credentials` | warning | |
| `checkout-unsafe-pr` | warning | |
| `artipacked` | mixed | error (legacy checkout credentials exposed via hidden files), warning (v6+ $RUNNER_TEMP risk only). Unknown checkout refs (for example SHA pins, branch refs, or arbitrary non-semver tags) conservatively assume both risks; unknown upload-artifact refs conservatively assume hidden-file behavior is not statically known. |
| `background-steps` | mixed | error (unknown/forward/non-background `wait`/`cancel` references); warning (more than 10 concurrent backgrounds in a job). |
| `known-vulnerable-actions` | error | online |
| `impostor-commit` | error | online |
| `ref-confusion` | error | online |
| `stale-action-refs` | warning | online |
| `deny-read-all` | error | |
| `deny-inherit-secrets` | error | |
| `job-timeout-minutes-required` | error | |
| `github-app-token-inputs` | error | |
| `workflow-secrets` | error | |
| `job-secrets` | error | |
| `action-shell-is-required` | error | |
| `cache-poisoning-trigger` | warning | |
| `self-hosted-runner-trigger` | warning | |
| `unredacted-secrets` | warning | |
| `secrets-outside-env` | warning | |
| `matrix` | warning | |
| `env-var` | warning | |
| `deprecated-commands` | warning | |
| `if-cond` | warning | |
| `fake-ternary` | warning | |
| `archived-uses` | warning | |
| `insecure-commands` | warning | |
| `overprovisioned-secrets` | warning | |
| `forbidden-uses` | mixed | warning (policy violation), info (allow-pattern overrides deny, verbose only) |
| `ref-version-mismatch` | warning | |
| `use-trusted-publishing` | warning | |
| `if-expr-wrapper` | warning | |
| `unsound-condition` | warning | |
| `unpinned-tools` | warning | |
| `concurrency-limits` | warning | opt-in |

### 5.8 Rule-Specific Configuration

Selected rules accept rule-specific configuration keys within the `rules.<rule-id>` section, in addition to the shared `enabled` / `severity` keys.

- Rule-specific keys are defined per rule ID. Unknown keys for a given rule ID are configuration errors.
- Where a rule accepts an additive list, merge behavior is set union (`effective = built-in U user-provided`) with deterministic deduplication.
- Duplicate entries after normalization are ignored.
- Invalid entries must produce configuration error with enough location/context for users to fix input.
- Extension never removes built-in defaults.

Non-normative example configuration shape:

```yaml
rules:
  dangerous-triggers:
    events:
      - issue_comment
      - pull_request_review_comment

  runner-label:
    known-hosted-labels:
      - ubuntu-24.04-arm
      - windows-2025-vs2026

  credentials:
    public-registries:
      - registry.example.com
      - mirror.example.net:5000

  cache-poisoning-trigger:
    untrusted-triggers:
      - discussion

  unredacted-secrets:
    output-commands:
      - tee

  unpinned-uses:
    ignore-actions:
      - owner: "my-org/*"
      - owner: "my-org/*"
        refs: [main, master]

  forbidden-uses:
    allow:
      - actions/*
    deny:
      - some-org/*

  expr-undefined-var:
    assume-events:
      - workflow_dispatch
      - repository_dispatch

  overprovisioned-secrets:
    max-step-env-secrets: 5
    max-job-secrets: 5

  bot-conditions:
    strict-detection: false
```

#### 5.8.1 `dangerous-triggers` — `events`

- Allows users to add event names treated as dangerous by the `dangerous-triggers` rule.
- Matching uses normalized event names (ASCII lower-case); configuration values should use canonical GitHub event naming.
- If a configured event is present in workflow `on`, rule emits the same diagnostic/severity behavior as built-in dangerous events.

#### 5.8.2 `runner-label` — `known-hosted-labels`

- Allows users to add runner labels treated as known GitHub-hosted labels for `runner-label` rule evaluation.
- Matching uses normalized label values (ASCII lower-case).
- Labels added here suppress only `runner-label` unknown-label diagnostics; they do not alter parsing or execution semantics.

#### 5.8.3 `credentials` — `public-registries`

- Allows users to add registry hosts treated as public/credential-optional by the `credentials` rule.
- Entry unit is registry host (`host` or `host:port`), without scheme and path.
- Matching uses normalized host values (ASCII lower-case).
- When image registry host matches this merged public-registry set, missing credentials does not produce `credentials` diagnostics.

#### 5.8.4 `cache-poisoning-trigger` / `self-hosted-runner-trigger` — `untrusted-triggers`

- Allows users to add trigger event names treated as low-trust for `cache-poisoning-trigger` and/or `self-hosted-runner-trigger` evaluation.
- Each rule has its own independent `untrusted-triggers` list; users set them separately to control which rule is affected.
- Matching uses normalized event names (ASCII lower-case).
- Extended trigger names never replace the built-in low-trust trigger set.

Built-in low-trust triggers for `cache-poisoning-trigger` (aligned with [GitHub Actions dependency caching — cache access for low-trust workflow triggers](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching#cache-access-for-low-trust-workflow-triggers)):

- `pull_request_target`
- `workflow_run`
- `issue_comment`

`pull_request` is intentionally excluded: caches created by `pull_request` runs are scoped to the merge ref and do not write to the default-branch cache scope.

Evaluation scope:

- Workflow-level: if any built-in or configured low-trust trigger appears in `on:`, write-capable cache steps are reported.
- Write-capable cache actions: `actions/cache`, `actions/cache/save`.
- Not reported: `actions/cache/restore` (restore-only). `lookup-only: true` on `actions/cache` does not disable saves.
- Does not analyze cache `key` / `restore-keys` expressions or `actions/checkout` `ref` values.

#### 5.8.5 `unredacted-secrets` — `output-commands`

- Allows users to add command names treated as output sinks by `unredacted-secrets`.
- Matching uses normalized command names (ASCII lower-case).
- Extended command names never replace the built-in sink command set.

#### 5.8.6 `forbidden-uses` — `allow` / `deny`

- `allow` defines additive allow patterns for `forbidden-uses` policy evaluation.
- `deny` defines additive deny patterns for `forbidden-uses` policy evaluation.
- Matching and precedence are runtime-defined by the `forbidden-uses` rule implementation; this contract requires deterministic matching for identical input and config.

#### 5.8.7 `expr-undefined-var` — `assume-events`

- Allows users to declare which trigger events the workflow is expected to handle.
- This provides event-type context for expression validation, suppressing false positives for event-specific context roots (e.g. `github.event.inputs` is valid under `workflow_dispatch`).
- Values are event name strings matching canonical GitHub event naming.

#### 5.8.8 `unpinned-uses` — `ignore-actions`

- Accepts a list of object entries.
- Each entry requires `owner`, a wildcard pattern matched against `owner/repo` case-insensitively.
- `refs` is optional. When omitted, `unpinned-uses` is suppressed for all refs of matching actions.
- When `refs` is present, it is a non-empty list of exact ref strings matched case-sensitively.
- Normalization trims surrounding whitespace; duplicate entries after normalization are ignored. Pattern matching uses ASCII lower-case normalization for `owner/repo`, while `refs` retain original case semantics.
- Unknown object keys, missing `owner`, empty `owner`, empty `refs`, empty ref elements, non-scalar `owner` values, or non-scalar `refs` entries are configuration errors. Scalar string items are configuration errors.

#### 5.8.9 `overprovisioned-secrets` — `max-step-env-secrets` / `max-job-secrets`

- `max-step-env-secrets`: Maximum number of `secrets.*` references allowed in a single step `env:` block before a diagnostic is emitted. Default: `5`.
- `max-job-secrets`: Maximum number of explicit secrets allowed in a single reusable workflow call `secrets:` block before a diagnostic is emitted. Default: `5`.
- Both values must be non-negative integers; values of `0` effectively require zero secret assignments.
- Setting either key suppresses the diagnostic only when the count is within the configured limit.
- Note: two explicitly named secrets in a step `env:` is a well-established least-privilege pattern and should not produce diagnostics under the default threshold.

#### 5.8.10 `bot-conditions` — `strict-detection`

- `strict-detection`: When `true`, report exclusion checks (`!=`) against spoofable bot contexts on PR-only workflows at info severity. Default: `false`.
- Rationale: `github.actor != 'dependabot[bot]'` is a common exclusion pattern with lower exploit impact than equality-based privilege grants; default-off reduces noise that would otherwise lead users to disable the rule entirely.
- Mitigation: A spoofable comparison is suppressed when the same expression AND-conjoins a non-spoofable trigger-author context (`github.event.pull_request.user.login` or `.id`) comparing the same literal with the **same operator** (`==` with `==`, `!=` with `!=`). Mismatched operators (for example `github.actor != 'dependabot[bot]' && github.event.pull_request.user.login == 'dependabot[bot]'`) do not mitigate.
- Trigger scope: Diagnostics are emitted only on PR-only workflows (`pull_request`, `pull_request_target`, `pull_request_review`, `pull_request_review_comment`). Mixed or non-PR triggers suppress all diagnostics for that workflow.

**Outcome matrix** (representative cases; `*` = any value):

| `strict-detection` | Operator | Workflow triggers | Mitigation (AND-conjoined) | Outcome |
| --- | --- | --- | --- | --- |
| `false` | `==` | PR-only | none | **warning** |
| `false` | `==` | PR-only | dual `==` on `user.login` / `user.id` | no diagnostic |
| `false` | `!=` | PR-only | any | no diagnostic |
| `true` | `!=` | PR-only | none | **info** |
| `true` | `!=` | PR-only | dual `!=` on `user.login` / `user.id` | no diagnostic |
| `true` | `!=` | PR-only | mismatched operator | **info** |
| `true` | `==` | PR-only | none | **warning** |
| `*` | `*` | mixed or non-PR | `*` | no diagnostic |
| `true` | `!=` | PR-only | none | **info** |
| `true` | `!=` | PR-only | dual `!=` on `user.login` / `user.id` | none |
| `true` | `!=` | PR-only | mismatched operator | **info** |
| `true` | `==` | PR-only | none | **warning** |
| `*` | `*` | mixed or non-PR | `*` | none |

#### 5.8.11 `run-env-context-direct-use` — `strict`

- `strict`: When `true`, `run-env-context-direct-use` reports shell single-quoted no-expand contexts instead of suppressing them. Default: `false`.
- no-expand heredoc (`<<'EOF'`) remains suppressed regardless of `strict`.
- Rationale: default behavior prioritizes noise reduction in intentional no-expand shell contexts; strict mode allows policy hardening for organizations that want explicit diagnostics.

#### 5.8.12 `run-inputs-context-direct-use` — `strict`

- `strict`: When `true`, `run-inputs-context-direct-use` reports shell single-quoted no-expand contexts instead of suppressing them. Default: `false`.
- no-expand heredoc (`<<'EOF'`) remains suppressed regardless of `strict`.
- Rationale: inputs are often routed through intentionally no-expand remote-shell patterns; default suppression avoids non-actionable diagnostics while preserving an explicit hardening opt-in.

#### 5.8.13 Cross-Rule Guardrails (No-Expand Context Policy)

- `run-env-context-direct-use` and `run-inputs-context-direct-use` share the same suppression gate for no-expand contexts to avoid drift across similar rules.
- no-expand heredoc suppression is unconditional in these direct-use rules.
- shell single-quoted suppression is conditional (`strict: false`) for env/inputs, but intentionally **not** for secrets.
- `run-secrets-context-direct-use` keeps diagnostics in shell single-quoted no-expand contexts and emits manual-refactor guidance when no safe fix can be attached.

Diagnostic outcome matrix (`run-env-context-direct-use`, `run-inputs-context-direct-use`, `run-secrets-context-direct-use`):

| Shell context | `strict` | `run-env` / `run-inputs` | `run-secrets` |
|---|---|---|---|
| Unquoted or double-quoted (expandable) | n/a | **diagnose** | **diagnose** |
| Shell single-quoted (`'...${{ }}...'`) | `false` | none | **diagnose** |
| Shell single-quoted (`'...${{ }}...'`) | `true` | **diagnose** | **diagnose** |
| Single-quoted heredoc (`<<'DELIM'` body) | any | none | none |

Auto-fix when a diagnostic is emitted:

| Shell context | `run-env` (`strict: true` only for single-quoted) | `run-inputs` (`strict: true` only for single-quoted) | `run-secrets` |
|---|---|---|---|
| Unquoted or double-quoted | fix when safe | fix when safe | fix when safe |
| Shell single-quoted | simple standalone token only | no fix | no fix |
| Single-quoted heredoc | n/a (suppressed) | n/a (suppressed) | n/a (suppressed) |

Notes:

- `strict` applies only to `run-env-context-direct-use` and `run-inputs-context-direct-use`.
- Single quotes nested inside a double-quoted string do not enter shell single-quote state; expressions there follow the expandable row.
- Complex single-quoted tokens (for example `'pre-${{ env.VERSION }}-post'`) are diagnosed under `strict: true` for env/inputs but remain no-fix.
- Shell single-quote detection is line-scoped (quotes are not tracked across newlines).

### 5.9 Minimal and Advanced Example Configuration File

Minimal example (recommended default authoring style):

```yaml
rules:
  dangerous-triggers:
    severity: error
```

Advanced example (non-normative):

The following YAML shows a larger non-normative example with optional sections.

```yaml
rules:
  job-permissions-required:
    enabled: false

  dangerous-triggers:
    severity: error
    events:
      - issue_comment
      - pull_request_review_comment

  shell-name:
    severity: warning

  runner-label:
    known-hosted-labels:
      - custom-large
      - ubuntu-24.04-arm

  credentials:
    public-registries:
      - registry.example.com
      - mirror.example.net:5000

  cache-poisoning-trigger:
    untrusted-triggers:
      - discussion

  unredacted-secrets:
    output-commands:
      - tee

  forbidden-uses:
    deny:
      - some-untrusted-org/*

  expr-undefined-var:
    assume-events:
      - workflow_dispatch
      - repository_dispatch

  overprovisioned-secrets:
    max-step-env-secrets: 3
    max-job-secrets: 3

  # Online rules (default disabled; enable to activate network-assisted audit)
  known-vulnerable-actions:
    enabled: true
  impostor-commit:
    enabled: true
  ref-confusion:
    enabled: true
  stale-action-refs:
    enabled: true

exclusions:
  - file: ".github/workflows/legacy/*.yml"
    rules:
      - dangerous-triggers
      - job-permissions-required

  - file: ".github/workflows/release.yml"
    jobs:
      - publish
    rules:
      - credentials

fix:
  defaults:
    job-timeout-minutes: 15

  pinning:
    enable-network: true
    min-age-days: 14
    exclude-branches:
      - main
      - master
    ignore-actions:
      - uses: "slsa-framework/*"
        ref: "*"

  images:
    enable-network: true
    exclude-images:
      - scratch
    exclude-tags:
      - latest
    ignore-images:
      - "mcr.microsoft.com/**"

network:
  on-error: skip
  timeout-seconds: 30
  max-concurrency: 4
  github:
    ghes-api-url: ""
    ghes-fallback: false

output:
  sort-order: location    # location (default) | rule
```

### 5.10 Recommended Config File Name and Location

Discovery order (when neither `--config` nor `SEITON_CONFIG` is set):

1. `.github/seiton.yaml` (primary)
2. `.github/seiton.yml`
3. `seiton.yaml`
4. `seiton.yml`

Explicit-config precedence: `--config <path>` > `SEITON_CONFIG` env var > discovery order. When an explicit path is provided, that file is the sole config source. Prioritizing `.github/` keeps workflow-related policy close to workflow files.

### 5.11 Configuration Profile Reference

This section describes four canonical usage profiles. Each profile states which rules are active, what capabilities are available, and what config is required.

---

#### Profile 1: No Config (Default Behavior)

Config file is absent or empty. No configuration is required.

**Active rules:** All rules in §4.4 with Default = `✓`. Online rules (Default = `✗`) and opt-in local rules (`concurrency-limits`) require explicit `rules.<id>.enabled: true`.

**Auto-fix behavior:** Local-only fixes attach per §8.4 (rules marked ✓ Fixable or △ Partial). `unpinned-uses` / `unpinned-image` pin-network fixes require explicit opt-in (see Profile 3a).

---

#### Profile 2: Minimal Config

Minimal config overrides only the settings users want to change. All omitted settings use built-in defaults. Profile 1 rule activation is unchanged unless `rules.<id>.enabled: false` or `rules.<id>.severity` is specified.

**Typical use cases:**

- Silence a rule that generates too much noise during migration
- Raise severity of a rule to error for organization policy
- Exclude a specific legacy workflow file from certain rules
- Add custom runner labels or registry hosts

**Example:**

```yaml
rules:
  action-shell-is-required:
    enabled: false
  deny-write-all:
    severity: error
  runner-label:
    known-hosted-labels:
      - ubuntu-24.04-large
exclusions:
  - file: ".github/workflows/legacy-release.yml"
    rules:
      - runner-no-latest
```

Active rules: same as Profile 1 minus disabled rules; exclusion/suppression applies per §5.3–§5.5.

**Constraints:**
- All rules can be disabled or have their severity overridden via config (§5.7).

---

#### Profile 3: Network Access Enabled

Network access must be explicitly opted in. It enables two distinct network-backed capabilities:

**3a — Pin remediation** (`fix.pinning.enable-network: true` and/or `fix.images.enable-network: true`):

Adds auto-fix suggestions to `unpinned-uses` and `unpinned-image` by resolving SHAs and digests at remediation time. Configuration keys are specified in §5.12.

Active rules: same as Profile 1. **Additionally**, `unpinned-uses` and `unpinned-image` now carry auto-fix data.

**3b — Online rules** (`rules.<online-rule-id>.enabled: true`):

Activates the four online rules that require network access:

| Rule | Requires |
|---|---|
| `known-vulnerable-actions` | Advisory dataset lookup via GitHub API |
| `impostor-commit` | Commit reachability check via GitHub API |
| `ref-confusion` | Branch/tag namespace query via GitHub API |
| `stale-action-refs` | Release/tag mapping check via GitHub API |

**3a + 3b combined:** All §4.4 rules active; `unpinned-uses` and `unpinned-image` carry auto-fixes.

---

#### Profile 4: Advanced / Full Config

All sections are populated. Provides maximum control over every aspect of linting, additive customization, suppression scope, and network-assisted behavior.

**Active rules:** identical to Profile 3a + 3b combined when all online rules are enabled and fix network is on. Active rule set is determined by `rules.<id>.enabled`, exclusion patterns, and inline directives.

See §5.9 for a comprehensive non-normative configuration example covering all sections.

---

#### Profile Summary Table

| Profile | Config required | Local-AST rules active | Online rules active | `unpinned-*` carry fixes |
|---|---|---|---|---|
| 1 No config | None | All §4.4 local-AST (~48 rules) | ✗ | ✗ |
| 2 Minimal | Partial (only changed keys) | Same ± per-rule overrides | ✗ | ✗ |
| 3a Pin remediation | `fix.pinning.enable-network: true` | Same as Profile 1 | ✗ | ✓ |
| 3b Online rules | `rules.<id>.enabled: true` (4 rules) | Same as Profile 1 | ✓ (4 rules) | ✗ |
| 3a+3b | Both enabled | Same as Profile 1 | ✓ (4 rules) | ✓ |
| 4 Full config | All sections populated | 3a+3b ± per-rule overrides | ✓ (4 rules) | ✓ |

Non-normative guidance for progressive adoption:

1. Start with Profile 1 (no config). Review diagnostics.
2. Apply Profile 2 to silence known migration debt or raise policy-critical rule severity.
3. Enable Profile 3a when ready to auto-fix pinning issues at remediation time.
4. Enable Profile 3b when advisory and ref-confusion coverage is needed.
5. Graduate to Profile 4 only when the full control surface is required.

---

### 5.12 `fix` Section Specification

The `fix` top-level section groups auto-fix generation settings. All keys are optional; omitted keys use built-in defaults.

```yaml
fix:
  defaults:
    job-timeout-minutes: 15       # default timeout for job-timeout-minutes-required auto-fix

  pinning:
    enable-network: true          # enable SHA resolution for unpinned-uses fixes
    min-age-days: 14              # minimum tag age for pinning eligibility
    exclude-branches:             # branch refs to never pin
      - main
      - master
    ignore-actions:               # action patterns to skip
      - uses: "slsa-framework/*"
        ref: "*"

  images:
    enable-network: true          # enable digest resolution for unpinned-image fixes
    exclude-images:               # image names to skip ("scratch" always enforced)
      - scratch
    exclude-tags:                 # tag names to skip
      - latest
    ignore-images:                # glob patterns for images to skip
      - "mcr.microsoft.com/**"
```

- `fix.defaults.job-timeout-minutes`: integer or null. When set, `job-timeout-minutes-required` attaches auto-fix with this value. Null/missing or `<= 0` disables fix attachment.
- `fix.pinning.enable-network`: when `true`, `unpinned-uses` diagnostics may receive network-resolved SHA fix payloads via the pin remediation engine. Default: `false`.
- `fix.pinning.min-age-days`: minimum age in days before a tag is eligible for SHA pinning. Default: `14`. `0` disables the constraint.
- `fix.pinning.exclude-branches`: branch names to never pin. Default: `["main", "master"]`.
- `fix.pinning.ignore-actions`: list of `{uses, ref}` wildcard patterns (`*` matches any sequence, `?` matches single char) to skip during SHA resolution. No regex.
- `fix.images.enable-network`: when `true`, `unpinned-image` diagnostics may receive network-resolved digest fix payloads. Default: `false`.
- `fix.images.exclude-images`: image names to skip. `scratch` is always enforced regardless of config.
- `fix.images.exclude-tags`: tag names to skip. Default: `["latest"]`.
- `fix.images.ignore-images`: glob patterns for images to skip entirely.

### 5.13 `network` Section Specification

The `network` top-level section groups shared network behavior settings used by all network-dependent features (pin remediation, online rules). All keys are optional; omitted keys use built-in defaults.

```yaml
network:
  on-error: skip                  # skip | fail
  timeout-seconds: 30
  max-concurrency: 4
  github:
    ghes-api-url: ""
    ghes-fallback: false
```

- `network.on-error`: controls behavior when network operations fail.
  - `skip` (default): resolution failures leave the diagnostic without fix and continue processing.
  - `fail`: any resolution failure causes the operation to return an error immediately.
- `network.timeout-seconds`: HTTP request timeout in seconds. Default: **`30`**. Accepted range after validation: **`0`–`300`**; larger values emit an error diagnostic and normalize to **`300`**.
- `network.max-concurrency`: maximum concurrent network operations. When omitted, the effective default is **`min(4, logical processor count)`** (minimum **`1`**), so the implicit default never exceeds the cap. Accepted range after validation when set: **`1`**–**`logical processor count`**; larger values emit an error diagnostic and normalize to that cap.
- `network.github.ghes-api-url`: optional GitHub Enterprise Server API URL. Empty string = github.com only. When set, **must** be an absolute `https` URI; `http`, other schemes, and embedded credentials (`https://user@host/...`) are configuration errors. Stored value is normalized to absolute URI form.
- `network.github.ghes-fallback`: when `true` and `ghes-api-url` is set, repositories not found on GHES are retried against github.com. Default: `false`.

HTTP clients that send the GitHub Bearer token use `AllowAutoRedirect = false` at the transport layer and manually follow **same-origin** `3xx` responses only; cross-origin redirects are not followed, so the token is not automatically replayed against a different scheme/host/port after a redirect response.

Token resolution:

- GitHub API token is resolved from environment variables in hardcoded order: `SEITON_GITHUB_TOKEN` → `GITHUB_TOKEN`.
- This order is not configurable. If no variable yields a token, API calls are made unauthenticated (lower rate limit).
- Rationale: exposing token env var selection in config creates an attack surface where a malicious config redirects token resolution to unintended environment variables.

Configuration parsing enforces resource caps on YAML payloads: **`1 048 576`** UTF-8 bytes total, **`64`** compound nesting depth, and **`50 000`** counted structural units. Oversized payloads fail validation with deterministic error messages. Wildcard pattern matching for `fix.pinning.ignore-actions` uses deterministic, bounded evaluation under these limits, so pattern evaluation cannot stall the process.

**Security note (configuration path):** `SEITON_CONFIG` / `--config` can name any absolute path. On shared CI runners, set them only to trusted locations. For fork pull request workflows, avoid pointing config at a file the PR branch can overwrite; use default discovery or defaults. CLI `--verbose` prints the resolved config path on stderr.


### 5.14 `output` Section Specification

The `output` top-level section controls diagnostic output behavior. All keys are optional; omitted keys use built-in defaults.

```yaml
output:
  sort-order: location            # location | rule
```

- `output.sort-order`: controls the order in which diagnostics are emitted.
  - `location` (default): sort by source position (StartLine → StartColumn → RuleId → Message). This matches the reading order of the source file and is the most natural output for interactive use.
  - `rule`: sort by rule priority first, then severity, then source position. Groups diagnostics by rule, useful for batch-fixing all instances of a single rule at a time.
- Rich human-readable CLI output (`text`, `github-actions`) replaces the source excerpt with a minimal YAML ancestor-chain gutter block when a structure path can be resolved (message prefix such as `jobs.'…'` / `steps[n]`, optional `structure-path` diagnostic metadata, or a structural ancestor including `jobs:` / `steps:` / `runs:`). Unrelated siblings are omitted with `...`; carets remain on the target line. When structure context cannot be built, the formatter falls back to the plain source snippet. Not applicable to `json`, `sarif`, or `--oneline`. See `.github/docs/Seiton_CLI_spec.md` §6.1.1 for the user-visible format.

---

## 6. Diagnostic Processing Contract

Diagnostic processing in linter entrypoint must be deterministic.

1. Start with parser diagnostics from parse result.
2. Append rule diagnostics from active rule set.
3. Apply stable sort according to configured sort order (see §6.2).
4. Deduplicate using deterministic diagnostic identity.
5. Apply final filtering phase.

Diagnostic identity for deduplication: `(severity, normalizedMessage, startLine)`. Column and byte offset are excluded so that parser diagnostics (reported at expression-internal positions) and lint diagnostics (reported at YAML key positions) on the same line with the same message are treated as duplicates. The message is **normalized** by stripping either the leading `jobs.'<id>'.steps[<n>] ` prefix or the leading `steps[<n>] ` prefix (if present, for action-metadata composite steps), so that alias-expanded or composite-expanded steps sharing the same source position are deduplicated even though each carries a distinct step index.

When a lint diagnostic duplicates a parser diagnostic (same identity), the lint diagnostic **replaces** the parser diagnostic so the `RuleId` is preserved for suppression and attribution.

#### 6.0.1 Accepted Parser/Linter Diagnostic Overlap

Some source lines may have diagnostics from both the parser (`[syntax-check]`) and a lint rule. Two categories:

- **Complementary**: Parser and lint rule detect semantically different issues on the same line (e.g., parser reports structural error, lint rule reports domain violation). Both diagnostics add value and are retained.
- **Redundant**: The same semantic check runs at both layers, but with different type resolution quality. The parser check uses static inference (dynamic contexts resolve to `any`), while the lint check uses override-aware inference (dynamic contexts resolve to concrete types). The parser must apply `any` guards (see `Seiton_Parser_spec.md` §7.4) to ensure it stays silent when type information is incomplete, deferring to the lint pass for concrete type checking.

Final filtering phase:

- Apply exclusion/suppression matches from config and inline directives according to §5.2 precedence.
- Keep deterministic behavior for identical inputs.

### 6.1 Observability Contract for Suppression

Linter output must include suppression observability data.

- Total suppressed diagnostic count.
- Per-rule suppressed count.
- Suppression application details containing at least:
  - `RuleId`
  - source location (`line`, `column`) of suppression directive/policy match anchor
  - matched diagnostic location (`line`, `column`)

This observability data enables CI detection of suppression increases.

### 6.2 Diagnostic Sort Order

Sort order is configurable via `output.sort-order` (§5.14).

| `sort-order` value | Sort key sequence |
|---|---|
| `location` (default) | StartLine → StartColumn → RuleId → Message |
| `rule` | RulePriority → Severity → StartLine → StartColumn → Message |

When multiple diagnostics share the same position under `location` sort, rule ID (lexicographic) breaks the tie. Under `rule` sort, lower priority numbers sort first.

---

## 7. Cross-Document Consistency Rule

When this specification is revised, also review and update:

- `.github/docs/Seiton_Linter_csharp_spec.md`
- `.github/docs/Seiton_Linter_go_spec.md`
- `.github/docs/Seiton_spec.md`
- `.github/docs/Seiton_Parser_spec.md` when parser/linter boundary changed

---

## 8. Auto-Fix Contract

Seiton supports optional auto-fix suggestions attached to diagnostics.
Fix application is separate from lint detection and must not be triggered automatically during normal lint runs.

### 8.1 Fix Data Model

A fix consists of a human-readable description and one or more text edits.

**TextEdit**:

| Field | Type | Description |
|---|---|---|
| Offset | int | UTF-8 byte offset into the original source (from `TextRange.Start`) |
| Length | int | Number of bytes to replace (0 = pure insertion) |
| NewText | string (UTF-8) | Replacement text (empty string = deletion) |

**DiagnosticFix**:

| Field | Type | Description |
|---|---|---|
| Description | string | Short human-readable description of the fix |
| Edits | TextEdit[] | Ordered list of non-overlapping edits |

Constraints:

- A `Diagnostic` may carry zero or one `DiagnosticFix`. Optional; absence is valid for all rules.
- A `DiagnosticFix` may contain one or more `TextEdit` entries.
- Multiple `TextEdit` entries in a single fix must be non-overlapping.
- A fix targets only the file that produced the diagnostic (single-file edits only).

### 8.1.1 Parser Data Required for Fix Generation

Auto-fix generation depends on parser output plus original source bytes.

- Quote presence comes from AST scalar nodes (`StringNode.Quoted`).
- YAML structural context (scalar/mapping/sequence position) comes from typed AST shape and node-specific types.
- Edit anchor position comes from node `TextRange` (`Start`/`Length` and line/column fields).
- Indentation and line-ending style are derived from original source text at fix application/generation time (not stored as dedicated parser fields).

Design note:

- Dedicated indentation fields in parser output are optional. They are not required by this contract because YAML indentation is recoverable from source lines and node ranges.

### 8.2 Fix Application Contract

When a caller applies fixes to source text:

1. Collect all `DiagnosticFix` entries to apply.
2. Sort edits in descending offset order (apply from end of file to start).
3. Apply each edit: replace bytes `[Offset, Offset + Length)` with `NewText`.
4. Applying fixes from multiple diagnostics that have overlapping edits is a conflict; overlapping fixes must not be applied together. Caller is responsible for conflict detection.
5. After all edits are applied, re-lint the resulting file to verify no regressions.

### 8.3 Rule Contract Extension for Fix

Rules that support auto-fix must attach `DiagnosticFix` to each fixable `Diagnostic` at the point of diagnostic creation.

- Fix generation must not perform I/O or make network requests.
- Fix generation must be deterministic for identical inputs.
- If a rule cannot guarantee a safe fix for a specific diagnostic instance, it must omit the `Fix` field rather than emit an unsafe fix.

The existing `GetDiagnostics()` contract is unchanged; fixes are embedded within returned `Diagnostic` values.

### 8.3.1 Parser-Originated Fixes

The parser may also attach `DiagnosticFix` to parser-originated diagnostics when a deterministic fix is available. Parser fixes follow the same data model and application contract as rule fixes (§8.1, §8.2).

Current parser-originated fixes:

| Diagnostic | Fix Description |
|---|---|
| Unknown event option with Levenshtein suggestion (`on.<event> does not support option: X. did you mean "Y"?`) | Replace key bytes with suggested option name |
| Unknown `image_version` option with suggestion | Replace key bytes with suggested option name |

Parser fixes are always attached (no config gate) because they are on error paths only and the suggestion is unambiguous (single closest match within distance threshold).

### 8.4 Fixable Rule Catalog

The following table classifies each default rule by fix feasibility.

| Rule ID | Fix Feasibility | Fix Description |
|---|---|---|
| `deny-write-all` | ✓ Fixable | Replace `write-all` scalar with an explicit empty mapping baseline (`{}`) for least-privilege follow-up scoping. |
| `run-env-context-direct-use` | △ Partial fixable | Replace `${{ env.VAR }}` with `$VAR` (or `${VAR}` for POSIX shells) inside `run:` text. Diagnostics are suppressed in no-expand contexts (single-quoted shell strings and single-quoted heredocs) by default; with `strict: true`, shell single-quoted contexts are diagnosed and only standalone simple single-quoted tokens are auto-fixable. Compound expressions emit a help hint instead of a fix. |
| `job-permissions-required` | ✓ Fixable | Insert `permissions:` as a new key immediately after `runs-on:` (or after `uses:` for reusable workflow jobs, or after job id key if both are absent). When the job's steps reference popular actions with known permission requirements (from supplemental-required-permissions.json), the fix inserts the merged minimum required scopes (e.g., `contents: read` for `actions/checkout`). When no known action requirements are found, the fix inserts `permissions: {}`. |
| `unpinned-uses` | ✗ Not auto-fixable | Requires resolving current SHA for the referenced action/workflow at fix time (external I/O). |
| `unpinned-image` | ✗ Not auto-fixable | Requires resolving current digest for the referenced image at fix time (external I/O). |
| `dangerous-triggers` | ✗ Not auto-fixable | Correct replacement is semantic (remove event, or restructure trigger) and context-dependent. |
| `permissions` | ✗ Not auto-fixable | Correct value is context-dependent; `deny-write-all` / `deny-read-all` handle the scalar form. |
| `job-structure` | ✗ Not auto-fixable | Structural problems (missing `runs-on`, conflicting keys) require user intent to resolve. |
| `reusable-workflow` | ✗ Not auto-fixable | Forbidden key removal requires user to confirm intent. |
| `popular-action-inputs` | △ Partial | When a unique closest input name is found (unambiguous Levenshtein match within threshold), replace the unknown input key with the suggested name. No fix is attached when no suggestion is found or when the match would be ambiguous. |
| `needs-graph` | ✗ Not auto-fixable | Unknown dependency target or cycle requires user to determine correct dependency. |
| `shell-name` | ✗ Not auto-fixable | Correct shell name is ambiguous; user must select. |
| `runner-label` | ✗ Not auto-fixable | Closest known label may be suggested but apply is ambiguous. |
| `runner-no-latest` | △ Partial | Built-in `*-latest` labels remain warn-only by default. When `rules.runner-no-latest.fix-mapping` provides a replacement for the matched label, attach a scalar replacement fix using that mapped value; labels without a mapping remain no-fix. |
| `id-naming` | △ Partial | For job IDs with invalid characters, auto-fix converts to kebab-case (underscores become `-` alongside other normalization) and updates `needs:` string references that match the old job ID under ASCII case-insensitive comparison, within the same workflow—unless the slug would duplicate another job id in that workflow under the same ASCII case-insensitive comparison (then no fix). Expression references (e.g. `needs.<id>.outputs`) are not updated automatically. |
| `glob-pattern` | ✗ Not auto-fixable | Glob correction requires understanding user intent. |
| `credentials` | ✗ Not auto-fixable | Adding credentials requires secrets names that are not known to linter. |
| `template-injection` | △ Partial | For `run:` script sinks with deterministic paths, auto-fix generates env var indirection (new env mapping + shell variable reference). Skips `actions/github-script` `script` inputs, heredoc no-expand bodies, and shell single-quoted strings. One fix per step per pass (multi-pass handles remaining). |
| `expr-undefined-var` | ✗ Not auto-fixable | Correct context variable cannot be inferred automatically. |
| `run-secrets-context-direct-use` | △ Partial | Replace simple `${{ secrets.KEY }}` / `${{ secrets['KEY'] }}` in `run:` by reusing an existing unique `env` mapping when present; otherwise insert a new step-local `env:` entry and rewrite to the generated shell variable. Compound expressions emit a help hint instead of a fix. No fix is offered for ambiguous mappings, no-expand heredocs, or shell single-quoted strings; for shell single-quoted no-expand contexts diagnostics remain with manual-refactor guidance. The insertion path additionally skips flow-style `env` and empty `env: {}` but replacement-only reuse may still be offered in those cases. |
| `run-inputs-context-direct-use` | △ Partial | Replace simple `${{ inputs.KEY }}` / `${{ github.event.inputs.KEY }}` (and bracket forms) in `run:` by reusing an existing unique `env` mapping when present; otherwise insert a new step-local `env:` entry and rewrite to the generated shell variable. Compound expressions (single `${{ ... }}` block referencing inputs, e.g. `${{ inputs.tag \|\| 'v1.0.0' }}`) are fixed by moving the entire expression to `env:` and rewriting to the shell variable. Diagnostics are suppressed in no-expand contexts (single-quoted shell strings and single-quoted heredocs) by default; with `strict: true`, shell single-quoted contexts are diagnosed again but remain no-fix (manual refactor required). The insertion path additionally skips flow-style `env` and empty `env: {}` but replacement-only reuse may still be offered in those cases. |
| `secrets-whole-context-access` | ✗ Not auto-fixable | Correct remediation (refactoring to specific key access) requires user intent about which secrets are needed. |
| `checkout-persist-credentials` | △ Partial | For deterministic cases, insert or replace `with.persist-credentials: false`. Expression-valued cases remain no-fix. Review downstream authenticated git commands such as `git push`, which may need explicit auth setup (for example `git remote set-url origin <url>` or `gh auth setup-git`). |
| `checkout-unsafe-pr` | △ Partial | For deterministic literal `true` values, replace `with.allow-unsafe-pr-checkout` with `false`. Expression-valued cases remain no-fix because intent and runtime value are not statically known. |
| `artipacked` | ✗ Not auto-fixable | Safe remediation depends on whether the user intends to change checkout auth behavior, artifact scope, or hidden-file upload behavior. |
| `background-steps` | ✗ Not auto-fixable | Remediation requires reordering steps, adjusting `background:` flags, splitting `parallel` groups, or adding `wait`/`cancel`/`wait-all` coordination. |
| `known-vulnerable-actions` | ✗ Not auto-fixable | Selecting a safe replacement version/commit requires advisory-aware upgrade policy and user intent. |
| `impostor-commit` | ✗ Not auto-fixable | Safe replacement SHA requires trusted repository graph/advisory resolution outside deterministic local edit. |
| `ref-confusion` | ✗ Not auto-fixable | Correct disambiguation (tag vs branch vs SHA) depends on project policy and intent. |
| `stale-action-refs` | ✗ Not auto-fixable | Updating stale pins requires repository/version policy and may change runtime behavior. |
| `deny-read-all` | ✓ Fixable | Replace `read-all` scalar with an explicit empty mapping baseline (`{}`) or configured least-privilege template when deterministic. |
| `deny-inherit-secrets` | ✗ Not auto-fixable | Determining exact secret pass-through list requires user intent and callee contract knowledge. |
| `job-timeout-minutes-required` | △ Partial | Insert `timeout-minutes: <default>` at job level only when `LintConfig.DefaultJobTimeoutMinutesForFix` is configured. |
| `github-app-token-inputs` | ✗ Not auto-fixable | Required repository/permission scopes cannot be inferred safely without repository policy context. |
| `workflow-secrets` | ✗ Not auto-fixable | Correct scope-minimized secret mapping requires repository-specific intent. |
| `job-secrets` | ✗ Not auto-fixable | Step-level secret scoping cannot be inferred safely in general. |
| `action-shell-is-required` | ✗ Not auto-fixable | Explicit shell choice is runtime/policy dependent. |
| `cache-poisoning-trigger` | ✗ Not auto-fixable | Trust-boundary-safe cache remediation is topology-dependent and cannot be inferred safely. |
| `self-hosted-runner-trigger` | ✗ Not auto-fixable | Runner isolation/guard strategy is environment/policy dependent. |
| `unredacted-secrets` | ✗ Not auto-fixable | Safe remediation requires sink-specific intent and may alter runtime behavior. |
| `secrets-outside-env` | ✗ Not auto-fixable | Correct env handoff placement is context and policy dependent. |
| `matrix` | ✗ Not auto-fixable | Matrix shape correction requires workflow-specific intent. |
| `env-var` | ✗ Not auto-fixable | Name/scope normalization can be policy- and runtime-specific. |
| `deprecated-commands` | ✗ Not auto-fixable | Migration to env files is command-context dependent and may require script restructuring. |
| `if-cond` | ✗ Not auto-fixable | Correct condition semantics cannot be inferred reliably. |
| `fake-ternary` | ✗ Not auto-fixable | Safe rewrite depends on desired branch semantics and value coercion behavior. |
| `archived-uses` | ✗ Not auto-fixable | Replacement dependency requires security and compatibility intent. |
| `insecure-commands` | ✗ Not auto-fixable | Safe command hardening is shell- and command-specific. |
| `overprovisioned-secrets` | ✗ Not auto-fixable | Minimum required secret set needs repository-specific knowledge. |
| `forbidden-uses` | ✗ Not auto-fixable | Allow/deny policy remediation requires governance intent. |
| `ref-version-mismatch` | ✗ Not auto-fixable | Correct lineage/version intent resolution is policy dependent. |
| `local-action-inputs` | ✗ Not auto-fixable | Action metadata validation is structural and requires user-driven fix decisions. |
| `outdated-action-runner` | ✗ Not auto-fixable | Upgrading to a supported action version requires compatibility review and user intent. |
| `dispatch-inputs` | ✗ Not auto-fixable | Input schema corrections require workflow-specific knowledge and user intent. |
| `schedule-event` | ✗ Not auto-fixable | Cron expression and timezone corrections require scheduler knowledge and user intent. |
| `workflow-call-input-default` | ✗ Not auto-fixable | Default value corrections require understanding of caller contracts and intended type semantics. |
| `use-trusted-publishing` | ✗ Not auto-fixable | Trusted publishing migration depends on registry ecosystem and release architecture. |
| `if-expr-wrapper` | ✓ Auto-fixable (safe cases) | Wraps single-line `if:` expressions in `${{ }}`, including quoted scalars. Fix is suppressed for block scalars (structural newline) and values already containing `${{` markers (would nest). |
| `unsound-condition` | ✓ Auto-fixable (safe cases) | Rewrites block-scalar indicators from clip chomping (`|` / `>`) to strip chomping (`|-` / `>-`) when the indicator can be located in source. |
| `unpinned-tools` | ✗ Not auto-fixable | Required version value depends on the intended tool release and repository compatibility policy. |
| `concurrency-limits` | ✗ Not auto-fixable | Concurrency group naming and cancel-in-progress policy depend on workflow semantics and user intent. |

### 8.5 Fix Safety Policy

- A fix must be semantically equivalent for the common case; it must not silently change runtime behavior in a way that is not obvious from its description.
- Unsafe transformations that cannot preserve clear intent and reviewability must not be provided as auto-fix; they may only appear as diagnostic message guidance.
- Security-critical rules (§8.5) must not offer fixes that would circumvent their intent (for example, `deny-write-all` fix replaces `write-all` with `{}` as a strict baseline, not with suppression).

---

## 9. Fix Engine Formatting Preservation Policy

This section defines implementation-level common rules for source-style preservation during auto-fix generation and application.

### 9.1 Indentation Preservation

- Fix engine must preserve existing indentation width/style of the surrounding block whenever inserting new lines.
- For inserted mapping entries, indentation depth must be inferred from sibling keys in the same mapping scope.
- If no sibling exists, indentation must be inferred from parent node indentation plus one YAML level.
- Tabs must not be introduced unless tabs already exist in the target file and the target scope uses tabs.

### 9.2 Line Ending Preservation

- Fix engine must preserve the dominant line ending style of the target file (`LF` or `CRLF`).
- New lines introduced by fixes must use the same dominant style.
- If mixed line endings exist, fix engine should preserve line endings of the nearest surrounding lines.

### 9.3 Quote Preservation

- For scalar replacement where value style is unchanged (string-to-string replacement), fix engine should preserve existing quote style from the replaced node (`single`, `double`, or unquoted).
- If preserving quote style would produce invalid YAML or invalid expression text, fix engine may switch quote style to a valid form.
- For inserted scalar values, fix engine should default to unquoted form when YAML-safe and expression-safe; otherwise use double quotes.

### 9.4 YAML Context Safety

- Fix engine must only emit edits that remain valid in the target YAML context (mapping key value, sequence item, scalar value).
- A fix that changes node kind (for example scalar to mapping) is allowed only when explicitly defined by that rule's fix contract.
- When node-kind transition is not explicitly defined, fix engine must keep the original node kind.

### 9.5 Whitespace Stability

- Fix engine must minimize whitespace churn outside edited ranges.
- Trailing spaces must not be introduced by fixes.
- Existing blank-line grouping should be preserved unless the fix requires adding/removing exactly one logical block.

### 9.6 Fallback Policy

- If style-preserving edit generation fails (for example ambiguous indentation detection), the engine must not emit a potentially destructive fix.
- In that case, diagnostic remains without fix and should include remediation guidance text.

---

## 10. Fix Observability Contract

When a lint result includes diagnostics with fixes, the caller must be able to:

- Query which diagnostics in a `LintResult` have an associated `DiagnosticFix`.
- Count total fixable diagnostics.
- Apply fixes selectively (per-diagnostic or all-fixable).
- Run a dry-run preview mode that emits patch-style diff output without mutating source.

Fix application is a separate operation from linting and must not mutate the `LintResult` or the original source bytes.

### 10.1 JSON `fixable` Semantics

For JSON diagnostics output, `fixable` must represent **"fix-applicable when running with `--fix` under the same effective configuration"** rather than only "this diagnostic currently carries `DiagnosticFix` in check mode".

- `fixable = true` when the diagnostic already carries `DiagnosticFix`.
- `fixable = true` when the rule is fix-applicable in fix mode and all rule-specific prerequisites are satisfied under the current config/flags.
- `fixable = false` when prerequisites are missing (for example, required config defaults are unset, or network-assisted pin remediation is disabled for pinning rules).

This keeps machine-readable JSON aligned with user-observed behavior in `--fix` / `--fix --dry-run`.

### 10.2 Dry-Run Diff Preview

Dry-run preview is an output-only operation for fix review.

- Dry-run must not modify source files.
- Output format should be unified diff style with hunk headers (for example `@@ -a,b +c,d @@`) and `-` / `+` line markers.
- Preview scope should be limited to changed hunks, not full-file dump.
- Implementations should include configurable nearby context lines around each change (recommended default: 1-3 lines).
- Output target is runtime-defined (for example standard output in CLI mode), but behavior must remain deterministic for identical inputs.
- CLI implementations should present fix summary before residual diagnostics in apply/dry-run mode to preserve "what changed" -> "what remains" reading order.

---

## 11. Normative Evaluation Sequence for Exclusion

Exclusion-aware lint evaluation sequence is fixed as follows.

1. Parse workflow and obtain parser diagnostics/AST.
2. Validate exclusion configuration and inline directive syntax (including unknown rule ID errors).
3. Build active rule set.
4. Execute rules and collect rule diagnostics.
5. Apply severity overrides.
6. Sort and deduplicate diagnostics.
7. Apply exclusion/suppression filtering using §5.2 precedence.
8. Emit final diagnostics and suppression observability data (§6.1).

---

## 12. Network-Assisted Pin Remediation

This section specifies the optional network-assisted remediation feature that enables `unpinned-uses` and `unpinned-image` diagnostics to carry auto-fix suggestions by resolving SHA/digest values at fix application time.

### 12.1 Motivation and Scope

§8.3 and §8.4 classify `unpinned-uses` and `unpinned-image` as not auto-fixable because fix generation must not perform I/O. Network-assisted pin remediation is a **separate, opt-in operation** that satisfies this constraint by deferring resolution to an explicit remediation phase distinct from lint execution.

Comparison of reference tools:

| Aspect | pinact | dockerfile-pin | frizbee |
|---|---|---|---|
| GitHub Actions SHA | GitHub REST API | — | GitHub REST API |
| OCI image digest | — | OCI registry HEAD | OCI registry HEAD |
| GitHub token source | `PINACT_GITHUB_TOKEN` → `GITHUB_TOKEN` → keyring → ghtkn → anon | — | `GITHUB_TOKEN` → anon |
| GHES support | Yes (`ghes.api_url`, `ghes.fallback`) | No | No |
| Age filtering for updates | `--min-age` / `PINACT_MIN_AGE` (default 0; update target candidate filtering) | — | — |
| OCI auth | — | `authn.DefaultKeychain` (`~/.docker/config.json`) | `authn.DefaultKeychain` |
| Default excludes | `ignore_actions` (wildcard) | `ignore-images` (glob, negation) | `exclude_branches: [main, master]`; `scratch` always; `latest` by default |
| Separate command | `pinact run` | `dockerfile-pin run` | `frizbee actions` / `frizbee image` |
| Skip sentinel | — | — | `ErrReferenceSkipped` |

Design principles adopted for Seiton:
- Resolution is injected via an interface — not embedded in lint rules.
- Two separate resolver interfaces: one for GitHub Actions SHA, one for OCI image digest.
- Resolution is never called during `Check(utf8Yaml, filePath)`; only during an explicit `Remediate()` operation.
- Resolver caches results in-process to avoid redundant network calls across diagnostics.
- Resolver failures leave the diagnostic without a fix (`on-error: skip` behavior).

### 12.2 Resolver Interfaces

#### 12.2.1 `IActionShaResolver`

Resolves a GitHub Actions or Reusable Workflow reference to a pinned commit SHA.

```
Resolve(owner, repo, ref) -> (sha, tagComment, error)
```

- `owner`: repository owner (e.g. `actions`)
- `repo`: repository name (e.g. `checkout`)
- `ref`: tag, branch, or SHA string as it appears in the `uses:` value (e.g. `v4`, `main`)
- Returns: 40-hex SHA and annotation comment string, error.
  - Default comment: resolved ref string.
  - Canonical promotion: for alias-like version refs (`vN`, `vN.M`), resolver chooses the highest compatible concrete tag on the same resolved SHA when available.
- Returns `(null, null, SkippedError)` when the ref is excluded by configuration (matches `ignore_actions` patterns).

#### 12.2.2 `IImageDigestResolver`

Resolves an OCI image reference to a pinned digest.

```
Resolve(imageRef) -> ImageDigestResolution
```

- `imageRef`: image reference with optional tag (e.g. `node:20.11.1`, `redis`, `ghcr.io/org/image:v1.2.3`). Tagless refs are treated as `latest`.
- Returns: `Digest` = `sha256:<hex>` on success.
- Returns `Digest: null`, `SkipReason: null` when the image does not exist (HTTP 404 from registry) or is already digest-pinned.
- Returns `Digest: null`, `SkipReason: "<reason>"` when the image ref is excluded by configuration (matches `exclude_images`, `exclude_tags`, or `ignore_images`). Remediation appends `SkipReason` to the diagnostic `help:` line.

#### 12.2.3 OCI Registry Protocol: HEAD Manifest + Bearer Token Flow

Implementations must use `HEAD /v2/{repo}/manifests/{reference}` with appropriate `Accept` headers to obtain the digest from the `Docker-Content-Digest` response header. This is rate-limit-friendly: Docker Hub counts `GET /manifests` as a **pull** (charged against the pull-rate quota), whereas `HEAD /manifests` is an **API request** (a separate, more generous quota).

**Bearer token challenge/response flow** (required for Docker Hub official images accessed without stored credentials):

1. Send `HEAD /v2/{repo}/manifests/{ref}` — no auth.
2. If the registry returns `401 Unauthorized` with a `WWW-Authenticate: Bearer realm="...",service="...",scope="..."` header:
   a. Send `GET {realm}?service={service}&scope={scope}` to the auth endpoint.
   b. Parse the JSON response for `access_token` (preferred) or `token`.
   c. Retry `HEAD /v2/{repo}/manifests/{ref}` with `Authorization: Bearer {token}`.
3. If the registry returns `200 OK`, read `Docker-Content-Digest` header value.
4. If the registry returns `404 Not Found` (at any stage), return no digest without error.
5. For any other non-success status, surface as a resolution error.

Security constraints on the bearer token flow:
- The `realm` URL obtained from `WWW-Authenticate` **must** use HTTPS. HTTP realm URLs are rejected without making a request.
- The flow is only triggered when the initial request is unauthenticated. If Basic credentials are found in `~/.docker/config.json`, they are sent on the first request and the bearer challenge flow is skipped (401 with Basic auth → error, not retry).

**Why HEAD does not consume the Docker Hub pull quota:**

Docker Hub's pull limit (e.g. 100 pulls/6 hours for anonymous users) is charged when a client downloads at least one layer (i.e. performs a `GET /v2/{repo}/blobs/` request). A `HEAD /v2/{repo}/manifests/{ref}` request retrieves only response headers — no manifest body, no layer data — and is not counted as a pull. This is why tools like `dockerfile-pin` and Seiton use HEAD for digest resolution.

**Lesson learned:**
Using HEAD for digest resolution is the correct approach (confirmed across reference implementations). Implementations must also support the anonymous bearer token challenge flow (RFC 6750), because Docker Hub official images (e.g. `node:20`, `python:3.12`) require it when no Docker credentials are configured.

### 12.3 Configuration

Network-assisted pin remediation is disabled by default. It must be explicitly enabled via the `fix` section (§5.12) and uses `network` settings (§5.13).

#### 12.3.1 `fix.pinning.enable-network` / `fix.images.enable-network`

When `false` (the default), no resolver is instantiated and the corresponding diagnostics carry no fix. When `true`, resolver implementations may be provided. Actions SHA pinning and OCI image digest pinning can be enabled independently.

#### 12.3.2 Token Resolution

Token resolution and network behavior (GHES, timeouts, concurrency, redirect safety) are specified in §5.13 and apply to all network-dependent features including pin remediation.

#### 12.3.3 `fix.pinning.ignore-actions`

List of `{uses, ref}` wildcard patterns (`*` matches any sequence, `?` matches single char) to skip during Actions SHA resolution. Equivalent to pinact's `ignore_actions`. Common use case: SLSA reusable workflows where the caller must not pin the SHA. No regex — wildcard matching only, eliminating ReDoS risk.

#### 12.3.4 `fix.pinning.exclude-branches`

Branch names (exact string match, ordinal) to never pin. Default: `["main", "master"]`. Matches frizbee's default behavior. Rationale: pinning a branch reference to its current SHA is semantically incorrect — the intent of a branch ref is to track the branch tip.

#### 12.3.5 `fix.pinning.min-age-days`

Minimum age in days a tag must have before it is considered eligible for SHA pinning. Default: `14`.

Rationale: a tag that was pushed very recently may be subject to a supply-chain compromise or rollback and should not be immediately trusted for pinning. Requiring a minimum age gives the community time to detect anomalies before a tool pins to a potentially malicious SHA.

When `min_age_days: 0`, the age constraint is disabled entirely and all tags are eligible regardless of creation time.

Age is computed from the later of the tag creation date (`tagger.date` for annotated tags; `commit.committer.date` for lightweight tags) and the current UTC time at resolution time.

Current implementation behavior:

1. If the requested ref is version-like (`vN`, `vN.M`, `vN.M.P`), resolver builds a candidate set from GitHub Releases first, then Tags as fallback.
2. Candidates are restricted to the same version family as the requested ref (for example `v4` -> `v4.*`, `v4.1` -> `v4.1.*`).
3. Candidates newer than cutoff are excluded (`published_at` for releases, `commit.committer.date` for tags).
4. Resolver selects the highest eligible candidate (semver-first ordering with deterministic string fallback), then resolves that candidate tag to SHA.
5. If no eligible candidate exists, remediation returns skip (no fix).

When the requested ref is not version-like, resolver keeps direct ref resolution and applies age gate to that resolved target.

Ref resolution order for `IActionShaResolver`:

1. Attempt `refs/tags/{ref}` first.
2. If tag lookup returns not found, attempt `refs/heads/{ref}` as branch fallback.
3. If neither exists, resolution fails according to `network.on-error`.

Rationale:
- GitHub Actions ecosystem commonly uses moving branch aliases such as `v1`.
- Without branch fallback, `min-age-days: 0` still cannot pin `owner/repo@v1` when `v1` is a branch but not a tag.

#### 12.3.6 `fix.images.exclude-images` and `fix.images.exclude-tags`

Glob patterns for images and tags to skip during digest resolution.

- `scratch` is always excluded regardless of configuration (enforced by resolver, matching frizbee's `MergeUserConfig` safety invariant).
- `latest` is excluded by default (matches frizbee's default `ExcludeTags`). Rationale: pinning `latest` is semantically vacuous — it will drift immediately.

### 12.4 Resolution Caching

- Both resolvers must cache successful results in-process for the duration of a single remediation call.
- Cache key for `IActionShaResolver`: `(owner, repo, ref)`.
- Cache key for `IImageDigestResolver`: fully-qualified image reference string.
- Error results (non-skip, non-success) must not be cached to prevent false-negative propagation across files.
- Cache must be concurrency-safe.

### 12.5 Pin Fix Format

#### 12.5.1 Actions SHA Fix Format

An `unpinned-uses` diagnostic fix replaces the `@ref` portion of the `uses:` value:

- Before: `uses: actions/checkout@v6`
- After: `uses: actions/checkout@<sha40> # v6.0.2`

The separator between SHA and comment defaults to ` # ` (matches pinact's `separator` default). Comment usually follows the resolved ref string; for alias-like version refs (`vN`, `vN.M`) Seiton promotes the comment to the highest compatible concrete tag on the same commit when available (for example `v1` -> `v1.0.2`).

If the ref is already a 40-hex SHA, it is considered already pinned; no fix is generated.

**Edit anchor resolution:** Pin fixes replace the full `uses:` reference string (for example `actions/checkout@v6`), but the diagnostic `TextRange` may cover only the `@ref` suffix. When the same reference string appears multiple times in one file, edit offset resolution must use the diagnostic anchor (the `@ref` range start) to select the occurrence whose byte span contains that anchor—not the file's first textual match.

#### 12.5.2 OCI Digest Fix Format

An `unpinned-image` diagnostic fix appends `@sha256:<hex>` to the image reference, preserving the tag:

- Before: `image: node:20.11.1`
- After: `image: node:20.11.1@sha256:<hex>`
- Before: `uses: docker://ghcr.io/astral-sh/uv:latest`
- After: `uses: docker://ghcr.io/astral-sh/uv:latest@sha256:<hex>`

Tag is preserved (not replaced). This matches dockerfile-pin's output format. The digest is appended as `@sha256:...` so the image still references the same named tag, but is now content-addressed.

If the image reference already contains `@sha256:`, it is considered already pinned; no fix is generated.

### 12.6 Integration with Fix Catalog

When `fix.pinning.enable-network: true` and/or `fix.images.enable-network: true` and resolvers are injected:

| Rule ID | Fix Feasibility (with network) | Notes |
|---|---|---|
| `unpinned-uses` | ✓ Fixable (network-assisted) | Via `IActionShaResolver` |
| `unpinned-image` | ✓ Fixable (network-assisted) | Via `IImageDigestResolver` |

When `enable-network: false` (default), these rules remain ✗ Not auto-fixable as specified in §8.4.

### 12.7 Separation from Lint Contract

- `Check()` never performs network I/O; §8.3 is unchanged.
- `Remediate(diagnostics, resolvers)` is a separate entry point that accepts pre-collected diagnostics and resolver implementations.
- Callers are responsible for constructing and injecting resolver implementations; lint rules do not hold resolver references.
- The fix data model (§8.1) is unchanged — network-assisted fixes are `DiagnosticFix` values like any other.

### 12.8 Observability

When remediation is run:
- Callers can count how many `unpinned-uses`/`unpinned-image` diagnostics received fixes vs. were left without fix (skipped or failed).
- Skip reason (excluded by config) and failure reason (network error) must be distinguishable in the returned result.
- Resolver implementations should log resolution attempts at debug level and failures at warning level.
