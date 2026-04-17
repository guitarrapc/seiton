# Seiton Linter Specification

> Defines the language-agnostic linter contract for rule execution, lint configuration, and diagnostic processing.
> Detailed parser behavior is specified in `Seiton_Parser_spec.md`.

---

## 1. Scope

This specification defines linter responsibilities after parser output is available.

In scope:

- Rule execution model over workflow AST
- Rule traversal hooks and ordering
- Lint configuration surface
- Rule diagnostics aggregation and final output processing

Out of scope:

- YAML structural parsing algorithms
- AST data model definitions
- Parser-level data extraction for suppression comments (linter consumes parsed suppression directives)

---

## 2. Entry Point Contract

```
Check(utf8Yaml, filePath) -> LintResult
```

High-level behavior:

1. Call parser entrypoint `Parse(utf8Yaml, filePath)`.
2. If parser has fatal error or no workflow AST, return parser diagnostics as lint result.
3. Build active rule set.
4. Traverse AST and invoke rule callbacks.
5. Collect rule diagnostics.
6. Sort, deduplicate, and filter diagnostics.
7. Return final `LintResult`.

---

## 3. Parser/Linter Boundary

- Parser owns AST construction and parser diagnostics.
- Linter owns rule execution and rule-originated diagnostics.
- Linter must consume parser output and must not re-implement YAML structural parsing.
- Rule suppression/exclusion is a linter concern and is specified in this document.

---

## 4. Rule Execution Model

Canonical pass traversal sequence:

`WorkflowPre -> Event -> JobPre -> Step -> JobPost -> WorkflowPost`

### 4.1 Pass Hooks

A pass exposes the following callbacks:

- `VisitWorkflowPre(workflow)`
- `VisitWorkflowPost(workflow)`
- `VisitEvent(event)`
- `VisitJobPre(job)`
- `VisitJobPost(job)`
- `VisitStep(step)`

### 4.2 Traversal Order

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

### 4.3 Rule Contract

Rule extends pass callbacks and provides:

- `Id`
- `Name`
- `SetConfig(config)`
- `GetDiagnostics()`

Rules collect diagnostics internally during traversal and return them after traversal completes.

### 4.4 Normative Rule Catalog

The default linter profile must include the following rule IDs.

| Rule ID | Required Behavior Summary |
|---|---|
| `job-structure` | Validate core job shape constraints: `uses` is mutually exclusive with `steps`/`runs-on`, and each job requires either reusable-call form (`uses`) or executable form (`runs-on` + `steps`). |
| `reusable-workflow` | Validate reusable workflow call semantics: `with`/`secrets` require `uses`, and reusable-call jobs must reject incompatible execution keys. |
| `permissions` | Validate `permissions` value domain: scalar must be `read-all` or `write-all`; scope values must be `read`, `write`, or `none`. |
| `popular-action-inputs` | Validate known action input names against maintained popular-action metadata and emit diagnostics for unknown inputs. |
| `unpinned-uses` | Warn when `uses:` references are not pinned to full commit SHA for remote actions/reusable workflows. |
| `unpinned-image` | Warn when docker image references (`docker://`, `job.container.image`, `job.services.*.image`) are not pinned by digest (`@sha256:<64-hex>`). |
| `dangerous-triggers` | Warn when dangerous trigger events are used (built-in dangerous event set plus any additive customization defined by config). |
| `job-permissions-required` | Warn when a job omits explicit `permissions` configuration. |
| `needs-graph` | Error on invalid `needs` graph: unknown dependency targets and circular dependencies. |
| `shell-name` | Error when configured shell names are outside the supported shell set for workflow/job defaults and `run` steps. |
| `runner-label` | Warn on unknown GitHub-hosted runner labels in `runs-on` (excluding self-hosted and expression-only cases), using built-in labels plus additive config labels. |
| `runner-no-latest` | Warn when moving GitHub-hosted labels (`ubuntu-latest`, `windows-latest`, `macos-latest`) are used in `runs-on`; prefer explicit version-pinned labels. |
| `id-naming` | Error when `job.id` or `step.id` contains characters outside allowed identifier set. |
| `glob-pattern` | Error on invalid glob patterns in `on.<event>.branches/tags/paths` style filters. |
| `deny-write-all` | Error when workflow/job permissions use `write-all`; this rule is fail-safe constrained by §5.7. |
| `credentials` | Warn when custom/private registry images in `job.container` or `job.services.*` are used without credentials, except registries treated as public by built-in plus additive config set. |
| `template-injection` | Error when untrusted `github.event`-origin data is directly interpolated into `run`/`env` sinks in unsafe ways. |
| `expr-undefined-var` | Error when expressions reference context roots unavailable in the current scope (for example job scope vs step scope context mismatch). |
| `run-env-context-direct-use` | Error when `run:` script text directly references `${{ env.* }}`; shell variable expansion must be used instead. |
| `run-secrets-context-direct-use` | Error when `run:` script text directly references `${{ secrets.* }}`; secret values should be mapped via `env` and referenced as shell variables (`${ENV_NAME}` / `$ENV_NAME` / `$env:ENV_NAME`). |
| `run-inputs-context-direct-use` | Error when `run:` script text directly references `${{ inputs.* }}` or `${{ github.event.inputs.* }}`; values should be mapped via `env` and referenced as shell variables (`${ENV_NAME}` / `$ENV_NAME` / `$env:ENV_NAME`). |
| `secrets-whole-context-access` | Error when any expression references the entire `secrets` context as an object (e.g. `${{ toJson(secrets) }}`, `${{ format('{0}', secrets) }}`), rather than accessing a specific secret key (`secrets.MY_KEY`). Exposing the whole secrets object in one expression leaks all secrets simultaneously. |
| `checkout-persist-credentials` | Warn when `actions/checkout` does not explicitly set `with.persist-credentials: false`; persisting credentials in `.git/config` increases secret exposure risk when repository data is reused or uploaded. |
| `known-vulnerable-actions` | Error when `uses:` references resolve to known vulnerable action versions (for example via GitHub Security Advisory metadata or curated vulnerability dataset). |
| `impostor-commit` | Error when a SHA-pinned `uses:` reference points to a commit that is not reachable in the referenced repository's graph for the intended ref semantics. |
| `ref-confusion` | Error when a symbolic ref in `uses:` (tag/branch) is ambiguous or confusion-prone (for example same name present in both tag and branch namespaces) under resolution policy. |
| `stale-action-refs` | Warn when SHA-pinned `uses:` references are stale relative to maintained release/tag mapping policy. |
| `deny-read-all` | Error when workflow/job permissions use `read-all`; callers must use explicit least-privilege scope mapping instead of blanket read grants. |
| `deny-inherit-secrets` | Error when reusable-workflow call jobs use `secrets: inherit`; full secret inheritance is forbidden under strict policy profile. |
| `job-timeout-minutes-required` | Error when executable jobs omit `timeout-minutes` (or equivalent compliant per-step timeout policy), to avoid unbounded runner execution. |
| `github-app-token-inputs` | Error when known GitHub App token actions are missing repository/permission-limiting inputs (for example `repositories`, `permissions`, `permission-*`). |

Rule set compatibility policy:

- Existing rule IDs are stable once published.
- Adding a new default rule requires this catalog to be updated in the same specification change.
- Removing or renaming a published rule ID is a breaking change and requires explicit migration guidance.
- Network-assisted rule IDs may be emitted by an opt-in post-lint audit entrypoint instead of the default local AST pass, but they still participate in shared rule-id, priority, suppression, and fixability catalogs.

### 4.5 Rule Guidance (Operational)

This section provides operator-facing guidance for each default rule.

- Scope: practical interpretation of rule intent, expected trigger patterns, remediation direction, and post-fix caution.
- Relationship to §4.4: §4.4 remains the normative source of rule IDs and required behavior. This section is explanatory and operational.
- Auto-fix status here follows §8.4 (including partial-fix boundaries).

| Rule ID | Rule Overview | Effective Pattern Examples | Why This Rule Is Needed | Preferred Remediation | Auto-Fix | Residual Risk and Recommended Response |
|---|---|---|---|---|---|---|
| `job-structure` | Enforces valid job shape (`uses` vs executable job keys). | Job contains both `uses` and `steps`; executable job missing `runs-on` or `steps`. | Prevents invalid workflow topology and ambiguous execution intent. | Split reusable-call jobs from executable jobs; ensure each executable job has `runs-on` and `steps`. | ✗ | Even after structural repair, re-check permissions and dependency flow (`needs`) for least privilege. |
| `reusable-workflow` | Validates reusable workflow call semantics and forbidden key combinations. | `with`/`secrets` without `uses`; reusable-call job with `steps`, `container`, `runs-on`, etc. | Avoids invalid call contracts and execution-context confusion. | Add `uses` when passing `with`/`secrets`; remove incompatible execution keys from call jobs. | ✗ | After edits, verify called workflow input/secret contracts and permission inheritance behavior. |
| `permissions` | Validates permission scalar/scope value domain. | Invalid scalar (`admin-all`), invalid scope value (`contents: admin`). | Prevents malformed permission config and silent policy drift. | Use `read-all`/`write-all` scalar or valid per-scope values (`read`/`write`/`none`). | △ Partial | Valid syntax does not guarantee safe scope. Review actual minimum scopes required by each job. |
| `popular-action-inputs` | Detects unknown input names for maintained popular actions. | Typo input for `actions/checkout` (`fetch-depht`). | Prevents no-op/ignored inputs and false security assumptions. | Correct input names to action-defined keys; pin action version and re-check release notes if key changed. | ✗ | Correct spelling alone may not preserve behavior across action major versions; confirm action docs. |
| `unpinned-uses` | Warns when action/reusable references are not full SHA pinned. | `uses: owner/repo@v4`, `@main`. | Reduces supply-chain risk from mutable refs. | Pin to 40-char commit SHA; retain tag in comment for readability. | ✗ (default), ✓ (network-assisted remediation phase) | SHA pinning still trusts upstream commit. Add provenance controls and update cadence policy. |
| `unpinned-image` | Warns when container image refs are not digest pinned. | `docker://repo/image:tag`, `container.image: repo/image:latest`. | Prevents mutable-tag drift and image substitution risk. | Pin images with `@sha256:<digest>` for deterministic pulls. | ✗ (default), ✓ (network-assisted remediation phase) | Digest pinning does not validate image trust posture. Add signature/attestation verification policy. |
| `dangerous-triggers` | Flags high-risk trigger events. | `pull_request_target`, `workflow_run` from untrusted context. | These events often execute with elevated trust boundaries. | Restrict trigger scope, add strict condition guards, or replace with safer events. | ✗ | Trigger hardening is insufficient without command/data sanitization in downstream steps. |
| `job-permissions-required` | Requires explicit job-level permissions declaration. | Job omits `permissions:`. | Prevents unintended default token scope inheritance. | Add explicit `permissions` mapping per job with least privilege. | ✓ | Explicit map can still be over-privileged. Review each scope against actual API calls. |
| `needs-graph` | Validates dependency graph integrity. | Unknown `needs` target, self-cycle, multi-job cycle. | Prevents deadlock/invalid scheduling and unclear execution order. | Fix job IDs, remove cycles, and redesign dependency boundaries. | ✗ | Graph correctness does not ensure artifact safety. Review cross-job data exposure channels. |
| `shell-name` | Validates shell identifiers in defaults and run steps. | Unsupported shell string (`fish` where unsupported). | Prevents runtime mismatch and script portability issues. | Use supported shell names or adjust script to runtime-supported shell. | ✗ | Supported shell still may differ by runner image. Validate commands on target runner matrix. |
| `runner-label` | Warns on unknown hosted runner labels. | `runs-on: ubuntu-9999` or mistyped hosted label. | Prevents queue/runtime failures from invalid labels. | Use known hosted labels or explicit self-hosted labels intentionally. | ✗ | Known label can still be policy-incompatible (cost/compliance). Align with org runner policy. |
| `runner-no-latest` | Discourages moving `*-latest` labels. | `ubuntu-latest`, `windows-latest`, `macos-latest`. | Reduces breakage from implicit platform upgrades. | Use explicit versioned labels (for example `ubuntu-24.04`). | ✗ | Version pinning still requires lifecycle updates. Track runner deprecation announcements. |
| `id-naming` | Enforces safe identifier charset for job/step IDs. | IDs with spaces or symbols outside `[a-zA-Z0-9_-]`. | Avoids reference ambiguity and downstream expression fragility. | Rename IDs to stable slug-style values. | △ Partial | ID rename can break references (`needs`, `steps.<id>`). Update all dependent expressions. |
| `glob-pattern` | Validates trigger filter glob syntax. | `***`, unmatched `[`/`]` in branch/path filters. | Prevents unintentionally broad/narrow trigger scope. | Correct glob syntax and validate trigger behavior against expected refs/paths. | ✗ | Syntax-correct patterns can still be overly broad. Add tests for expected trigger matrix. |
| `deny-write-all` | Fail-safe rule forbidding `write-all` permissions. | Workflow/job uses `permissions: write-all`. | Enforces hard least-privilege baseline and prevents blanket write grants. | Replace with `read-all` or explicit minimal scopes. | ✓ | Reduced scopes can break required write operations; add explicit targeted scopes where needed. |
| `credentials` | Warns when private/custom registry images lack credentials config. | `container.image` or `services.*.image` points to private host without `credentials`. | Prevents pull failures and accidental fallback assumptions. | Add proper `credentials` or move to approved public registry. | ✗ | Credential presence is not credential safety. Ensure secret storage, rotation, and least scope. |
| `template-injection` | Detects unsafe direct interpolation of untrusted event data. | `run`/`env` directly embeds `github.event.*` user-controlled fields. | Mitigates command/script injection and unsafe template expansion. | Use safe indirection (`env` mapping, strict quoting, validation/sanitization). | ✗ | Sanitization defects may remain. Add allowlist validation and escape-by-context patterns. |
| `expr-undefined-var` | Detects context roots unavailable in current expression scope. | Job-level expression uses `steps.*`; invalid root for that location. | Prevents silent logic errors and brittle condition behavior. | Replace with scope-valid contexts or restructure where data is produced/consumed. | ✗ | Scope-valid expression can still be semantically wrong. Add tests for condition truth tables. |
| `run-env-context-direct-use` | Disallows `${{ env.* }}` direct expansion inside `run`. | `run: echo "${{ env.VERSION }}"`. | Avoids timing/context confusion and unsafe interpolation style. | Map to shell variables and reference as shell-native syntax. | ✓ | Variable source may still be untrusted. Apply quoting and input validation in shell commands. |
| `run-secrets-context-direct-use` | Disallows direct `${{ secrets.* }}` usage inside `run`. | `run: curl -H "Auth: ${{ secrets.TOKEN }}" ...`. | Reduces accidental secret exposure in command rendering/logging paths. | Map secrets into `env` and use shell variables with careful redaction handling. | △ Partial | Partial auto-fix applies only when a unique existing `env` mapping points to the same secret key. Secret can still leak via command args, process lists, or logs; prefer stdin/files where possible. |
| `run-inputs-context-direct-use` | Disallows direct `${{ inputs.* }}` in `run`. | `run: do-something "${{ inputs.target }}"`. | Inputs may be user-controlled and unsafe in shell context. | Map via `env`, validate/normalize input, then consume as shell variable. | △ Partial | Partial auto-fix applies only when a unique existing `env` mapping points to the same input key. Validation gaps can still permit injection; use strict allowlists and safe argument passing. |
| `secrets-whole-context-access` | Detects whole-object access to secrets context. | `${{ toJson(secrets) }}`, `${{ format('{0}', secrets) }}`. | Prevents bulk secret exfiltration through single expression sink. | Replace with explicit key-level access to only required secrets. | ✗ | Key-level access can still leak if routed to unsafe sinks. Review sinks and masking behavior. |
| `checkout-persist-credentials` | Requires explicit `with.persist-credentials: false` for checkout hardening. | Missing key, `persist-credentials: true`, or non-deterministic expression value. | Prevents leaving checkout token credentials in `.git/config` during later workflow steps. | Set `with.persist-credentials: false`; configure explicit auth only where later git operations need it. | △ Partial | After fix, downstream authenticated git operations may fail. For `git push`, configure explicit auth (for example set remote URL with token or use dedicated credential helper step), then validate push path safely. |
| `known-vulnerable-actions` | Detects action versions with known vulnerabilities. | `uses: owner/repo@vX` where `vX` is listed in advisory dataset. | Prevents introducing known-compromised versions into CI/CD path. | Upgrade/pin to non-vulnerable commit or fixed release line. | ✗ | Advisory feeds can lag; combine with pinning and provenance verification. |
| `impostor-commit` | Detects SHA pins that are not valid commits for the referenced repository lineage. | `uses: owner/repo@<sha>` where `<sha>` is not reachable as expected. | Mitigates ghost/impostor commit supply-chain abuse. | Replace with verified commit from trusted tag/release mapping. | ✗ | Network/offline data freshness affects certainty; treat as high-severity policy signal. |
| `ref-confusion` | Detects ambiguous branch/tag symbolic references in `uses:`. | Same symbolic name exists in both refs/tags and refs/heads. | Prevents ref namespace confusion and unexpected resolution targets. | Use explicit full SHA pin, or enforce ref-namespace disambiguation policy. | ✗ | Ambiguity may be intentional in rare repos; allow explicit suppression with justification. |
| `stale-action-refs` | Detects stale SHA pins against maintained release/tag mapping. | Pinned SHA no longer corresponds to expected maintained tag line. | Keeps pinned dependencies current while preserving deterministic refs. | Move pin to current approved SHA for intended release family. | ✗ | Aggressive update cadence can cause churn; use policy thresholds/min-age controls. |
| `deny-read-all` | Forbids `read-all` permissions baseline. | Workflow/job uses `permissions: read-all`. | Enforces strict least privilege and explicit scope declaration. | Replace with explicit scope map (`contents: read` etc.). | ✓ | Over-tightening may break workflows; validate required read scopes explicitly. |
| `deny-inherit-secrets` | Forbids `secrets: inherit` in reusable workflow calls. | Reusable call job declares `secrets: inherit`. | Prevents broad secret propagation across workflow boundaries. | Map only required secrets explicitly under `secrets:`. | ✗ | Explicit mapping can still overshare; periodically review call-site contracts. |
| `job-timeout-minutes-required` | Requires timeout on executable jobs. | Job missing `timeout-minutes` and no equivalent policy exception. | Prevents runaway jobs and unexpected runner cost/exhaustion. | Add `timeout-minutes` per job or enforce approved per-step timeout policy. | △ Partial | Partial auto-fix applies only when `LintConfig.DefaultJobTimeoutMinutesForFix` is configured. Timeout values may still be mis-sized; monitor failures and tune thresholds. |
| `github-app-token-inputs` | Requires scoped inputs for GitHub App token actions. | `actions/create-github-app-token` or `tibdex/github-app-token` without repo/permission limits. | Reduces over-broad app token issuance. | Add `repositories` and permission-limiting inputs (`permissions`, `permission-*`). | ✗ | Action interface changes may require metadata updates in rule dataset. |

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

### 5.1 Rule Identifier Contract

- Rule identifiers used by exclusion/suppression should use semantic IDs (for example: `job-permissions-required`) as the primary format.
- Stable canonical IDs (`seiton-lint-rule-001`, `seiton-lint-rule-002`, ...) are accepted for backward compatibility.
- Canonical IDs are immutable once published.
- If a human-readable rule name changes, canonical ID must remain unchanged.
- Breaking-change migration note: `reusable-workflow-secrets-inherit` has been removed and replaced by `deny-inherit-secrets`. Suppressions/config using the removed ID must migrate to `deny-inherit-secrets`.
- Unknown rule IDs in config or inline directives are configuration errors.

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
- Exclusion entries may include optional `jobId` condition (see §5.4).

### 5.4 Job-Level Exclusion (Configuration)

- Job scoping uses `job.id` only.
- Job `name` is not a matching key for exclusion.
- For reusable workflow call jobs (`uses:` at job level), matching is evaluated only against the caller workflow job in the current file.
- Seiton does not traverse into the referenced reusable workflow file for caller-file exclusion matching.

### 5.5 Inline Exclusion Directive

Inline suppression supports file/job/next-line scopes.

- `disable-next-line` applies only to the immediately following YAML line.
- `disable-job` applies to diagnostics inside the specified `job.id` scope.
- `disable-file` applies to all diagnostics in the current workflow file.
- A directive can target one or multiple rule IDs.
- Multiple rule ID format is comma-separated; semantic IDs are recommended.

Canonical directive format:

```
# seiton: disable-next-line job-permissions-required
# seiton: disable-job build job-permissions-required,credentials
# seiton: disable-file dangerous-triggers,job-permissions-required
```

Non-normative note: parsers may allow optional spaces after commas, but normalized output must preserve rule-id matching behavior.

### 5.6 Audit Metadata Policy

- Suppression reason text is optional and not required by contract.
- Expiration (`expires`) is not required by contract.
- Implementations may support optional metadata fields, but must not require them for valid exclusion entries.

### 5.7 Fail-Safe Rule Policy

Linter contract supports mandatory safety constraints on selected rules.

- Some rules may be marked non-disableable.
- Some rules may define minimum severity.
- If config or inline directives attempt to disable a non-disableable rule, linter must emit configuration error.
- If config attempts to set severity lower than rule minimum severity, linter must emit configuration error.
- Severity order is `Error > Warning > Info`.

### 5.8 Rule-Specific Additive Customization

Linter contract supports additive rule customization for selected rules.

- Custom entries are merged with built-in defaults; built-in defaults are not removed by this contract.
- Merge behavior is set union (`effective = built-in U custom-added`) with deterministic deduplication.
- Duplicate entries after normalization are ignored.
- Invalid custom entries must produce configuration error with enough location/context for users to fix input.

Non-normative example configuration shape:

```yaml
rules:
  dangerous-triggers:
    additionalDangerousEvents:
      - issue_comment
      - pull_request_review_comment

  runner-label:
    additionalKnownHostedLabels:
      - ubuntu-24.04-arm
      - windows-2025-vs2026

  credentials:
    additionalPublicRegistries:
      - registry.example.com
      - mirror.example.net:5000
```

#### 5.8.1 `dangerous-triggers` Additional Events

- `additionalDangerousEvents` allows users to add event names that are treated as dangerous by the `dangerous-triggers` rule.
- Matching uses normalized event names (ASCII lower-case); configuration values should use canonical GitHub event naming.
- If a configured event is present in workflow `on`, rule emits the same diagnostic/severity behavior as built-in dangerous events.

#### 5.8.2 `runner-label` Additional Known Labels

- `additionalKnownHostedLabels` allows users to add runner labels treated as known GitHub-hosted labels for `runner-label` rule evaluation.
- Matching uses normalized label values (ASCII lower-case).
- Labels added here suppress only `runner-label` unknown-label diagnostics; they do not alter parsing or execution semantics.

#### 5.8.3 `credentials` Additional Public Registries

- `additionalPublicRegistries` allows users to add registry hosts treated as public/credential-optional by the `credentials` rule.
- Entry unit is registry host (`host` or `host:port`), without scheme and path.
- Matching uses normalized host values (ASCII lower-case).
- When image registry host matches this merged public-registry set, missing credentials does not produce `credentials` diagnostics.

### 5.9 Complete Example Configuration File

The following YAML shows a complete non-normative example of configuration-file based linter settings.

```yaml
rules:
  job-permissions-required:
    enabled: false

  dangerous-triggers:
    severity: error
    additionalDangerousEvents:
      - issue_comment
      - pull_request_review_comment

  runner-label:
    additionalKnownHostedLabels:
      - custom-large
      - ubuntu-24.04-arm

  credentials:
    additionalPublicRegistries:
      - registry.example.com
      - mirror.example.net:5000

  shell-name:
    severity: warning

exclusions:
  - filePattern: ".github/workflows/legacy/*.yml"
    ruleIds:
      - dangerous-triggers
      - job-permissions-required

  - filePattern: ".github/workflows/release.yml"
    jobId: publish
    ruleIds:
      - credentials
```

Interpretation notes:

- `rules.<rule-id>.enabled` controls rule enable/disable, subject to fail-safe constraints in §5.7.
- `rules.<rule-id>.severity` overrides diagnostic severity, subject to fail-safe constraints in §5.7.
- `rules.dangerous-triggers.additionalDangerousEvents`, `rules.runner-label.additionalKnownHostedLabels`, and `rules.credentials.additionalPublicRegistries` are additive extensions defined in §5.8.
- `exclusions[].filePattern` and optional `exclusions[].jobId` define config-based suppression scope.
- `exclusions[].ruleIds` accepts one or more semantic rule IDs; canonical IDs remain accepted for backward compatibility per §5.1.
- Inline directives such as `# seiton: disable-next-line ...` are not part of the config file YAML; they are written inside workflow source files and are specified separately in §5.5.

### 5.10 Recommended Config File Name and Location

Because Seiton targets GitHub Actions workflow repositories, the recommended config file location is under `.github/`.

Recommended file path (primary):

- `.github/seiton.yaml`

Accepted alternate file names:

- `.github/seiton.yml`
- `seiton.yaml`
- `seiton.yml`

Recommended discovery order (when no explicit config path is provided):

1. `.github/seiton.yaml`
2. `.github/seiton.yml`
3. `seiton.yaml`
4. `seiton.yml`

Explicit-config precedence recommendation:

- If runtime/CLI provides an explicit config path option (for example `--config <path>`), that file should be used as the only config source for that lint invocation.
- If explicit config is not provided, runtimes may use the discovery order above.

Rationale (non-normative):

- actionlint uses `.github/actionlint.yaml` / `.github/actionlint.yml` as repository config locations.
- zizmor discovers `.github/zizmor.yml` / `.github/zizmor.yaml` before root-level names.
- ghalint accepts both root-level and `.github/` config names.
- Prioritizing `.github/` keeps workflow-related policy close to workflow files and avoids ambiguity with other root-level YAML files.

---

## 6. Diagnostic Processing Contract

Diagnostic processing in linter entrypoint must be deterministic.

1. Start with parser diagnostics from parse result.
2. Append rule diagnostics from active rule set.
3. Apply stable sort (rule priority, severity, position, message or equivalent deterministic key).
4. Deduplicate using deterministic diagnostic identity.
5. Apply final filtering phase.

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

---

## 7. Cross-Document Consistency Rule

When this specification is revised, also review and update:

- `Docs/linter_implementation_csharp_plan.md`
- `Docs/Seiton_spec.md`
- `Docs/Seiton_Parser_spec.md` when parser/linter boundary changed

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

### 8.4 Fixable Rule Catalog

The following table classifies each default rule by fix feasibility.

| Rule ID | Fix Feasibility | Fix Description |
|---|---|---|
| `deny-write-all` | ✓ Fixable | Replace `write-all` scalar with `read-all` in the permissions node. |
| `run-env-context-direct-use` | ✓ Fixable | Replace `${{ env.VAR }}` with `$VAR` (or `${VAR}` for POSIX shells) inside `run:` text. |
| `job-permissions-required` | ✓ Fixable | Insert `permissions: {}` as a new key immediately after `runs-on:` (or after job id key if `runs-on` is absent). |
| `unpinned-uses` | ✗ Not auto-fixable | Requires resolving current SHA for the referenced action/workflow at fix time (external I/O). |
| `unpinned-image` | ✗ Not auto-fixable | Requires resolving current digest for the referenced image at fix time (external I/O). |
| `dangerous-triggers` | ✗ Not auto-fixable | Correct replacement is semantic (remove event, or restructure trigger) and context-dependent. |
| `permissions` | △ Partial | For scalar form only: replace invalid scalar with `read-all`. Scope value corrections are ambiguous (correct value is context-dependent). |
| `job-structure` | ✗ Not auto-fixable | Structural problems (missing `runs-on`, conflicting keys) require user intent to resolve. |
| `reusable-workflow` | ✗ Not auto-fixable | Forbidden key removal requires user to confirm intent. |
| `popular-action-inputs` | ✗ Not auto-fixable | Closest valid input name may be suggested in diagnostic message but must not be applied automatically. |
| `needs-graph` | ✗ Not auto-fixable | Unknown dependency target or cycle requires user to determine correct dependency. |
| `shell-name` | ✗ Not auto-fixable | Correct shell name is ambiguous; user must select. |
| `runner-label` | ✗ Not auto-fixable | Closest known label may be suggested but apply is ambiguous. |
| `runner-no-latest` | ✗ Not auto-fixable | Replacing `*-latest` with a concrete runner version requires repository policy/compatibility intent. |
| `id-naming` | △ Partial | Replace invalid characters with `-` for `job.id` and `step.id` only when single invalid character substitution is unambiguous. |
| `glob-pattern` | ✗ Not auto-fixable | Glob correction requires understanding user intent. |
| `credentials` | ✗ Not auto-fixable | Adding credentials requires secrets names that are not known to linter. |
| `template-injection` | ✗ Not auto-fixable | Safe remediation patterns (env variable indirection, `toJSON()`) are context-dependent. |
| `expr-undefined-var` | ✗ Not auto-fixable | Correct context variable cannot be inferred automatically. |
| `run-secrets-context-direct-use` | △ Partial | Replace simple `${{ secrets.KEY }}` / `${{ secrets['KEY'] }}` in `run:` only when exactly one existing `env` variable maps to the same secret key; ambiguous/no-mapping cases remain no-fix. |
| `run-inputs-context-direct-use` | △ Partial | Replace simple `${{ inputs.KEY }}` / `${{ github.event.inputs.KEY }}` (and bracket forms) in `run:` only when exactly one existing `env` variable maps to the same input key; ambiguous/no-mapping cases remain no-fix. |
| `secrets-whole-context-access` | ✗ Not auto-fixable | Correct remediation (refactoring to specific key access) requires user intent about which secrets are needed. |
| `checkout-persist-credentials` | △ Partial | For deterministic cases, insert or replace `with.persist-credentials: false`. Expression-valued cases remain no-fix. Review downstream authenticated git commands such as `git push`, which may need explicit auth setup (for example `git remote set-url origin ...`). |
| `known-vulnerable-actions` | ✗ Not auto-fixable | Selecting a safe replacement version/commit requires advisory-aware upgrade policy and user intent. |
| `impostor-commit` | ✗ Not auto-fixable | Safe replacement SHA requires trusted repository graph/advisory resolution outside deterministic local edit. |
| `ref-confusion` | ✗ Not auto-fixable | Correct disambiguation (tag vs branch vs SHA) depends on project policy and intent. |
| `stale-action-refs` | ✗ Not auto-fixable | Updating stale pins requires repository/version policy and may change runtime behavior. |
| `deny-read-all` | ✓ Fixable | Replace `read-all` scalar with an explicit empty mapping baseline (`{}`) or configured least-privilege template when deterministic. |
| `deny-inherit-secrets` | ✗ Not auto-fixable | Determining exact secret pass-through list requires user intent and callee contract knowledge. |
| `job-timeout-minutes-required` | △ Partial | Insert `timeout-minutes: <default>` at job level only when `LintConfig.DefaultJobTimeoutMinutesForFix` is configured. |
| `github-app-token-inputs` | ✗ Not auto-fixable | Required repository/permission scopes cannot be inferred safely without repository policy context. |

### 8.5 Fix Safety Policy

- A fix must be semantically equivalent for the common case; it must not silently change runtime behavior in a way that is not obvious from its description.
- Unsafe transformations (for example, template-injection remediation that alters data flow) must not be provided as auto-fix; they may only appear as diagnostic message guidance.
- Fail-safe rules (§5.7) that are non-disableable must not offer fixes that would circumvent their enforcement (for example, `deny-write-all` fix replaces with `read-all`, not with suppression).

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

### 10.1 Dry-Run Diff Preview

Dry-run preview is an output-only operation for fix review.

- Dry-run must not modify source files.
- Output format should be unified diff style with hunk headers (for example `@@ -a,b +c,d @@`) and `-` / `+` line markers.
- Preview scope should be limited to changed hunks, not full-file dump.
- Implementations should include configurable nearby context lines around each change (recommended default: 1-3 lines).
- Output target is runtime-defined (for example standard output in CLI mode), but behavior must remain deterministic for identical inputs.

---

## 11. Normative Evaluation Sequence for Exclusion

Exclusion-aware lint evaluation sequence is fixed as follows.

1. Parse workflow and obtain parser diagnostics/AST.
2. Validate exclusion configuration and inline directive syntax (including unknown rule ID errors).
3. Build active rule set subject to non-disableable and minimum-severity constraints.
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
| Default excludes | `ignore_actions` (regex) | `ignore-images` (glob, negation) | `exclude_branches: [main, master]`; `scratch` always; `latest` by default |
| Separate command | `pinact run` | `dockerfile-pin run` | `frizbee actions` / `frizbee image` |
| Skip sentinel | — | — | `ErrReferenceSkipped` |

Design principles adopted for Seiton:
- Resolution is injected via an interface — not embedded in lint rules.
- Two separate resolver interfaces: one for GitHub Actions SHA, one for OCI image digest.
- Resolution is never called during `Check(utf8Yaml, filePath)`; only during an explicit `Remediate()` operation.
- Resolver caches results in-process to avoid redundant network calls across diagnostics.
- Resolver failures leave the diagnostic without a fix (`failOpen: true` behavior).

### 12.2 Resolver Interfaces

#### 12.2.1 `IActionShaResolver`

Resolves a GitHub Actions or Reusable Workflow reference to a pinned commit SHA.

```
Resolve(owner, repo, ref) -> (sha, tagComment, error)
```

- `owner`: repository owner (e.g. `actions`)
- `repo`: repository name (e.g. `checkout`)
- `ref`: tag, branch, or SHA string as it appears in the `uses:` value (e.g. `v4`, `main`)
- Returns: 40-hex SHA, original ref as comment string (e.g. `v4`), error
- Returns `(null, null, SkippedError)` when the ref is excluded by configuration (matches `ignore_actions` patterns).

#### 12.2.2 `IImageDigestResolver`

Resolves an OCI image reference to a pinned digest.

```
Resolve(imageRef) -> (digest, error)
```

- `imageRef`: fully-qualified image reference with tag (e.g. `node:20.11.1`, `ghcr.io/org/image:v1.2.3`)
- Returns: `sha256:<hex>` digest string, error
- Returns `(null, SkippedError)` when the image ref is excluded by configuration (matches `exclude_images` or `exclude_tags` patterns).

### 12.3 Configuration

Network-assisted pin remediation is disabled by default. It must be explicitly enabled via the Seiton configuration file.

```yaml
pin_resolution:
  allow_network: false             # must be true to enable remediation
  github_actions:
    token_env_vars:
      - SEITON_GITHUB_TOKEN
      - GITHUB_TOKEN
    ghes_api_url: ""               # optional; empty = github.com only
    ghes_fallback: false           # if true, fall back to github.com when repo not found on GHES
    ignore_actions:
      - name: "slsa-framework/.*"
        ref: ".*"
    exclude_branches:
      - main
      - master
    min_age_days: 14               # skip pinning tags created fewer than N days ago; 0 = no constraint
  images:
    exclude_images:
      - scratch
    exclude_tags:
      - latest
    ignore_images:
      - "mcr.microsoft.com/**"
  fail_open: true                  # if true, resolution failures leave diagnostic without fix
  request_timeout_sec: 30
  max_concurrency: 4
```

#### 12.3.1 `allow_network`

When `false` (the default), no resolver is instantiated and `unpinned-uses`/`unpinned-image` diagnostics carry no fix. When `true`, resolver implementations may be provided.

#### 12.3.2 `github_actions.token_env_vars`

Ordered list of environment variable names to check for a GitHub API token. The first non-empty value is used. If no variable yields a token, the GitHub API is called unauthenticated (lower rate limit). Rationale: tool-specific env var (`SEITON_GITHUB_TOKEN`) takes priority over the generic `GITHUB_TOKEN`, matching pinact's pattern.

#### 12.3.3 `github_actions.ghes_api_url` and `github_actions.ghes_fallback`

Optional support for GitHub Enterprise Server. When `ghes_api_url` is set, the resolver first queries the GHES instance. If `ghes_fallback: true`, repositories not found on GHES are retried against github.com. Matches pinact's `ClientResolver` pattern.

#### 12.3.4 `github_actions.ignore_actions`

List of name/ref patterns (regex) to skip during Actions SHA resolution. Equivalent to pinact's `ignore_actions`. Common use case: SLSA reusable workflows where the caller must not pin the SHA.

#### 12.3.5 `github_actions.exclude_branches`

Branch names (exact or regex) to never pin. Default: `["main", "master"]`. Matches frizbee's default behavior. Rationale: pinning a branch reference to its current SHA is semantically incorrect — the intent of a branch ref is to track the branch tip.

#### 12.3.5 `github_actions.exclude_branches`

Branch names (exact or regex) to never pin. Default: `["main", "master"]`. Matches frizbee's default behavior. Rationale: pinning a branch reference to its current SHA is semantically incorrect — the intent of a branch ref is to track the branch tip.

#### 12.3.6 `github_actions.min_age_days`

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

#### 12.3.8 `images.exclude_images` and `images.exclude_tags`

Glob patterns for images and tags to skip during digest resolution.

- `scratch` is always excluded regardless of configuration (enforced by resolver, matching frizbee's `MergeUserConfig` safety invariant).
- `latest` is excluded by default (matches frizbee's default `ExcludeTags`). Rationale: pinning `latest` is semantically vacuous — it will drift immediately.

#### 12.3.9 `fail_open`

When `true` (the default), resolution failures (network error, auth failure, timeout) leave the diagnostic without a fix rather than causing the remediation call to fail. Callers may inspect which diagnostics received fixes and which did not. When `false`, any resolution failure causes the remediation call to return an error.

### 12.4 Resolution Caching

- Both resolvers must cache successful results in-process for the duration of a single remediation call.
- Cache key for `IActionShaResolver`: `(owner, repo, ref)`.
- Cache key for `IImageDigestResolver`: fully-qualified image reference string.
- Error results (non-skip, non-success) must not be cached to prevent false-negative propagation across files.
- Cache must be concurrency-safe.

### 12.5 Pin Fix Format

#### 12.5.1 Actions SHA Fix Format

An `unpinned-uses` diagnostic fix replaces the `@ref` portion of the `uses:` value:

- Before: `uses: actions/checkout@v4`
- After: `uses: actions/checkout@<sha40> # v4`

The separator between SHA and comment defaults to ` # ` (matches pinact's `separator` default). Comment preserves the original ref string verbatim.

If the ref is already a 40-hex SHA, it is considered already pinned; no fix is generated.

#### 12.5.2 OCI Digest Fix Format

An `unpinned-image` diagnostic fix appends `@sha256:<hex>` to the image reference, preserving the tag:

- Before: `image: node:20.11.1`
- After: `image: node:20.11.1@sha256:<hex>`
- Before: `uses: docker://ghcr.io/astral-sh/uv:latest`
- After: `uses: docker://ghcr.io/astral-sh/uv:latest@sha256:<hex>`

Tag is preserved (not replaced). This matches dockerfile-pin's output format. The digest is appended as `@sha256:...` so the image still references the same named tag, but is now content-addressed.

If the image reference already contains `@sha256:`, it is considered already pinned; no fix is generated.

### 12.6 Integration with Fix Catalog

When `allow_network: true` and resolvers are injected:

| Rule ID | Fix Feasibility (with network) | Notes |
|---|---|---|
| `unpinned-uses` | ✓ Fixable (network-assisted) | Via `IActionShaResolver` |
| `unpinned-image` | ✓ Fixable (network-assisted) | Via `IImageDigestResolver` |

When `allow_network: false` (default), these rules remain ✗ Not auto-fixable as specified in §8.4.

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
