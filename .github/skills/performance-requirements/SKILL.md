---
name: performance-requirements
description: Guidelines for writing high-performance and memory-efficient parser algorithms in the `src/Seiton.Core/Parsing/` folder. This includes instructions on zero allocations, aggressive inlining, loop optimization, and verification practices.
---

# Performance Requirements

All parser algorithms must be implemented with **maximum attention to performance and memory efficiency**.

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

## Verification

- Test with BenchmarkDotNet to measure performance
- Verify zero allocations in Release builds

Additional parser verification:

1. Run parser tests after each parser refactor.
2. Check for new `GetScalarString()` calls in `src/Seiton.Core/Parsing/**`.
3. For meaningful parser changes, run allocation benchmarks (or a focused micro benchmark) and compare to previous baseline.
4. Reject changes that regress allocation behavior without explicit justification in PR description.
