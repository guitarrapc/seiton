---
name: performance-requirements
description: Guidelines for writing high-performance and memory-efficient code in `src/Seiton.Core/` (Parsing and Linting) and generated code emitted by `src/Seiton.Update/Generators/`. Covers zero allocations, per-run caching, zero-copy string design, bounded stackalloc in generated code, error-path CPU budget, and verification practices.
---

# Performance Requirements

All parser and linting code must be implemented with **maximum attention to performance and memory efficiency**.

## Core Requirements

### 1. Zero Allocations

- Never allocate arrays or collections during parser execution/ast processing.
- Use `Span<T>` for all array operations
- Use `stackalloc` for small temporary buffers (≤ 128 elements)
- Use `ArrayPool<T>.Shared` for large temporary buffers (> 128 elements)
- **NEVER** use `new T[]` or `new List<T>` for internal buffers

Parser-specific additions:

- For parser key checks, use `ReadOnlySpan<byte>` + `SequenceEqual("..."u8)`.
- In normal parse success paths, do not materialize strings.
- `GetScalarString()` is allowed only for diagnostics or exceptional fallback handling.
- Keep dynamic text as `Utf8Slice` and decode only when reporting diagnostics.
- Repeated lookups must be avoided by carrying resolved metadata through parse steps.
- AST nodes are struct rows in `AstArena` `NodeTable`s addressed by typed IDs/ranges. Do not add class nodes, object pools, or per-node `Reset()` methods — arena reset is a table-counter clear.
- When nested parsing can insert rows into the same table (child lists such as steps, raw-yaml items/props), collect IDs in a scratch `PooledBuffer<T>` and bulk-append to the shared list store so ranges stay valid. Key-embedded map rows (`NodeRange` maps) must be appended contiguously — their nested parsing may only touch *other* tables; if a new nesting violates that, switch that map to scratch-buffer construction.

**Example:**

```csharp
// ✅ For small buffers - use stackalloc (no heap allocation)
if (span.Length <= 128)
{
    Span<T> tempBuffer = stackalloc T[span.Length];
    // ... parser logic using tempBuffer ...
}
// ✅ For large buffers - use ArrayPool (reusable, no GC pressure)
else
{
    var rentedArray = ArrayPool<T>.Shared.Rent(span.Length);
    try
    {
        var tempBuffer = rentedArray.AsSpan(0, span.Length);
        // ... parser logic using tempBuffer ...
    }
    finally
    {
        ArrayPool<T>.Shared.Return(rentedArray);
    }
}
```

### 2. Aggressive Inlining

- Mark hot-path methods with `[MethodImpl(MethodImplOptions.AggressiveInlining)]`
- Especially for methods called frequently in loops (comparisons, swaps, etc.)

### 3. Loop Optimization

- Cache frequently accessed values outside loops
- Use `for` loops with indices instead of `foreach`
- Minimize redundant comparisons
- Avoid repeated property access or method calls

### 4. Parser Hot Path Prohibitions

- No LINQ in parsing loops.
- No regex in parser implementation.
- No dictionary/collection growth in per-node parse paths.
- No repeated decoding of the same scalar when one decode at diagnostic time is enough.

## Linting Pipeline Requirements

Lint rules execute per-workflow, per-job, or per-step and are called hundreds of times for large workflows (e.g. 20 jobs × 12 steps = 240 steps). Apply the following patterns when writing or modifying lint rules.

- Lint rules read the AST through the readonly-struct Ref surface (`StepRef` / `StringRef` / map refs): compare with `StringRef.Value` / `.ValueEquals("..."u8)` (UTF-8 spans), look up maps with `TryGetValue(keySpan, out ...)`, and call `StringRef.Decode()` only when actually emitting a diagnostic.
- Lint rules may use `string` for convenience, but prefer Span-based APIs for key checks and value comparisons in hot paths.
- If a rule is expensive, make it opt-in (only runs when explicitly enabled) rather than always-on.

### 5. Per-Run Shared Caching

The `LintConfig` object is shared across all rules for a single `LintEngine.Check()` call. Use it as the caching layer for expensive computed results that are invariant within one lint run.

- **Line-starts cache**: Source line offsets are invariant per file. Compute once via `LintConfig.GetLineStarts()` and pass the `int[]` to every call site. Never call `BuildLineStarts()` per-expression.
- **Expression parse cache**: Use `LintConfig.ParseExpression()` which caches by content hash (XXH64 64-bit). Same expression text at different offsets returns the cached `ExpressionParseResult`. Never call `ExpressionParser.Parse()` directly from a rule.
- **General principle**: If a computation depends only on the source YAML `byte[]` (which is immutable during a lint run), cache the result in `LintConfig` on first access.

### 6. Utf8String Zero-Copy Construction

`Utf8String` stores `ReadOnlyMemory<byte>` internally. `Equals` and `GetHashCode` operate on **byte content** (XXH64), not reference identity — two `Utf8String` values with the same bytes are equal regardless of whether they share the same backing array. Two construction modes:

- **Copying** (`new Utf8String(ReadOnlySpan<byte>)`): Allocates a `byte[]`. Use for static literals and generated code.
- **Zero-copy** (`new Utf8String(ReadOnlyMemory<byte>)` or `slice.ToUtf8StringZeroCopy(byte[])`): Wraps a slice of the existing source array. Use in linting hot paths where the source `byte[]` outlives the `Utf8String`.

```csharp
// ❌ Allocates a new byte[] per call
props[new Utf8String(pair.Key.AsSpan(utf8Yaml))] = type;

// ✅ Zero-copy: wraps source array memory
props[pair.Key.ToUtf8StringZeroCopy(utf8Yaml)] = type;
```

### 7. Static Promotion of Repeated Literals

UTF-8 byte arrays created from literals (`"steps"u8.ToArray()`) should be `static readonly` fields when used across multiple calls (e.g. per-job). Same applies to `Utf8String` keys used in every needs/outputs entry.

```csharp
// ❌ Allocates per-job
var key = "steps"u8.ToArray();

// ✅ Allocates once at class load
static readonly byte[] StepsKeyUtf8 = "steps"u8.ToArray();
```

### 8. Per-Entity Array Reuse

When override arrays (e.g. scope overrides per job) have a fixed maximum size, allocate the array once as a field and overwrite elements per-entity instead of creating `new[]` per-job.

```csharp
// ❌ Allocates per-job
var overrides = new (byte[], ExprType)[3];

// ✅ Allocated once, elements overwritten
private readonly (byte[], ExprType)[] _overrides = new (byte[], ExprType)[3];
// In VisitJobPre: _overrides[0] = ...; _overrides[1] = ...; _overrides[2] = ...;
```

### 9. Diagnostic Message Deduplication

When the same diagnostic message is emitted many times with the same dynamic content (e.g. the same action ref repeated across 120 steps), cache the last-generated message string and skip `Decode()` + string interpolation on cache hit.

```csharp
// Cache pattern: compare source bytes, reuse string
private Utf8Slice _lastSlice;
private string? _lastMessage;

private string GetMessage(Utf8Slice usesSlice, byte[] source)
{
    if (_lastMessage is not null
        && usesSlice.Length == _lastSlice.Length
        && source.AsSpan(_lastSlice.Offset, _lastSlice.Length)
            .SequenceEqual(source.AsSpan(usesSlice.Offset, usesSlice.Length)))
    {
        return _lastMessage;
    }
    _lastSlice = usesSlice;
    _lastMessage = BuildMessage(usesSlice, source);
    return _lastMessage;
}
```

### 10. HereDoc / Temporary State Zero-Alloc

For small temporary state arrays (e.g. heredoc tracking during script analysis), use `stackalloc` with a counter instead of `new List<T>()`. Store offsets into the source array instead of copying byte slices.

## Verification

- Test with BenchmarkDotNet to measure performance
- Verify zero allocations in Release builds

Parser verification:

1. Run parser tests after each parser refactor.
2. Check for new `GetScalarString()` calls in `src/Seiton.Core/Parsing/**`.
3. For meaningful parser changes, run allocation benchmarks (or a focused micro benchmark) and compare to previous baseline.
4. Reject changes that regress allocation behavior without explicit justification in PR description.

Linting verification:

1. Run `dotnet test` after any lint rule change.
2. For rules that process per-expression or per-step, run `LintBenchmark` (Large) and compare `Allocated` to previous baseline.
3. Check that new rules use `Config.ParseExpression()` (not direct `ExpressionParser.Parse()`).
4. Check that new rules use `Config.GetLineStarts()` (not direct `BuildLineStarts()`).
5. Verify no `new Utf8String(span)` in per-job/per-step paths when `ToUtf8StringZeroCopy` is possible.

## Quick Decision Guide

| Situation | Pattern |
|---|---|
| Need line offsets from source YAML | `Config.GetLineStarts()` (cached) |
| Need to parse an expression in a rule | `Config.ParseExpression(span)` (content-hash cached) |
| Building `Dictionary<Utf8String, ...>` from source keys | `slice.ToUtf8StringZeroCopy(utf8Yaml)` |
| UTF-8 literal used per-job/per-step | `static readonly byte[]` field |
| Fixed-size override array per-job | Field array with element overwrite |
| Same diagnostic message repeated N times | Last-message cache with byte equality check |
| Small temp state (≤ 4-8 entries) in analysis | `stackalloc` + counter |
| Large temp buffer | `ArrayPool<T>.Shared.Rent()` with try/finally Return |
| `stackalloc` with input-derived length | Guard with max-known constant, early-return if exceeded |
| Suggestion / "did you mean" on invalid input | Skip for inputs longer than max valid length |
| Need to iterate `FrozenSet<string>` candidates | Accept `IReadOnlyCollection<string>` — struct enumerator, zero-alloc |

## Generated Lookup Code (Seiton.Update)

When generating static lookup classes (e.g. `IanaTimeZones.g.cs`), follow these patterns:

### 11. Bounded stackalloc in Generated Code

If generated code uses `stackalloc` with a size derived from input (e.g. UTF-8 span length), the generator **must** emit a compile-time max-length constant and guard before the stackalloc. User-controlled input can be arbitrarily long — unbounded stackalloc causes stack overflow (process-terminating).

```csharp
// ❌ Unbounded stackalloc — stack overflow on long input
Span<char> chars = stackalloc char[utf8Id.Length];

// ✅ Generator computes max from data, emits constant + early-return guard
private const int MaxIdByteLength = 32; // computed by generator

internal static bool IsKnown(ReadOnlySpan<byte> utf8Id)
{
    if (utf8Id.Length > MaxIdByteLength)
        return false;

    Span<char> chars = stackalloc char[MaxIdByteLength];
    var charCount = Encoding.UTF8.GetChars(utf8Id, chars);
    // AlternateLookup = KnownIds.GetAlternateLookup<ReadOnlySpan<char>>()
    // Allows span-based lookup into FrozenSet without allocating a string
    return AlternateLookup.Contains(chars[..charCount]);
}
```

The generator must compute `maxByteLength` using `Encoding.UTF8.GetByteCount()` (not `string.Length`) and handle the empty-data case (return 0).

`AlternateLookup` refers to `FrozenSet<string>.GetAlternateLookup<ReadOnlySpan<char>>()` — it enables span-based `Contains` without materializing a `string`.

### 12. FrozenSet as Primary, No Redundant Arrays

`FrozenSet<string>` is the correct primary store for immutable lookup sets. Do **not** keep a parallel `string[]` field:

- `FrozenSet` already provides O(1) `Contains` and struct-enumerator `foreach` (zero-allocation iteration when called directly).
- If suggestion logic needs to iterate candidates, accept `IReadOnlyCollection<string>` (which `FrozenSet` implements) rather than materializing an array. Note: interface dispatch via `IReadOnlyCollection` boxes the enumerator (one small allocation), but this is acceptable since suggestion runs only on error paths.
- Suggestion tie-break order (when multiple candidates have equal Levenshtein distance) is non-deterministic from `FrozenSet` enumeration. This is acceptable — exact tie-break order is not a correctness concern.

```csharp
// ❌ Redundant static array alongside FrozenSet
private static readonly string[] IdsArray = [.. KnownIds]; // extra alloc at type-init

// ✅ FrozenSet is primary; iterate directly via IReadOnlyCollection
internal static string? FindSuggestion(string input)
    => SuggestionHelper.FindClosest(input, KnownIds); // FrozenSet implements IReadOnlyCollection
```

### 13. Error-Path CPU Budget

Error paths (diagnostics, "did you mean" suggestions) are allowed to allocate, but must still be bounded for pathological inputs:

- **Skip expensive computation for long inputs**: If input length exceeds the max valid value length (`MaxIdByteLength`), there is no plausible match — skip Levenshtein search entirely.
- **Decode only what you display**: If displaying a truncated prefix, decode only that prefix from UTF-8 — don't decode the full multi-KB string. Use a separate display-cap constant (`MaxDisplayLength`, e.g. 40 bytes) distinct from the validation constant (`MaxIdByteLength`, e.g. 32 bytes).
- **Short-circuit pattern**: Check `span.Length` (bytes) before decode, decode before suggestion.

Two constants serve different purposes:
- `MaxIdByteLength`: Maximum UTF-8 byte length of any known valid value (computed by generator from data). Used to guard `stackalloc` and `IsKnown`.
- `MaxDisplayLength`: Maximum bytes to decode for diagnostic display (e.g. 40 bytes). Defined in the rule class. Applied to `span[..MaxDisplayLength]` then decoded to string with "..." appended.

```csharp
// ✅ Bounded error path — skip expensive work for pathological inputs
if (span.Length > MaxDisplayLength)
{
    // Too long to be valid — decode prefix only, skip suggestion
    display = string.Concat(Encoding.UTF8.GetString(span[..MaxDisplayLength]), "...");
}
else
{
    var decoded = Decode(slice);
    display = decoded;
    suggestion = FindSuggestion(decoded); // Levenshtein over ~600 candidates
}
```
