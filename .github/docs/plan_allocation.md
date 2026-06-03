# Diagnostic Output Allocation Investigation

## Scope

This document investigates whether the following items are implementable and how to proceed:

1. Diagnostic output UTF-8 migration
2. Allocation reduction for `ExtractLines` (`string[]` and per-line `string` materialization)

## Investigation Result

### 1) Diagnostic output UTF-8 migration

**Status: Implemented (done).**

Current implementation already uses a UTF-8-first path:

- Formatter entrypoint is unified at `DiagnosticFormatter.Write(IBufferWriter<byte>, ...)`.
- CLI stdout path is `WriteToStandardOutput(...)` -> pooled UTF-8 buffer -> flush.
- `json` and `sarif` write directly with `Utf8JsonWriter` to `IBufferWriter<byte>`.
- Text/GitHub Actions output uses `Utf8Writer`.
- `WriteToTextWriter(...)` remains as a decode-only adapter for injection/tests.

Conclusion:

- Diagnostic UTF-8 migration is fully implementable and already completed in scope.
- No additional migration task is required for this item.

### 2) `ExtractLines` string-array reduction

**Status: Implemented (done).**

## Implementation (ExtractLines / LineSlice)

### Changes

- Replaced `ExtractLines` (`string[]` + per-line `Encoding.UTF8.GetString`) with `ExtractLineSlices` (`LineSlice` start/length metadata).
- Rich-text gutter rendering writes snippet lines via `Utf8Writer.WriteLiteral` over source bytes (no per-line `string`).
- `lineCount <= 16`: `stackalloc LineSlice[]`; larger spans rent from `ArrayPool<LineSlice>`.
- Public API unchanged; output strings unchanged.

### API review

| Aspect | Result |
|---|---|
| User-facing API | No change (`DiagnosticFormatter.Write`, CLI flags, output format) |
| Internal API | `LineSlice` is private to `DiagnosticFormatter`; callers unaffected |
| Testability | Existing `DiagnosticFormatterRichTextTests` snippet equivalence classes pass (2410 tests) |

### Performance (`DiagnosticOutputBenchmark`, ShortRun, vs pre-change baseline)

| Format | Count | Metric | Before | After | Change | Verdict |
|---|---|---|---|---|---|---|
| text rich | F1 | Mean | 239.7 µs | 226.5 µs | **−5.5%** | Improved |
| text rich | F1 | Allocated | 8.35 KB | 1.65 KB | **−80%** | Improved |
| text rich | F10 | Mean | 2329.7 µs | 2316.5 µs | −0.6% | OK |
| text rich | F10 | Allocated | 72.67 KB | 5.64 KB | **−92%** | Improved |
| github-actions rich | F1 | Allocated | 8.35 KB | 1.65 KB | **−80%** | Improved (shared snippet path) |
| github-actions rich | F10 | Allocated | 72.67 KB | 5.70 KB | **−92%** | Improved |

**Why allocation improved**

- Removed `string[]` for each snippet extraction.
- Removed one heap `string` per displayed source line (`GetString` on every line in range).
- Snippet bytes are copied once into the output buffer via `WriteLiteral` instead of decode-then-re-encode.

**Why mean improved slightly (F1) or stayed flat (F10)**

- Less GC pressure from fewer short-lived strings.
- Single-pass newline scan unchanged; added work is cheap span slicing vs UTF-16 string materialization.

**If mean had regressed**

- Mitigation would be to reduce `endLine.ToString().Length` allocations (pre-existing) or cache line-index tables per file in `sourceMap` for repeated diagnostics on the same file.

### Review (post-implementation)

| Finding | Action |
|---|---|
| Snippet output parity | Covered by existing rich-text tests (single/multi-line, CRLF, missing map, line beyond file) |
| `stackalloc` / `ArrayPool` split at 16 lines | Matches typical diagnostic spans; pool only for unusually wide ranges |
| Spec drift | `Seiton_CLI_csharp_spec.md` §7.3 updated to describe byte-slice snippet extraction |

## Implementation Decision (final)

- **Diagnostic UTF-8 migration:** complete (no further action).
- **`ExtractLines` allocation reduction:** complete.
- Future optional work: per-file line-index cache in `sourceMap` if profiling shows repeated full-file scans dominate.

---

Snippet rendering no longer materializes per-line strings; rich text formatting keeps the same output with much lower allocation.
