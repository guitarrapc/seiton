# Rich Diagnostic Output — Implementation Plan

> Tracks the implementation of Rust-style rich diagnostic output for the `seiton check` command.
> Spec contract is defined in `Seiton_CLI_spec.md` §7.1.

---

## 1. Background

Previously, `seiton check` emitted diagnostics in a single-line format regardless of the `--oneline` flag (the flag was accepted but had no effect). There was no source-snippet rendering.

The goal is to add rich multi-line output inspired by Rust's compiler error format, where the problem line is quoted and the relevant column range is underlined with `^` characters.

---

## 2. Design Decisions

### 2.1 Where source text lives

`Seiton.Core` intentionally has no I/O concern. `LintEngine.Check()` takes `byte[]` and returns `LintResult` (containing `Diagnostic[]`). Source bytes are not stored in `LintResult`.

The CLI layer (`CheckCommand`) already reads source bytes per file. The decision is to keep source bytes in the CLI layer and pass them to `DiagnosticFormatter` as a `IReadOnlyDictionary<string, byte[]>` keyed by file path.

This avoids any change to `Seiton.Core` and keeps the formatter stateless.

### 2.2 `--oneline` semantics

`--oneline` is now the compact single-line mode (previous default behavior). The new default (without `--oneline`) is the rich multi-line mode. This is a visible behavior change but improves the out-of-the-box experience.

### 2.3 Source map allocation strategy

The source map is only allocated when `format == text && !oneline`. JSON and SARIF formats do not benefit from source snippets, so no memory is held for those paths.

### 2.4 Column-to-caret mapping

`TextRange.StartColumn` and `TextRange.EndColumn` are 1-based. Caret count = `max(1, EndColumn - StartColumn)`. Leading spaces before carets = `StartColumn - 1`.

For multi-line spans (`EndLine > StartLine`), all lines are shown with `/ ... |___^` prefix/suffix fencing identical to rustc's multi-line rendering.

---

## 3. Files Changed

| File | Change |
|---|---|
| `src/Seiton/Output/DiagnosticFormatter.cs` | Added rich output mode, source snippet extraction, `sourceMap` parameter |
| `src/Seiton/Commands/CheckCommand.cs` | Build `sourceMap` during file lint loop; pass to `DiagnosticFormatter.Write()` |
| `Docs/Seiton_CLI_spec.md` | Updated §7.1 to specify rich format and `--oneline` compact format |

---

## 4. Completed Steps

- [x] `DiagnosticFormatter.Write()` signature extended with optional `IReadOnlyDictionary<string, byte[]>? sourceMap`
- [x] `WriteText()` dispatches to rich format (default) or compact format (`--oneline`)
- [x] `WriteRichDiagnostic()` emits header, location arrow, source snippet, and help annotation
- [x] `WriteSourceSnippet()` extracts UTF-8 lines from source bytes, renders single-line and multi-line spans
- [x] `ExtractLines()` extracts a range of lines from UTF-8 bytes without allocating the full string array upfront
- [x] `CheckCommand` allocates source map only for `text` non-oneline mode
- [x] `Seiton_CLI_spec.md` §7.1 updated with rich format spec

---

## 5. Known Limitations and Future Work

| Item | Priority | Notes |
|---|---|---|
| Display-width awareness for multi-byte UTF-8 characters | Low | East-Asian wide characters count as 2 display columns; current impl counts codepoint boundary bytes only. Acceptable for most YAML content. |
| `Diagnostic.Help` is rarely populated by rules | Medium | Rules currently emit no `Help` text. Populating `Help` in rules would make rich output more actionable. |
| `FixCommand` does not yet use rich output | Low | `FixCommand` currently shares output formatting call; can be wired up the same way. |
| `DiagnosticFormatter` tests for rich format | Medium | Unit tests for snippet rendering and caret alignment should be added. |
| `--context-lines` flag for configurable snippet context | Low | Currently shows only the exact diagnostic line(s). Could optionally show N surrounding lines for context. |
