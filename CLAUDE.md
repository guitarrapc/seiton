# Seiton - Project Instructions

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
