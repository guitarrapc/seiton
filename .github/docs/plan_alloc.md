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

**Status: ✅ IMPLEMENTED**

Replaced `new int[]` × 2 with inline `stackalloc` (≤128) / `ArrayPool` (>128). The algorithm loop is inlined in both branches to avoid method-call overhead with `Span<int>` parameters.

**Benchmark Results (EditDistanceBenchmark, ShortRun):**

| Method | Before (Mean) | Before (Alloc) | After (Mean) | After (Alloc) | Δ Mean | Δ Alloc |
|--------|--------------|----------------|-------------|---------------|--------|---------|
| ComputeAll (6×6 loop) | 9,882 ns | 5,568 B | 7,867 ns | 0 B | **-20%** | **-100%** |
| SingleShort ("tokne"↔"token") | 35 ns | 0 B | 50 ns | 0 B | +43%* | — |
| SingleLong ("cache-dependency-pathx"↔"cache-dependency-path") | 505 ns | 0 B | 722 ns | 0 B | +43%* | — |

\*SingleShort/SingleLong show 0 alloc in baseline because .NET 10 JIT applies Object Stack Allocation (OSA) for isolated calls where arrays don't escape. This optimization is lost with `stackalloc` (larger stack frame). In real usage, `EditDistance` is **always called in a loop** (comparing unknown input against all known inputs), where the baseline OSA doesn't apply and allocations accumulate. The **ComputeAll** benchmark is the representative scenario.

**Tests:** 320/320 `RuleInterfaceTests` pass. No regressions.

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
| R-4 | `ScheduleEventRule` | Line 90 | `Generated.IanaTimeZones.IsKnown(Encoding.UTF8.GetString(span))` | Low-Medium | Called per-schedule entry. Added a `IsKnown(ReadOnlySpan<byte>)` overload that uses `FrozenSet<string>.GetAlternateLookup<ReadOnlySpan<char>>()` + stackalloc char decode, avoiding string allocation on the happy path (timezone is valid). |
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

## Design Trade-off Evaluation

Each candidate item is evaluated from three orthogonal perspectives before deciding whether to implement:

1. **Security** — Could the change introduce data leakage across calls, expose sensitive path/content information, or create attack surface (e.g. shared mutable state across trust boundaries)?
2. **Instance Lightness & State Complexity** — Does the change add long-lived fields, require careful lifecycle management (clear/dispose), or make concurrency reasoning harder?
3. **Code Clarity & Simplicity** — Does the change increase cognitive load for maintainers, require non-obvious invariants, or obscure the algorithm's intent?

### Evaluation Matrix

| ID | Security | Instance Lightness | Code Clarity | Verdict |
|----|----------|-------------------|--------------|---------|
| **ED-1** ✅ | ◎ No state shared across calls; stackalloc is stack-local | ◎ No new fields; allocation is call-scoped | ○ Two inlined branches add code volume but intent is clear | **Implement** — pure local optimization, zero cross-call state |
| **R-4** ✅ | ◎ `AlternateLookup` is a static readonly derived from static data; no mutable state | ◎ One static field added to generated code | ◎ Single-line overload; generated code hides complexity | **Implement** — zero-alloc with no architectural impact |
| **L-1** | ◎ Snapshot semantics already required for public API | △ Already has `_suppressionRecords` field (reused via Clear); switching to field array adds capacity tracking | ○ Current `new[]` is maximally clear | **Won't Fix** — snapshot copy is intentional API contract; replacing `new[]` with a pooled buffer would only avoid a small Gen0 array on a per-file warm path |
| **L-2** | ◎ Snapshot semantics | △ Same as L-1 | ○ Current code is clear | **Won't Fix** — same reasoning as L-1 |
| **L-3** | ○ Inner dictionaries hold rule-id strings (non-sensitive); risk of stale entries if Clear is forgotten | △ `_nextLineRuleSuppressions` / `_jobRuleSuppressions` already exist as outer-dict fields; pooling inner dicts requires a `Queue<Dictionary>` or object pool, adding lifecycle complexity | ✗ Pool-and-return pattern is subtle; maintainers must understand borrow/return invariants | **Won't Fix** — inner dicts are created lazily only when suppression directives exist (rare); pooling adds complexity disproportionate to savings |
| **AR-1** | ○ Cache holds `(int,int)→bool` (no sensitive data); but `[ThreadStatic]` + Clear-at-entry is fragile | △ `[ThreadStatic]` means dictionary persists for thread lifetime in thread pool; field-level would require breaking the static API contract of `ActionRefHelpers` | ✗ Current per-call `new` is maximally simple and stateless; any alternative adds implicit state coupling | **Won't Fix** — `GlobMatch` is called per-file × exclusion-count (typically 0–5); small Gen0 dictionary; simplicity > micro-optimization |
| **AR-2** | ◎ Strings are returned as `out string` for API boundary | ◎ No state change | ◎ Current code is clear | **Won't Fix** — string materialization is required at API boundary; `Utf8Slice` would leak internal representation and break `out string` contract |
| **R-5** | ○ Dictionary holds job-id Utf8String keys; no secret data | △ Making it a field requires `NeedsGraphRule` to manage lifecycle (clear in `VisitWorkflowPre`, risk of stale data across workflows if engine is reused) | △ Per-workflow allocation is simple and scoped; field adds lifecycle invariant | **Won't Fix** — per-workflow allocation (1 dict per lint call); converting to field saves one small alloc but adds state lifecycle complexity |
| **R-6** | ◎ Only reached on cycle (error path) | ◎ No change needed | ◎ Already acceptable | **Won't Fix** — cold path only |
| **R-7 to R-14** | ◎ All on diagnostic/error paths | ◎ No state impact | ◎ Current code is clear | **Won't Fix** — all are cold/diagnostic paths where allocation is acceptable by design |
| **F-1 to F-6** | ◎ Fix engine is one-shot; no shared state | ◎ No change needed | ◎ Already clear | **Won't Fix** — fix application is not performance-critical |

### Decision Summary

Only **ED-1** and **R-4** justified implementation:
- Both are **call-local** optimizations (stackalloc / static alternate lookup) that add zero cross-call state.
- Both affect the **success path** (not just error/diagnostic paths).
- Both maintain or improve code clarity (ED-1's inlined branches are mechanical; R-4 is a single generated overload).

All other items are **Won't Fix** because:
- They are on cold/diagnostic/error paths where Gen0 allocation is acceptable.
- The proposed fixes would introduce state lifecycle complexity (`[ThreadStatic]`, field-level pooling, Clear invariants) disproportionate to the savings.
- The current code is maximally simple and stateless, which is the preferred default.

---

## Summary: Final Status

### Completed (Phase 1)

| ID | Change | Result |
|----|--------|--------|
| **ED-1** ✅ | `EditDistance.ComputeIgnoreCase`: stackalloc/ArrayPool | ComputeAll -20% latency, -100% alloc. 320 tests pass. |
| **R-4** ✅ | `IanaTimeZones.IsKnown`: ReadOnlySpan\<byte\> overload | LookupValidAll -27% latency, -100% alloc. 522 tests pass. |

### Won't Fix (Evaluated and Rejected)

| ID | Reason |
|----|--------|
| **L-1, L-2** | Snapshot semantics require new array for public API contract; warm path, small Gen0. |
| **L-3** | Inner dictionaries only created when suppression directives exist (rare); pooling adds lifecycle complexity. |
| **AR-1** | Per-call `new` is maximally simple; `[ThreadStatic]` or field-level breaks statelesness for minimal gain on warm path. |
| **AR-2** | String materialization required at API boundary (`out string`). |
| **R-5** | Per-workflow dictionary (1 per lint call); field-level adds lifecycle invariant for negligible savings. |
| **R-6 to R-14** | All cold/diagnostic paths. |
| **F-1 to F-6** | Fix application is not performance-critical (one-shot operation). |

---

## Conclusion

The Seiton codebase is well-optimized. The parser uses zero-alloc patterns throughout, and most linting rules only allocate on error/diagnostic paths (acceptable by design).

Two items were implemented:

1. **ED-1** (`EditDistance`) — ✅ stackalloc/ArrayPool: -20% latency, -100% alloc in representative loop scenarios.
2. **R-4** (`IanaTimeZones.IsKnown`) — ✅ `FrozenSet.GetAlternateLookup` + stackalloc: -27% latency, -100% alloc.

All remaining items were evaluated against security, state complexity, and code clarity criteria and determined to be **Won't Fix** — the cost of added state management and code complexity exceeds the benefit of eliminating small Gen0 allocations on warm/cold paths.
