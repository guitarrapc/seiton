# CLI Spec Review: Findings and Recommended Updates

> Review of `.github/docs/Seiton_CLI_spec.md`, `.github/docs/Seiton_CLI_csharp_spec.md`, `.github/docs/Seiton_CLI_go_spec.md`.
> Scope: appropriateness of content, bloat, consistency, contract vs implementation separation.

---

## Summary

The base spec (`.github/docs/Seiton_CLI_spec.md`) is well-structured and mostly appropriate in scope. The language specs (`*_csharp_spec.md`, `*_go_spec.md`) have grown beyond "implementation contract" into "code documentation" territory — particularly in §6 (Command Implementation Details). This creates three problems:

1. **Maintenance burden** — internal type names and field layouts change frequently; spec must be updated each time.
2. **Attention dilution** — readers looking for the behavioral contract get distracted by internal struct definitions and concurrency primitives.
3. **DRY violations** — language specs repeat verbose output format/behavior already defined in the base spec, creating drift risk.

---

## Findings

### F1. Language specs §6 over-specifies internal details (HIGH)

**C# spec §6.1 CheckCommand** includes:

- `VerboseLogger` exposes `GetTimestamp()` and `GetElapsedTime(long start)` delegating to `TimeProvider`
- `FileCheckResult` carries `DocumentKind`, `FileElapsed`, `FileDiagnosticCount` (computed), and `FileSuppressedCount` (computed)
- `RuleActivationMetadata` captured via `Interlocked` — avoiding N redundant `string[]` snapshots

**Go spec §6.1** includes:

- Full struct definitions (`fileCheckResult`, `ruleActivationMetadata`) with every field
- `sync.Once or atomic CAS` capture strategy commentary

These are **code-level concerns**, not implementation contracts. Although `docs_authoring_guidelines.md` §1.2 is written for `Seiton_Linter_*` language specs, the same scoping principle applies to CLI language specs: record runtime-specific contracts and implementation notes that affect external behavior or cross-team coordination, not internal field names.

**Guideline**: If something changes and no external caller/test/output is affected, it doesn't belong in the spec.

### F2. Duplication between base spec and language specs (HIGH)

Both language specs repeat in §6.1:

- Verbose output format strings (`verbose: rules: <N> enabled, <M> disabled (workflow)`)
- Hint line conditions (when no `--min-severity`, errors zero, warnings non-zero)
- Interleaving behavior for parallel verbose mode
- Network fix hint conditions
- Per-file timing format

These are **already defined as contract in `.github/docs/Seiton_CLI_spec.md` §6.4**. Language specs should simply say "follows `.github/docs/Seiton_CLI_spec.md` §6.4" and only add language-specific deviations (e.g., "uses `TimeProvider` for testability" is a valid C#-specific note, but the format string itself is redundant).

### F3. Go spec §6.1 struct definitions are spec-inappropriate (MEDIUM)

The Go spec includes complete struct definitions:

```go
type fileCheckResult struct {
    diagnostics        []*Diagnostic
    filePath           string
    utf8Yaml           []byte
    documentKind       DocumentKind
    suppressionSummary SuppressionSummary
    fileElapsed        time.Duration
    fileDiagnosticCount int
    fileSuppressedCount int
}
```

This is source code, not specification. The contract is: "results are collected in file-input order for deterministic output." The internal struct shape is irrelevant to the contract.

### F4. Edit distance thresholds in §6.5 / base spec §2.6 (LOW)

Both language specs and the base spec mention exact thresholds: `≤1` for short (≤4 chars), `≤2` for medium (≤8 chars), `≤3` for long.

For the **base spec**, this is borderline appropriate — it ensures both implementations produce similar suggestions. But the exact numbers could live in a "recommended" note rather than normative text, since the user-visible contract is "suggest close matches" not "use these exact thresholds."

For the **language specs**, repeating the same thresholds adds nothing.

### F5. Base spec §6.1.1 rich format detail level (LOW)

The color coding rules and multi-line span rendering (`/ ... |___^`) are prescriptive but justified: both implementations must produce visually consistent output. This is **appropriate contract** because output format is user-facing behavior.

No change recommended.

### F6. C# spec §6.2 FixCommand internal details (MEDIUM)

- "8 passes per file to converge" — algorithm detail, not contract.
- "Separate HttpClient instances for GitHub API and OCI registry (different redirect policies)" — implementation choice.
- "Copies diagnostics immediately after Check()" — engine lifecycle detail.

The contract is: "fix converges to stable state; network resolution uses appropriate HTTP policies." Implementation doesn't need these internals documented in spec.

---

## Recommended Changes (Priority Order)

### P1. Trim language specs §6 to behavioral contracts only (HIGH)

**Action**: Rewrite both `Seiton_CLI_csharp_spec.md` §6 and `Seiton_CLI_go_spec.md` §6 to:

1. State the **behavioral contract** (e.g., "parallel with deterministic output order").
2. State **language-specific design decisions** that affect testability or API surface (e.g., "ThreadLocal<LintEngine>" for C#, "errgroup with SetLimit" for Go).
3. **Remove** internal type field names, concurrency primitive choices, and internal method signatures that have no external-facing consequence.

Specifically remove:
- C#: `VerboseLogger` API surface, `FileCheckResult` field list, `RuleActivationMetadata` type details, Interlocked strategy.
- Go: `fileCheckResult` struct definition, `ruleActivationMetadata` struct definition, `sync.Once or atomic CAS` commentary.
- Both: "8 passes per file", separate HttpClient/http.Client redirect policies, Copy diagnostics lifecycle detail.

**Keep**:
- Parallelization strategy names (ThreadLocal vs errgroup) — these are design choices affecting test/debug.
- Sequential fast-path conditions.
- TestWriter injection pattern (affects test contract).

### P2. Eliminate verbose-format duplication (HIGH)

**Action**: In both language specs §6.1, replace repeated verbose output format descriptions with:

```
Verbose output format follows `.github/docs/Seiton_CLI_spec.md` §6.4.
```

Only add language-specific addenda (e.g., "C# uses `TimeProvider` for testable timing" or "Go uses `time.Now()` directly").

### P3. Remove Go struct definitions from spec (MEDIUM)

**Action**: Delete the `fileCheckResult` and `ruleActivationMetadata` struct code blocks from `Seiton_CLI_go_spec.md` §6.1. Replace with a prose statement of the behavioral invariant:

> Results are collected into a pre-allocated slice indexed by file position. Rule activation metadata is captured once per DocumentKind (at most 2 snapshots) to avoid redundant allocations.

### P4. Soften edit-distance thresholds in base spec (LOW)

**Action**: In `.github/docs/Seiton_CLI_spec.md` §2.6, change threshold statement from normative to recommended:

> **Recommended thresholds**: ≤1 for short options (≤4 chars), ≤2 for medium (≤8 chars), ≤3 for long. Implementations may tune these if suggestion quality improves.

Remove threshold repetition from language specs entirely (they add nothing beyond the base spec statement).

### P5. Trim C# §6.2 / Go §6.2 FixCommand internals (MEDIUM)

**Action**: Reduce FixCommand sections to:

1. Fix is always async (network I/O).
2. Fix loop converges iteratively (max passes is implementation detail — remove exact number).
3. Stdin is rejected.
4. Network remediation is constructed only when enabled.
5. `--check` takes precedence over `--dry-run`.

Remove: pass count, HttpClient/http.Client redirect policy details, diagnostic copy lifecycle.

---

## Non-Issues (Confirmed Appropriate)

| Section | Reason it's fine |
|---|---|
| Base spec §6.1.1 rich format rendering | User-visible output contract; both impls must match |
| Base spec §6.4 verbose format strings | Contract for deterministic output |
| Base spec §8 Example Invocations | Clarifies usage without bloat |
| Language spec §3 (Build/NativeAOT) | Genuinely language-specific, affects build pipeline |
| Language spec §4 (Entry Point) | Shows wiring pattern, useful for navigating implementation |
| Language spec §5 (Config Bridge) | Resolution logic is contract-adjacent; code snippets are concise |

---

## Expected Outcome After Changes

- Base spec: unchanged (or minimal threshold softening).
- C# spec §6: ~40% shorter. Focused on behavioral contracts + C#-specific design decisions.
- Go spec §6: ~50% shorter. No struct definitions. Focused on behavioral contracts + Go-specific patterns.
- Both language specs: no repeated verbose format strings.
- Maintenance cost: significantly reduced (internal refactors won't require spec edits).
