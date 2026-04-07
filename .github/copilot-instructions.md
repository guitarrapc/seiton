# Seiton - Project Instructions

## What is this project?


## Project Structure

## Important Guidelines

When implementing or reviewing sorting algorithms, refer to these detailed guides:

- **[Architecture](agent_docs/architecture.md)** - Understand the Context + SortSpan pattern
- **[Coding Style](agent_docs/coding_style.md)** - C# style conventions for this project
- **[Performance Requirements](agent_docs/performance_requirements.md)** - Zero-allocation, aggressive inlining, and memory management
- **[Testing Guidelines](agent_docs/testing_guidelines.md)** - Writing/Run effective unit tests

**Script Rule:** Don't write any multi-line PowerShell Code in the shell. If you need to run a script, create a file then executte it.

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

Before implementing a new sorting algorithm or making significant changes:

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
