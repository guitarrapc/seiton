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
- Detailed suppression/exclusion syntax and matching policy (reserved for future revision)

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
- Rule suppression/exclusion is a linter concern and must be specified in this document in a future update.

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

Reserved extension area (future):

- Rule exclusion/suppression policies (file-level, scope-level, inline comment-level)
- Additional policy filters and compliance controls

This section intentionally does not define exclusion rule details yet.

---

## 6. Diagnostic Processing Contract

Diagnostic processing in linter entrypoint must be deterministic.

1. Start with parser diagnostics from parse result.
2. Append rule diagnostics from active rule set.
3. Apply stable sort (rule priority, severity, position, message or equivalent deterministic key).
4. Deduplicate using deterministic diagnostic identity.
5. Apply final filtering phase.

Final filtering phase:

- Currently includes generic post-processing only.
- Future rule exclusion/suppression behavior will be defined here.

---

## 7. Cross-Document Consistency Rule

When this specification is revised, also review and update:

- `Docs/linter_implementation_csharp_plan.md`
- `Docs/Seiton_spec.md` when parser/linter boundary changed
