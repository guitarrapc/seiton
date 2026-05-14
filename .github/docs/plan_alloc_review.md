# Deep Allocation Analysis — Seiton.Core

## Executive Summary

Full lint of a Large workflow (20 jobs × 12 steps, 45 KB YAML) allocates **~500 KB** in steady-state (arena reused, expression cache warm). The BenchmarkDotNet measurement shows **~710 KB** because the benchmark never disposes the Arena, preventing ThreadStatic reuse.

**Top allocation sources (steady-state):**

| Source | KB | % | Actionability |
|---|---|---|---|
| Parse: object pool growth (Step/ExecRun/ExecAction) | ~28 | 6% | Increase defaults |
| Parse: Step[] per-job arrays + SliceMap entries | ~30 | 6% | Structural |
| Parse: VYaml + PooledBuffer + structural data | ~189 | 38% | Low (well-optimized) |
| Lint: ExprUndefinedVarRule string allocations | ~122 | 24% | **HIGH** |
| Lint: Diagnostic message strings (280 diags) | ~131 | 26% | **HIGH** |
| **Total** | **~500** | 100% | |

**Benchmark vs real-world gap:**

| Scenario | KB | Reason |
|---|---|---|
| Benchmark (no Arena.Dispose) | ~710 | Fresh Arena backing arrays every iteration |
| Steady-state (Arena reused) | ~500 | ThreadStatic pool provides arena |
| Delta | ~210 | ArrayPool rents + object pool re-creation |

---

## 1. Measurement Methodology

All measurements use `GC.GetTotalAllocatedBytes(precise: true)` with explicit warmup (5 iterations + GC.Collect + GC.WaitForPendingFinalizers) to ensure steady-state. The probe scripts are in `sandbox/DotnetFiles/`:
- `LintAllocDeepDive.cs` — component-level breakdown
- `ArenaReuseProbe.cs` — arena disposal impact analysis
- `ScalingAllocProbe.cs` — per-job/per-step scaling analysis

---

## 2. Benchmark Fixture Profile

```
File: 20 jobs × 12 steps (6 run + 6 action), 44,967 bytes, 1,443 lines
Expressions: 482 total occurrences, 6 unique
  "github.ref_name", "github.ref", "matrix.os", "github.sha"
  "startsWith(github.ref, 'refs/heads/') && success()"
  "!cancelled() && github.event_name == 'push'"
Diagnostics: 280 (120 checkout-persist-credentials, 120 unpinned-uses,
             20 job-permissions-required, 20 runner-no-latest)
```

---

## 3. Scaling Characteristics

### ExprUndefinedVarRule (isolated)

| Jobs | Steps | Total (KB) | Parse (KB) | Rule (KB) | Per-Job (B) |
|------|-------|-----------|-----------|----------|------------|
| 0 | 0 | 6.8 | 4.3 | 2.5 | — |
| 1 | 12 | 25.1 | 16.6 | 8.5 | — |
| 5 | 60 | 89.6 | 57.5 | 32.1 | 6,024 |
| 10 | 120 | 176.8 | 114.5 | 62.3 | 6,177 |
| 20 | 240 | 363.7 | 241.6 | 122.1 | 6,120 |

**Per-job cost: 6,120 bytes** — linear scaling, consistent across sizes.

### Full Lint (all rules)

| Jobs | Steps | Total (KB) | Parse (KB) | Lint (KB) | Diags | Per-Job (B) |
|------|-------|-----------|-----------|----------|-------|------------|
| 0 | 0 | 10.5 | 3.0 | 7.5 | 1 | — |
| 1 | 12 | 33.8 | 17.3 | 16.5 | 14 | — |
| 5 | 60 | 122.5 | 57.5 | 65.0 | 70 | 12,416 |
| 10 | 120 | 240.0 | 114.5 | 125.5 | 140 | 12,396 |
| 20 | 240 | 488.3 | 241.6 | 246.7 | 280 | 12,411 |

**Per-job cost: 12,411 bytes** = ExprUndefinedVarRule (6.1 KB) + diagnostic strings (6.3 KB).

---

## 4. Parser Allocation Analysis (247 KB steady-state)

### 4.1 VYaml Reader: ~2 KB

VYaml itself is extremely efficient. `YamlParser.FromBytes(Memory<byte>)` allocates minimal internal state (~336 bytes full read). The parser creates two readers (hint + full parse), totaling ~2 KB.

### 4.2 AstArena Object Pools: ~28 KB

Default pool capacities are designed for small-medium workflows:

| Pool | Default | Large needs | Growth | New objects |
|------|---------|-------------|--------|-------------|
| Job | 24 | 20 | 0 | 0 |
| Step | 64 | 240 | 64→128→256 | 176 × 80B = 14 KB |
| ExecRun | 64 | 120 | 64→128 | 56 × 64B = 3.5 KB |
| ExecAction | 64 | 120 | 64→128 | 56 × 88B = 4.9 KB |
| **Total** | | | | **~28 KB** |

On Dispose, pools shrink to defaults, discarding the extra objects. Next parse re-creates them.

### 4.3 AstArena Scalar Arrays: ~0 (ArrayPool-cached)

StringNodeData[], BoolNodeData[], IntNodeData[], FloatNodeData[] are ArrayPool-backed. After one warmup+dispose cycle, the pool retains all needed sizes. Subsequent parses get free Rents. Zero allocation in steady-state.

### 4.4 Step[] Per-Job Arrays: ~2 KB

`ParseSteps()` calls `steps.ToArray()` creating a fresh `Step[N]` per job. 20 jobs × 12 steps × (16 + 12×8) = ~2,240 bytes.

### 4.5 SliceMap Entry[] Arrays: ~30+ KB

Each composite (Env, With-inputs, Matrix, Outputs, etc.) uses a SliceMap backed by ArrayPool. For 20 jobs with multiple sub-maps, the cumulative Entry[] rental from pool totals ~30 KB (mostly pool hits after warmup, but some growth-related new allocations).

### 4.6 Remaining Parse (~189 KB)

Structural allocations during parsing:
- Event objects (WebhookEvent, WorkflowDispatchEvent, DispatchInput, filters): ~0.5 KB
- Matrix structures (Matrix, MatrixRow, RawYamlString per value): ~4 KB
- Runner, Strategy, Concurrency, Permissions, Defaults: ~5 KB
- Env/EnvVar structures: ~10 KB
- Expression validation during parse (`ParseAndValidateInline` × 482): PooledBuffer rentals, mostly ArrayPool-cached
- Remaining unaccounted: likely PooledBuffer growth, Grow() intermediate arrays for SliceMaps before pool has correct sizes

---

## 5. Linter Allocation Analysis (253 KB lint-only)

### 5.1 ExprUndefinedVarRule: ~122 KB

**Confirmed allocators (33 KB, 27%):**

Per-step string allocations from `Decode()` + interpolated sink-name strings:
- `Decode(Arena.GetStringSlice(envVar.Name))` — `Encoding.UTF8.GetString()` per env var: ~7 KB
- `$"{sinkName}-key"` per env var: ~9 KB
- `$"{sinkName}.{keyName}"` per env var: ~9 KB
- `Decode(pair.Key)` per with-input: ~7 KB
- `$"step.with.{inputName}"` per with-input: ~10 KB

**Structural allocators (5 KB):**
- `BuildGithubOverride`: new Dictionary with ~40 entries (github context copy): ~3 KB
- `BuildWorkflowDispatchInputsType`: new Dictionary with 1 entry: ~0.2 KB
- `NormalizeRules` / `ParseInlineSuppression` framework cost: ~2 KB

**Unaccounted (~84 KB, 69%):**

The remaining per-job overhead (~4.5 KB per job × 20 = ~90 KB) could not be traced to specific allocations through code reading. Hypotheses:
1. Generic method JIT metadata for `CheckNode<TTarget>` instantiations (Job, Step, Workflow, Event)
2. Dictionary internal bucket arrays when reusable dicts are first populated each run
3. `Utf8String.GetHashCode()` comparison path internals
4. ValidateDynamicPropertyAccessInline recursive type inference creating temporary ExprType instances

### 5.2 Diagnostic Strings: ~131 KB

280 diagnostics × average ~468 bytes each:
- `Message` string: dominant cost (30-80 chars × 2 bytes + 40 byte header)
- `RuleId` string: shared across same-rule diagnostics (small, ~20 bytes each)
- `FilePath` string: null in benchmark (since filePath is relative, not stored)
- `Fix`: null when FixEnabled=false

### 5.3 Expression Cache: ~0 KB (after warmup)

The `LintConfig._expressionCache` stores 6 entries (one per unique expression). Each entry is `(byte[] copy, ExpressionParseResult)`. After first population (~2 KB total), subsequent runs are pure cache hits with zero allocation.

### 5.4 LintEngine Framework: ~0 KB

All internal collections (`_diagnostics`, `_seen`, `_ruleDiagnostics`, `_suppressedByRule`, etc.) are pre-allocated fields. `Clear()` resets without deallocating. The visitor traversal is zero-alloc.

---

## 6. Design-Level Improvement Proposals

### P-1: Eliminate Sink-Name String Interpolation (saves ~33 KB, difficulty: Medium)

**Problem:** `ExprUndefinedVarRule` allocates ~600 strings per lint run via `Decode()` + `$"step.with.{name}"` interpolation. These strings are ONLY used in error messages (which are rare).

**Design:**
- Replace `string sinkName` parameter with a struct `SinkLocation` containing:
  ```csharp
  readonly struct SinkLocation(SinkKind kind, Utf8Slice name)
  ```
- Only materialize the string lazily when an error IS reported (inside `report()`).
- `Decode()` calls for env var and with-input keys become deferred.

**Impact:** Eliminates ~33 KB of confirmed allocations. May also eliminate part of the unaccounted 84 KB if string-related pressure is the cause.

**Complexity:** Moderate refactor of `CheckNode`/`CheckEnv` method signatures. Backward-compatible since it's internal API.

### P-2: Diagnostic Message Templates (saves ~100+ KB, difficulty: Medium-High)

**Problem:** 280 diagnostics each allocate a unique `Message` string. Many messages follow templates:
- "action 'actions/checkout@v4' should pin to a full-length commit SHA" × 120
- "step uses 'actions/checkout@v4' which should set persist-credentials: false" × 120
- "job 'job0' should have explicit permissions" × 20

**Design A — Interned message fragments:**
```csharp
// Instead of: $"action '{uses}' should pin to a full-length commit SHA"
// Use: Diagnostic with template + parameters, format on ToString()/display
readonly record struct Diagnostic(
    DiagnosticSeverity Severity,
    DiagnosticMessage Message,  // ← new type
    TextRange Location, ...);

readonly struct DiagnosticMessage {
    // Pre-allocated template + parameters; formats only when .ToString() is called
    private readonly string _template;
    private readonly object[]? _args; // or ReadOnlyMemory<byte> for utf8 keys
}
```

**Design B — String deduplication for repeated messages:**
```csharp
// For rules that produce identical messages (like runner-no-latest),
// cache the message string and reuse it across diagnostics
private string? _cachedRunnerMessage;
```

**Impact:** Could save 100+ KB for workflows with many repeated diagnostics. Design A is more general; Design B is simpler for specific rules.

**Complexity:** Design A requires changing the `Diagnostic` type (breaking public API). Design B is per-rule and non-breaking.

### P-3: Increase Object Pool Defaults (saves ~22 KB per Large parse, difficulty: Low)

**Problem:** Default pool capacities (Step=64, ExecRun/ExecAction=64) are too small for large workflows. After Dispose+Rent, 176 Step objects + 112 ExecRun/ExecAction objects are re-created.

**Design:**
```csharp
private const int DefaultStepCapacity = 128;      // was 64
private const int DefaultExecRunCapacity = 128;    // was 64
private const int DefaultExecActionCapacity = 128; // was 64
```

**Impact:** Reduces re-creation to 112 Step objects (saves ~9 KB). Trade-off: ThreadStatic cache retains larger arrays permanently (~10 KB more resident memory).

**Complexity:** One-line changes. Risk-free.

### P-4: Fix Benchmark Arena Disposal (clarification, not perf improvement)

**Problem:** `CoreLintBenchmark.CheckWorkflow()` never calls `Arena.Dispose()`, so ThreadStatic cache is always empty and every iteration pays fresh Arena creation cost (~94-210 KB depending on pool state).

**Design:**
```csharp
[Benchmark]
public int CheckWorkflow()
{
    var result = _engine.Check(_yamlBytes, _filePath, _lintConfig);
    var count = result.Diagnostics.Length;
    result.ParseResult.Arena?.Dispose(); // ← add this
    return count;
}
```

**Impact:** Benchmark will report ~500 KB instead of ~710 KB, which reflects real-world steady-state more accurately. Does NOT save real allocations — just makes measurement honest.

**Risk:** Could be argued that "first-call" cost is also valid to measure. Consider adding a separate benchmark for cold-start vs warm-start.

### P-5: BuildGithubOverride Caching (saves ~3 KB, difficulty: Low)

**Problem:** `BuildGithubOverride` creates a new `Dictionary<Utf8String, ExprType>(~40 entries)` per lint run by copying the builtin github context type and replacing the `event` property.

**Design:** Cache the github override in `ExprUndefinedVarRule` (the override depends only on the workflow's event declarations, which don't change between runs of the same file):
```csharp
private (byte[] NameUtf8, ExprType Type)? _cachedGithubOverride;
private int _cachedEventCount; // invalidation key
```

**Impact:** ~3 KB per lint run. Small but free.

### P-6: Per-Rule Diagnostic Deduplication (saves variable, difficulty: Low)

**Problem:** Rules like `UnpinnedUsesRule` and `CheckoutPersistCredentialsRule` generate near-identical messages for repeated `uses: actions/checkout@v4`:
- 120 messages like "action 'actions/checkout@v4' should pin to..." (identical text!)

**Design:** In rules that produce repeated diagnostics for the same action reference, cache the message string:
```csharp
private string? _lastMessage;
private ReadOnlySpan<byte> _lastUsesValue; // invalidation key
```

**Impact:** For 120 identical `actions/checkout@v4` diagnostics, saves ~119 × ~80 bytes = ~9.5 KB per message template. Total across both rules: ~19 KB.

### P-7: Deferred Expression Parsing in Lint (saves ~2 KB cache misses, difficulty: Low)

Already well-optimized: the `LintConfig._expressionCache` with XxHash64 deduplication means only 6 unique expressions are ever parsed. The `expression.ToArray()` per cache miss is tiny (~2 KB total). No further optimization needed.

---

## 7. Priority Matrix

| ID | Savings (KB) | Difficulty | Risk | Recommendation |
|----|-------------|-----------|------|----------------|
| P-1 | 33-122 | Medium | Low | **Do** — largest confirmed saving |
| P-2B | 19-100 | Low-Medium | Low | **Do** — per-rule string caching |
| P-3 | 22 | Low | None | **Do** — trivial change |
| P-4 | 0 (clarity) | Low | None | **Do** — honest benchmarks |
| P-5 | 3 | Low | None | **Do** — trivial caching |
| P-6 | 19 | Low | None | **Do** — per-rule dedup |
| P-2A | 100+ | High | Medium | **Defer** — API-breaking |

### Recommended Implementation Order

1. **P-4** (benchmark fix) — immediate, establishes true baseline
2. **P-3** (pool defaults) — trivial, predictable improvement
3. **P-5** (github override cache) — trivial
4. **P-6** (diagnostic dedup) — per-rule, low risk
5. **P-1** (sink-name elimination) — biggest impact, moderate effort
6. **P-2B** (message caching) — after P-6, builds on same pattern

### Expected Result After All Non-Breaking Changes

| Before | After | Savings |
|--------|-------|---------|
| 500 KB (steady-state) | ~380-420 KB | 80-120 KB (16-24%) |
| 710 KB (benchmark) | ~380-420 KB | 290-330 KB (41-46%) |

---

## 8. Architecture Notes

### What's Already Well-Optimized

1. **AstArena ThreadStatic pooling** — zero-cost arena reuse when callers dispose
2. **ArrayPool for all backing arrays** — StringNodeData/Bool/Int/Float from pool
3. **Utf8Slice zero-copy** — no string materialization for keys/values
4. **Expression cache** — XxHash64 deduplication, 6 cache entries serve 482 lookups
5. **LintEngine field reuse** — all collections pre-allocated, Clear() between runs
6. **SliceMap replaces Dictionary** — flat array with pool-backed Entry[]
7. **PooledBuffer in parser** — ArrayPool-backed growable buffers
8. **Static lambdas** — delegate caching prevents per-call allocation
9. **VYaml efficiency** — <2 KB for 45 KB YAML parsing

### Fundamental Constraints

1. **AST nodes are reference types** — Job, Step, ExecRun, ExecAction are classes (for Reset/reuse pattern). Moving to structs would require arena-based flat storage (major redesign).
2. **Diagnostic.Message is string** — public API contract. Changing to lazy template requires API break.
3. **IReadOnlyList\<Step\> per job** — structural requirement for visitor pattern. Could use Span-returning API but would require ref struct visitors (major redesign).
4. **Expression cache stores byte[] copies** — required for collision detection. Only 6 entries, negligible.
