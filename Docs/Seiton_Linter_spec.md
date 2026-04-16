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
| `id-naming` | Error when `job.id` or `step.id` contains characters outside allowed identifier set. |
| `glob-pattern` | Error on invalid glob patterns in `on.<event>.branches/tags/paths` style filters. |
| `deny-write-all` | Error when workflow/job permissions use `write-all`; this rule is fail-safe constrained by §5.7. |
| `credentials` | Warn when custom/private registry images in `job.container` or `job.services.*` are used without credentials, except registries treated as public by built-in plus additive config set. |
| `template-injection` | Error when untrusted `github.event`-origin data is directly interpolated into `run`/`env` sinks in unsafe ways. |
| `expr-undefined-var` | Error when expressions reference context roots unavailable in the current scope (for example job scope vs step scope context mismatch). |
| `run-env-context-direct-use` | Error when `run:` script text directly references `${{ env.* }}`; shell variable expansion must be used instead. |

Rule set compatibility policy:

- Existing rule IDs are stable once published.
- Adding a new default rule requires this catalog to be updated in the same specification change.
- Removing or renaming a published rule ID is a breaking change and requires explicit migration guidance.

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

---

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

## 8. Normative Evaluation Sequence for Exclusion

Exclusion-aware lint evaluation sequence is fixed as follows.

1. Parse workflow and obtain parser diagnostics/AST.
2. Validate exclusion configuration and inline directive syntax (including unknown rule ID errors).
3. Build active rule set subject to non-disableable and minimum-severity constraints.
4. Execute rules and collect rule diagnostics.
5. Apply severity overrides.
6. Sort and deduplicate diagnostics.
7. Apply exclusion/suppression filtering using §5.2 precedence.
8. Emit final diagnostics and suppression observability data (§6.1).
