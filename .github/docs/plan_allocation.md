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

**Status: Not implemented yet, but implementable.**

Current behavior in `DiagnosticFormatter`:

- `ExtractLines(byte[] utf8, int startLine, int endLine)` allocates:
  - `string[]` of target lines
  - one `string` per extracted line via `Encoding.UTF8.GetString(...)`
- Rich-text snippet rendering then writes those strings.

Allocation impact:

- This path is inside rich diagnostic rendering (`WriteSourceSnippet`), i.e. hot when many diagnostics include source snippets.
- Existing benchmarks already improved greatly, but this remains a residual allocation source.

Feasibility:

- High. The formatter already writes UTF-8 bytes via `Utf8Writer`, so snippet lines can be emitted directly from source bytes without converting each line to `string`.

## Proposed Direction

### Goal

Reduce snippet-path allocations by removing `string[]` and per-line string materialization in `ExtractLines`.

### Candidate approach (recommended)

Replace `ExtractLines` output from `string[]` to byte-range metadata:

- Introduce a small value-type slice descriptor (e.g. `LineSlice { Start, Length }`).
- Parse newline boundaries once and collect slices for `[startLine..endLine]`.
- Keep CR trimming behavior (`\r\n` -> strip trailing `\r`).
- Update gutter writers to emit source lines with `Utf8Writer.WriteLiteral(sourceBytes.AsSpan(start, length))`.
- Keep existing caret and gutter formatting behavior unchanged.

Expected benefit:

- Remove `string[]` allocation.
- Remove per-line `Encoding.UTF8.GetString(...)` allocations.
- Keep user-visible output contract unchanged (same line content and formatting).

## Risks and Mitigations

- **Risk: subtle output drift in snippet rendering**
  - Mitigation: golden string tests on rich text output (single-line span, multi-line span, CRLF source, missing source map).
- **Risk: line/column/caret regressions**
  - Mitigation: branch-coverage tests for single-line and multi-line ranges, including edge clamping paths.
- **Risk: performance tradeoff not guaranteed for tiny inputs**
  - Mitigation: benchmark before/after and accept only if allocation improves and mean regression stays within project threshold.

## Validation Plan

1. Add/adjust tests first (red/green):
   - Rich text snippet parity tests for representative diagnostics.
   - Equivalence classes for source snippet extraction:
     - single-line span
     - multi-line span
     - CRLF file
     - source shorter than requested line range
     - no source map
2. Run full test suite.
3. Run `DiagnosticOutputBenchmark` and compare with current baseline.
4. Update specs/documents only if externally observable behavior changes.

## Implementation Decision

- **Diagnostic UTF-8 migration:** complete (no further action required).
- **`ExtractLines` allocation reduction:** implement as a follow-up optimization task.
- Priority recommendation: **Medium** (worth doing, but not blocker because major migration is already done).

---

やったー: The core UTF-8 migration is already finished. Next win is focused cleanup of snippet-path allocations.
