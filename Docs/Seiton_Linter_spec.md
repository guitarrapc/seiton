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

- Rule identifiers used by exclusion/suppression must use stable canonical IDs: `seiton-lint-rule-001`, `seiton-lint-rule-002`, ...
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

Inline suppression is next-line scoped.

- A directive comment applies only to the immediately following YAML line.
- The directive can target one or multiple rule IDs.
- Multiple rule ID format: comma-separated canonical IDs.

Canonical directive format:

```
# seiton-lint: disable-next-line seiton-lint-rule-001
# seiton-lint: disable-next-line seiton-lint-rule-001,seiton-lint-rule-014,seiton-lint-rule-120
```

Non-normative note: parsers may allow optional spaces after commas, but normalized output must preserve canonical ID matching behavior.

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
