# Memory Allocation Improvement Plan

## Overview

This document summarizes the findings from a deep investigation of unnecessary memory allocations in `src/Seiton.Core/Parsing/`, `src/Seiton.Core/Linting/`, `src/Seiton.Core/Linting/Rules/`, and `src/Seiton.Core/Linting/Fixing/`. Each item is categorized by priority and includes the specific file, code pattern, impact, and suggested fix.

**Methodology:**
- Hot path = called per-step, per-job, per-expression, or per-scalar (N × hundreds)
- Warm path = called per-workflow or per-lint-run (N × ~1–20)
- Cold path = called once at init or only on error (negligible impact)

---

## Parser (`src/Seiton.Core/Parsing/`)

**Status: Well optimized.** The parser already uses `ArrayPool`, `stackalloc`, span-based key comparisons, and avoids string materialization on success paths. No high-priority allocation issues found.

---

## Linter Engine (`src/Seiton.Core/Linting/LintEngine.cs`)

### P1 — High Priority

| # | Location | Pattern | Hot Path? | Suggested Fix |
|---|----------|---------|-----------|---------------|
| L-1 | `LintEngine.cs:392` | `new SuppressionRecord[suppressionCount]` — per `Check()` call | Warm (per file) | Use a reusable `List<SuppressionRecord>` field; expose as `ReadOnlySpan` or copy only when caller retains (snapshot semantics already required for public API, so keep but document as intentional). **Low ROI if small counts.** |
| L-2 | `LintEngine.cs:398` | `new Dictionary<string, int>(..., StringComparer.Ordinal)` — per `Check()` call | Warm (per file) | Same as L-1. Snapshot semantics. Keep but document. |
| L-3 | `LintEngine.cs:741, 832` | `new Dictionary<string, SuppressionAnchor>(...)` inside suppression parsing loops | Warm | These are inner dictionaries created lazily. Consider pooling or reusing a `Dictionary` field (clear per job). Only actionable if workflows have many jobs. |

### P2 — Medium Priority

| # | Location | Pattern | Hot Path? | Suggested Fix |
|---|----------|---------|-----------|---------------|
| L-4 | `LintEngine.cs:888` | `_knownJobIdSlices = new Utf8Slice[count]` — only when count exceeds current capacity | Warm | Already amortized (geometric growth pattern). No change needed. |

---

## EditDistance (`src/Seiton.Core/Linting/EditDistance.cs`)

### P2 — Medium Priority

| # | Location | Pattern | Hot Path? | Suggested Fix |
|---|----------|---------|-----------|---------------|
| ED-1 | `EditDistance.cs:21-22` | `new int[right.Length + 1]` × 2 per call | Warm (per unknown input suggestion) | Use `stackalloc` when `right.Length + 1 <= 128`; otherwise use `ArrayPool<int>.Shared`. Callers are `PopularActionInputsRule` (per-unknown-input) and `RuleCatalog` (per-rule-id-typo). |

**Implementation:**

```csharp
public static int ComputeIgnoreCase(string left, string right)
{
    if (left.Length == 0) return right.Length;
    if (right.Length == 0) return left.Length;

    var len = right.Length + 1;
    int[]? rentedPrev = null, rentedCurr = null;
    Span<int> previous = len <= 128
        ? stackalloc int[len]
        : (rentedPrev = ArrayPool<int>.Shared.Rent(len)).AsSpan(0, len);
    Span<int> current = len <= 128
        ? stackalloc int[len]
        : (rentedCurr = ArrayPool<int>.Shared.Rent(len)).AsSpan(0, len);
    try
    {
        for (var j = 0; j <= right.Length; j++) previous[j] = j;
        // ... loop body unchanged ...
        return previous[right.Length];
    }
    finally
    {
        if (rentedPrev is not null) ArrayPool<int>.Shared.Return(rentedPrev);
        if (rentedCurr is not null) ArrayPool<int>.Shared.Return(rentedCurr);
    }
}
```

---

## ActionRefHelpers (`src/Seiton.Core/Linting/ActionRefHelpers.cs`)

### P2 — Medium Priority

| # | Location | Pattern | Hot Path? | Suggested Fix |
|---|----------|---------|-----------|---------------|
| AR-1 | `ActionRefHelpers.cs:500` | `new Dictionary<(int,int), bool>()` per `GlobMatch` call | Warm (per exclusion file match or pin remediation image ignore) | Create a `[ThreadStatic]` or field-level dictionary and clear per call. Alternatively, since GlobMatch is called only for exclusion matching (per-file, not per-step), mark as low priority. |
| AR-2 | `ActionRefHelpers.cs:138-139` | `Encoding.UTF8.GetString(ownerSpan/repoSpan)` in `TrySplitOwnerRepoFromActionPath` | Warm (per step with remote uses) | These are returned as `out string` for API/resolution. Acceptable for now but consider returning `Utf8Slice` in internal callers. |

---

## Rules — Per-Step/Per-Job Hot Paths

### P1 — High Priority

| # | Rule | Location | Pattern | Impact | Suggested Fix |
|---|------|----------|---------|--------|---------------|
| R-1 | `PopularActionInputsRule` | Lines 61-62, 69, 106 | `Encoding.UTF8.GetString(...)` per-input per-step for diagnostics | High (N steps × M inputs) | Only decode when actually emitting a diagnostic — the `Encoding.UTF8.GetString` calls are inside the diagnostic emission path, which is correct. However the `unknownInputName` decode on line 69 happens unconditionally for every unknown input. Consider deferring to message builder. **Actually acceptable** since this is only reached for unknown inputs (error path). Keep as-is. |
| R-2 | `IfCondRule` | Line 121 | `Encoding.UTF8.GetString(expression).Trim()` | Medium (per-constant-expression step/job) | Only called when expression is constant (error/warning path). Acceptable. |
| R-3 | `ExprUndefinedVarRule` | Lines 694, 728 | `Encoding.UTF8.GetString(rootName/funcName)` per-context-error | Medium | Only in error paths. Acceptable. |
| R-4 | `ScheduleEventRule` | Line 90 | `Generated.IanaTimeZones.IsKnown(Encoding.UTF8.GetString(span))` | Low-Medium | Called per-schedule entry. Consider adding a `IsKnown(ReadOnlySpan<byte>)` overload that uses UTF-8 comparison internally, avoiding string allocation on the happy path (timezone is valid). |
| R-5 | `NeedsGraphRule` | Line 67 | `new Dictionary<Utf8String, byte>(_knownJobs.Count)` per-workflow | Warm (per workflow, not per job) | Per-workflow. Low priority. Consider field-level dictionary cleared per workflow for repeated linting of the same engine instance. |
| R-6 | `NeedsGraphRule` | Line 147 | `stack.ToArray()` in `BuildCyclePath` | Cold (only on cycle detection) | Acceptable — only reached when a cycle exists. |

### P2 — Medium Priority

| # | Rule | Location | Pattern | Impact | Suggested Fix |
|---|------|----------|---------|--------|---------------|
| R-7 | `ForbiddenUsesRule` | Lines 90, 105 | `Encoding.UTF8.GetString(ownerRepoKey)` | Warm (per-denied step) | Only called when policy violation detected (diagnostic path). Acceptable. |
| R-8 | `CredentialsRule` | Line 80 | `Encoding.UTF8.GetString(host)` | Warm (per-private-registry container) | Only called for non-public registries (diagnostic path). Acceptable. |
| R-9 | `IdNamingRule` | Line 129 | `new List<TextEdit>()` per fix generation | Warm (per non-kebab job ID) | Only triggered for naming violations. Low impact. Could use `stackalloc` + fixed-size approach since max edits = 1 + N(needs refs), but List is fine for diagnostic path. |
| R-10 | `MatrixRule` | Line 369 | `new List<(string,string)>()` in `FormatRawYamlValue` | Cold (only for diagnostic formatting) | Acceptable. |
| R-11 | `JobPermissionsRequiredRule` | Line 154 | `new Dictionary<string, string>(...)` per job (for fix generation) | Warm (per job without permissions) | Only triggered when generating fix suggestions. Acceptable on diagnostic path. |
| R-12 | `PermissionsRule` | Line 85 | LINQ `.Select(v => $"\"{v}\"")` + `string.Join` | Cold (only on invalid permission value) | Acceptable — diagnostic-only path. |
| R-13 | `RunnerLabelRule` | Line 305 | `.OrderBy(...).Select(...)` + `string.Join` | Cold (only on config diagnostic) | Acceptable. |
| R-14 | `LocalActionInputsRule` | Line 335 | `new List<string>(declared.Count)` | Cold (only for unknown input diagnostics) | Acceptable. |

### P3 — Low Priority / Already Optimized

| # | Rule | Location | Pattern | Notes |
|---|------|----------|---------|-------|
| R-15 | `TemplateInjectionRule` | Line 17 | `static readonly string[][] untrustedPaths` | Static allocation — optimal. |
| R-16 | `OutdatedActionRunnerRule` | Lines 20-21 | `"node12"u8.ToArray()` as `static readonly` | Already promoted to static. Optimal. |
| R-17 | `IfExprWrapperRule` | Lines 170-193 | Message caching with `_lastSlice`/`_lastMessage` pattern | Already implements the deduplication cache. Excellent. |
| R-18 | `ExprUndefinedVarRule` | Lines 25-26 | Per-entity override arrays as fields | Already uses the reusable field pattern. Excellent. |
| R-19 | `ForbiddenUsesRule` | Lines 76-143 | `stackalloc` + `ArrayPool` for key buffer | Already optimized. |

---

## Fixer (`src/Seiton.Core/Linting/Fixing/FixEngine.cs`)

### P3 — Low Priority

| # | Location | Pattern | Impact | Suggested Fix |
|---|----------|---------|--------|---------------|
| F-1 | `FixEngine.cs:175, 193` | `new List<TextEdit>()` / `new List<DiagnosticFix>()` per `Apply` call | Cold (fix application is a one-shot operation) | Acceptable — fix application is not performance-critical. |
| F-2 | `FixEngine.cs:224` | `new byte[orderedEdits.Length][]` | Cold | Required for correctness — output buffer must be new since it's returned to caller. |
| F-3 | `FixEngine.cs:232` | `new byte[outputSize]` | Cold | Required — new output array is the return value. |
| F-4 | `FixEngine.cs:350-351` | `new int[ops.Count + 1]` × 2 for diff prefix arrays | Cold | Could use `stackalloc`/`ArrayPool` for very large diffs, but diff generation is a cold path (only on fix application). Low ROI. |
| F-5 | `FixEngine.cs:451` | `new int[oldCount + 1, newCount + 1]` LCS matrix | Cold | Classic LCS algorithm; 2D array is necessary. Could use `ArrayPool` for large inputs but fix diffs are typically small. |
| F-6 | `FixEngine.cs:527` | `new string[count]` in `SplitLines` | Cold | Fix path only. |

---

## Summary: Actionable Items by Priority

### Priority 1 (High Impact, Low Risk)

| ID | Change | Expected Impact | Verification |
|----|--------|----------------|--------------|
| **ED-1** | `EditDistance.ComputeIgnoreCase`: Replace `new int[]` with `stackalloc`/`ArrayPool` | Eliminates 2 × `int[]` allocations per edit-distance call | `ParsingBenchmark` + `LintBenchmark` alloc delta; `dotnet test` |
| **R-4** | `IanaTimeZones.IsKnown`: Add `ReadOnlySpan<byte>` overload | Eliminates 1 string allocation per valid timezone check | `dotnet test --treenode-filter /*/*/ScheduleEventRule*` |

### Priority 2 (Medium Impact)

| ID | Change | Expected Impact | Verification |
|----|--------|----------------|--------------|
| **AR-1** | `GlobMatch` dictionary: Use `[ThreadStatic]` or field-level reuse | Eliminates Dictionary allocation per glob match call | `dotnet test`; manual benchmark if needed |
| **L-3** | Suppression inner dictionaries: Pool or field-level reuse | Reduces per-job dictionary allocations for large workflows | `LintBenchmark` (Large) |

### Priority 3 (Low Impact / Cold Path)

All F-* and remaining R-* items. These are on diagnostic/fix paths only and do not affect steady-state linting performance.

---

## Implementation Workflow

For each item:

1. **Write test (red):** Add or identify an existing test that exercises the path. Confirm pass.
2. **Implement fix (green):** Apply the suggested allocation elimination.
3. **Run tests:** `dotnet test` — confirm no regressions.
4. **Benchmark:**
   - `cd src/Seiton.Benchmark && dotnet run -c Release`
   - Compare `Allocated` column for `LintBenchmark` (Large) and `ParsingBenchmark` to previous baseline.
5. **Document:** Update baseline numbers in `BenchmarkDotNet.Artifacts/results/`.

---

## Benchmark Baseline Reference

Before starting implementation, capture current baselines:

```shell
cd src/Seiton.Benchmark
dotnet run -c Release -- --filter "*LintBenchmark*"
dotnet run -c Release -- --filter "*ParsingBenchmark*"
```

Compare `Allocated` (bytes) before and after each change.

---

## Conclusion

The Seiton codebase is already well-optimized. The parser uses zero-alloc patterns throughout, and most linting rules only allocate strings on error/diagnostic paths (which is acceptable by design). The actionable items are:

1. **ED-1** (`EditDistance`) — straightforward stackalloc/ArrayPool conversion with measurable per-suggestion improvement.
2. **R-4** (`IanaTimeZones.IsKnown`) — add a UTF-8 overload to avoid string allocation on the happy path.
3. **AR-1** (`GlobMatch` dictionary reuse) — moderate improvement for workflows with file exclusions.
4. **L-3** (suppression dictionary pooling) — helps large multi-job workflows.

All other patterns are either already optimized or are on cold paths where allocation is acceptable.
