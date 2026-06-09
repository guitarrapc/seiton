# Seiton - Project Instructions

## Working Principles

### Think Before Coding

Don't assume. Don't hide confusion. Surface tradeoffs.

- State assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them — don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

### Minimal & Surgical Changes

Minimum code that solves the problem. Touch only what you must.

- No features, abstractions, or error handling beyond what was asked.
- Don't "improve" adjacent code, comments, or formatting. Match existing style.
- Remove only imports/variables/functions that YOUR changes made unused.
- Every changed line should trace directly to the user's request.

### Goal-Driven Execution

Define success criteria. Loop until verified.

- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"

For multi-step tasks, state a brief plan with verification per step.

### Data-Oriented Design

Prefer data-oriented design with explicit types and explicit side-effect boundaries.

- Model domain state with simple typed data structures first; keep transformations deterministic.
- Make type intent obvious at API boundaries (input/output, ownership, nullability, lifetimes).
- Isolate side effects (I/O, network, time, environment access) to clear boundary layers so core parsing/linting logic remains pure where practical.
- Avoid over-OOP abstractions: do not add inheritance, service layers, or interface indirection unless there is a measured and recurring need.
- Prefer value/data modeling (`struct`, `record struct`, plain data classes) and explicit control flow over deep class hierarchies or behavior-heavy objects.
- Keep polymorphism at narrow extension points only (for example, rule/plugin boundaries). Do not spread dynamic dispatch across core hot paths.

## What is this project?

Seiton is a C# tool that parses and lints GitHub Actions documents (workflow files and action metadata files).
Target documents: `.github/workflows/*.yml` / `.github/workflows/*.yaml`, `action.yml` / `action.yaml`.

## Project Structure

```
.github/docs          — Specifications (source of truth)
src/Seiton/           — CLI entry point
src/Seiton.Core/      — Core library (Parsing/, Linting/, Generated/)
src/Seiton.Update/    — Generated data update tool
src/Seiton.Benchmark/ — Performance benchmarks
tests/                — Test projects
sandbox/DotnetFiles/  — Prototyping and verification scripts
data/                 — Generated data sources (manifest, availability, etc.)
docs/                 - User-facing usage documentations
```

## Skills

When implementing or reviewing, refer to these detailed skills documents for specific guidelines and best practices:

- `architecture/SKILL.md` — design principles and architecture of the parser
- `performance-requirements/SKILL.md` — performance and memory efficiency requirements for parser implementation
- `sandbox-code-guidelines/SKILL.md` — guidelines for writing and running sandbox
- `scripting/SKILL.md` — guidelines for writing and running scripts in the project
- `spec-document-policy/SKILL.md` — policy for reading and writing specification documents
- `test-first-development/SKILL.md` — mandatory test-first workflow for all implementation, modification, and bug fix tasks

## How to Work on This Project

Building the Project.

```shell
dotnet build
```

Running Tests. See the [test-first-development skill](.claude/skills/test-first-development/SKILL.md) for filtering examples, fixture conventions, and line-ending notes.

```shell
dotnet test
```

Running Benchmarks.

```shell
cd src/Seiton.Benchmark
dotnet run -c Release
```

Run Some Script. See [Sandbox Code Guidelines](.claude/skills/sandbox-code-guidelines/SKILL.md) for details and script sample.

```shell
dotnet run sandbox/DotnetFiles/YourCsFile.cs
```

## Progressive Disclosure

Before implementing a parser/ast or making significant changes:

0. Retrieve baseline benchmark for mean time and allocations.
1. Read the relevant documentation files in `.github/docs`
2. Review existing similar implementations in `src/`
3. Check corresponding tests in `tests/Seiton.Tests/`
4. Run regression tests and benchmarks to validate assumptions and measure impact.

Ask which documentation files you need if you're unsure what to read.
