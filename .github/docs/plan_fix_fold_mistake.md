# Plan: Fix Folded Block Scalar Slice Resolution

## Problem Statement

When a YAML `if:` condition uses a folded block scalar (`>`), Seiton produces:
1. **Critical**: `--fix` corrupts the file by inserting `${{ }}` into unrelated keys (`runs-on:`, `cancel-in-progress:`)
2. **Medium**: Diagnostic message contains raw newlines from source, making output unreadable

## Root Cause

`GetScalarSlice()` in `VYamlStreamAdapter` cannot resolve the correct source byte range for folded block scalars.

### Failure Chain

```
VYaml normalizes folded scalar: "\n + indent" → single space
  ↓
GetScalarSlice() tries to find source range:
  ↓
TryResolveNormalizedSlice → TryMeasureSourceLength
  → space in normalized vs \n in source → MISMATCH → returns false
  ↓
TryGetScalarAsSpan → block scalars use internal buffer → returns false
  ↓
IndexOf(utf8) fallback → normalized bytes don't exist verbatim in source → not found
  ↓
mark.Position fallback → wrong offset + wrong length (normalized length ≠ source length)
  = GARBAGE SLICE
```

### Consequence of Garbage Slice

```
Arena.GetStringValue(condition) reads garbage bytes from source
  ↓
CanOfferAutoFix checks garbage bytes → no trailing \n → returns true (should be false)
  ↓
BuildFixEdit uses garbage offset/length → TextEdit spans wrong region
  ↓
--fix applies edit → file corrupted
```

### Reproduction

```
.references\githubactions-lab\.github\workflows\monthly-oss-repo-status.lock.yml
```

Run latest published Seiton with `seiton --fix` against this file.

## Affected Code

| File | Function | Role |
|------|----------|------|
| `src/Seiton.Core/Parsing/VYamlStreamAdapter.cs` | `TryMeasureSourceLength` | Core byte matching — doesn't handle fold semantics |
| `src/Seiton.Core/Parsing/VYamlStreamAdapter.cs` | `GetScalarSlice` | Orchestrates slice resolution, fallback produces garbage |
| `src/Seiton.Core/Linting/Rules/IfExprWrapperRule.cs` | `GetOrBuildDiagnosticStrings` | Uses raw slice bytes in message (shows newlines) |
| `src/Seiton.Core/Linting/Rules/IfExprWrapperRule.cs` | `CanOfferAutoFix` | Gate relies on correct `GetStringValue` (garbage bypass) |

## Implementation Plan

### Phase 1: Fix `TryMeasureSourceLength` for Folded Scalars (Critical)

**Goal**: Make slice resolution succeed for folded block scalars.

**Change**: In `TryMeasureSourceLength`, when `valueByte == ' '` and `source[sourceIndex] == '\n'` (or `'\r'`), treat it as a fold point — skip the newline + indentation in source and continue matching.

```csharp
// Current: mismatch → immediate failure
if (source[sourceIndex] != valueByte)
{
    return false;
}

// Fixed: handle fold point (space in normalized = \n+indent in source)
if (source[sourceIndex] != valueByte)
{
    if (valueByte == (byte)' '
        && (source[sourceIndex] == (byte)'\n' || source[sourceIndex] == (byte)'\r'))
    {
        // Skip CRLF or LF
        if (source[sourceIndex] == (byte)'\r'
            && sourceIndex + 1 < source.Length
            && source[sourceIndex + 1] == (byte)'\n')
            sourceIndex += 2;
        else
            sourceIndex++;
        // Skip indentation (same as line-start logic)
        var skipped = 0;
        while (skipped < lineIndentWidth
            && sourceIndex < source.Length
            && (source[sourceIndex] == (byte)' ' || source[sourceIndex] == (byte)'\t'))
        {
            sourceIndex++;
            skipped++;
        }
        atLineStart = false;
        continue;
    }
    return false;
}
```

**Effect**: Correct slice → `GetStringValue` returns real source bytes (with internal `\n`) → trailing `\n` detected → `CanOfferAutoFix` correctly returns false → no fix offered → no corruption.

### Phase 2: Sanitize Diagnostic Message Newlines (Medium)

**Goal**: Even with correct slice, source bytes for block scalars contain internal newlines. The diagnostic message should be single-line for readability.

**Change**: In `IfExprWrapperRule.GetOrBuildDiagnosticStrings`, after decoding `rawSpan` to `rawText`, collapse internal whitespace runs (newline + spaces/tabs) into a single space.

```csharp
var rawText = Encoding.UTF8.GetString(rawSpan);
// Collapse internal newline+whitespace runs to single space for readable diagnostics
rawText = CollapseInternalWhitespace(rawText);
```

Where `CollapseInternalWhitespace` replaces sequences of `[\r\n\t ]+` containing at least one `\n` with a single space.

**Effect**: Diagnostic messages are always single-line regardless of scalar style.

### Phase 3: Defense-in-Depth for CanOfferAutoFix (Low priority)

**Goal**: Prevent future similar bugs from causing file corruption.

**Change**: In `CanOfferAutoFix`, add a secondary check: if the source bytes at the slice contain any `\n` character (excluding trailing), disable auto-fix. This catches any case where the slice spans multiple source lines.

```csharp
// Additional gate: multi-line source content → block scalar → no fix
var body = raw[..^(raw[^1] == '\n' ? 1 : 0)];
if (body.IndexOf((byte)'\n') >= 0)
    return false;
```

**Effect**: Even if slice resolution has edge-case bugs, fix will never be offered for multi-line source content.

## Test Strategy

1. **Unit test**: Folded block scalar `if: >\n  expr1 ||\n  expr2` — verify correct slice offset/length
2. **Unit test**: Folded scalar produces no auto-fix (like existing literal scalar test)
3. **Unit test**: Diagnostic message from folded scalar contains no newlines
4. **Integration test**: `--fix` on file with folded `if:` does not corrupt adjacent keys
5. **Regression**: Existing literal block scalar tests still pass

## Risk Assessment

| Risk | Mitigation |
|------|------------|
| Fold-point heuristic matches non-fold cases | Only triggers when normalized has `\n` (block scalar path); plain scalars never enter `TryResolveNormalizedSlice` |
| Performance regression from extra branching | Hot path unchanged (non-block scalars exit before this code); block scalars are rare |
| Edge case: folded scalar with blank lines (`\n\n`) | Blank lines in folded scalars produce `\n` in normalized — already handled by existing `valueByte == '\n'` branch |
| Edge case: strip chomping (`>-`) removes trailing `\n` | Phase 3's defense catches this; Phase 1's correct slice still exposes internal `\n` for Phase 3 gate |

---

## Implementation Results

### Changes Made

| File | Change |
|------|--------|
| `src/Seiton.Core/Parsing/VYamlStreamAdapter.cs` | `TryMeasureSourceLength`: fold-point handling (space vs newline+indent) |
| `src/Seiton.Core/Parsing/VYamlStreamAdapter.cs` | `GetScalarSlice`: fallback to `TryResolveNormalizedSlice` before garbage mark.Position |
| `src/Seiton.Core/Linting/Rules/IfExprWrapperRule.cs` | `CanOfferAutoFix`: internal `\n` check (defense-in-depth) |
| `src/Seiton.Core/Linting/Rules/IfExprWrapperRule.cs` | `GetOrBuildDiagnosticStrings`: collapse internal whitespace in message |
| `tests/Seiton.Core.Tests/RuleInterfaceTests.IfExprWrapperRule.cs` | 6 new tests for folded/literal block scalar handling |

### Benchmark (CoreLintBenchmark, ShortRun)

No performance regression — fold-point logic is in the `source[i] != valueByte` failure path only. Non-block scalars never reach it.

| Size | FixEnabled | Mean | Allocated |
|------|------------|------|-----------|
| Small | False | 67.31 μs | 8.7 KB |
| Small | True | 71.74 μs | 10.16 KB |
| Medium | False | 1,397.04 μs | 68.9 KB |
| Medium | True | 2,018.71 μs | 82.26 KB |
| Large | False | 22,364.74 μs | 327.41 KB |
| Large | True | 35,162.43 μs | 382.26 KB |

### Test Results

- All 1787 Seiton.Core.Tests pass
- All 159 Seiton.Tests pass
- 6 new tests added (folded clip, folded strip, folded CRLF, message sanitization, literal multi-line message, corruption prevention)

### Phase 2 Performance Analysis

**`CollapseInternalWhitespace` design:**
- Guard: `rawText.Contains('\n')` — O(n) SIMD-accelerated scan, fast-path returns false for single-line scalars (99%+ of cases)
- Allocations: Zero on common path. `StringBuilder(text.Length)` only on rare block-scalar diagnostic path
- Algorithm: Single O(n) pass, no regex, pre-allocated capacity prevents resizing
- Impact: Zero measurable impact on benchmark (identical allocation numbers)

**Why no regression**: The message sanitization code executes ONLY when:
1. A diagnostic is emitted (not hot path)
2. The raw value string contains `\n` (block scalars only)
3. Both conditions are rare — typical workflows use plain or quoted scalars for `if:`
