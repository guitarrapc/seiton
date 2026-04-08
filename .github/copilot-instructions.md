# Seiton - Project Instructions

## What is this project?


## Project Structure

## Important Guidelines

When implementing or reviewing parser algorithms, refer to these detailed guides:

- **[Architecture](agent_docs/architecture.md)** - Understand the Parser's overall architecture and design principles
- **[Coding Style](agent_docs/coding_style.md)** - C# style conventions for this project
- **[Performance Requirements](agent_docs/performance_requirements.md)** - Zero-allocation, aggressive inlining, and memory management
- **[Testing Guidelines](agent_docs/testing_guidelines.md)** - Writing/Run effective unit tests

**Script Rule:** Don't write any multi-line PowerShell Code in the shell. If you need to run a script, create a file then executte it.

### Parser Allocation Guardrails (Always-On)

For files under `src/Seiton.Core/Parsing/**`, follow these rules strictly.

1. Hot path key/value checks must use UTF-8 span comparisons (`ReadOnlySpan<byte>`), not `string` comparisons.
2. Avoid `GetScalarString()` and `Encoding.UTF8.GetString(...)` in normal success paths.
3. String conversion is allowed only for diagnostics, logs, and explicit fallback paths.
4. Do not introduce `new T[]`, `List<T>`, `Dictionary<TKey, TValue>`, LINQ, or regex in parser hot paths.
5. Reuse parsed metadata (for example event spec resolution) instead of repeated lookups.
6. If a value must be kept, prefer `Utf8Slice`/offset-length over materialized `string`.

Before completing parser changes, validate all of the following.

1. No new `GetScalarString()` usage was added on success paths.
2. Newly introduced key checks are UTF-8 span based.
3. Diagnostics still show useful text when errors occur.
4. Parser test suite passes.

## How to Work on This Project

### Running Tests

See [Testing Guidelines](agent_docs/testing_guidelines.md) for details on writing and running tests. To run all tests:

```shell
dotnet test
```

To run specific tests (e.g., XxxxxTests):

```shell
dotnet run --treenode-filter /*/*/XxxxxTests/*
```

### Running Benchmarks

```shell
cd src/Seiton.Benchmark
dotnet run -c Release
```

### Building the Project

```shell
dotnet build
```

### Run Some Script

You can create a .cs file in `sandbox/DotnetFiles/` and run it directly. See [Sandbox Code Guidelines](agent_docs/sandbox_code_guidelines.md) for details and script sample.

use `sandbox/DotnetFiles/Sample.cs` for template:

```shell
dotnet run sandbox/DotnetFiles/YourCsFile.cs
```

## Progressive Disclosure

Before implementing a parser/ast or making significant changes:

1. Read the relevant documentation files in `.github/agent_docs/`
2. Review existing similar implementations in `src/`
3. Check corresponding tests in `tests/Seiton.Tests/`

Ask which documentation files you need if you're unsure what to read.

## Specification Document Policy

Spec files live under `Docs/`. When reading or writing them, follow these rules:

**What belongs in a spec:**
- **WHAT** — what the feature or behavior is
- **WHY** — the reasoning and motivation behind the decision
- **Lessons learned** — things that were only discovered by actually trying (e.g., unexpected constraints, failed approaches, surprising behavior)

**What does NOT belong in a spec:**
- Detailed HOW — step-by-step implementation instructions, code structure, algorithm internals. Those belong in code comments, `agent_docs/`, or the implementation itself.

**After implementing:**
- Always update any related spec files to reflect what was actually built, especially documenting lessons learned or decisions made during implementation that weren't captured upfront.

**Cross-document consistency:**
- `Seiton_Parser_spec.md` is the **source of truth** for the parser specification. When it is revised, you **must** review and update the following downstream documents for consistency:
  - `Seiton_Parser_csharp_spec.md` — C# implementation spec (AST types, adapter layer, etc.)
  - `Seiton_Parser_go_spec.md` — Go reference implementation spec (AST definitions, parse functions, etc.)
  - `parser_implementation_csharp_plan.md` — C# implementation plan (phase/step references to spec sections)
- Conversely, if a downstream doc is updated with new implementation details or lessons learned, check whether the change implies a spec-level update to `Seiton_Parser_spec.md`.
