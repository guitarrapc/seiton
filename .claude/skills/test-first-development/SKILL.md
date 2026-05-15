---
name: test-first-development
description: Mandatory test-first workflow for all implementation, modification, and bug fix tasks in this project. Covers red-green test cycle, regression tests, benchmark verification, and spec updates. Applies whenever code under src/ is added or changed.
---

# Test-First Development

**This skill is mandatory for every task that adds or modifies code under `src/`.**

Skip this skill only when the change is limited to documentation, configuration, or generated files (via `Seiton.Update`).

## Workflow

### 1. Write Failing Tests First (Red)

Before writing any production code, create tests that demonstrate the current behavior is wrong or missing.

- **New feature**: Write a test that exercises the new behavior and verify it fails (compile error or assertion failure).
- **Bug fix**: Write a test that reproduces the bug and verify it fails.
- **Modification**: Write a test that asserts the new expected behavior and verify it fails against the current code.

Run the failing test to confirm:

```shell
dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/YourTestClass/YourTestMethod*
```

### 2. Implement (Green)

Write the minimum production code to make the failing test pass. Then run the test again to confirm it passes.

### 3. Run Full Test Suite

After the implementation passes targeted tests, run all tests to catch regressions:

```shell
dotnet test
```

All tests must pass before proceeding.

### 4. Add Regression Tests

For bug fixes, the test written in Step 1 often doubles as the regression test. If Step 1 already covers the fix scenario, you do not need a separate test — but verify it matches the pattern below. For new features, add edge-case tests beyond the initial happy-path test from Step 1.

Regression test patterns by change type:

| Change type | Test pattern | Assertion |
|---|---|---|
| False positive fixed (was erroring on valid input) | `ok-*` case or valid-input test | Zero diagnostics |
| False negative fixed (was missing an error) | `ng-*` case or invalid-input test | Expected diagnostic message appears |
| Parser fix | `ParserTests` method | AST structure is correct |
| Linter rule fix | `RuleInterfaceTests` case or dedicated test | Correct diagnostics emitted |

### 5. Benchmark Verification

When changing parser or linter code, run benchmarks:

```shell
cd src/Seiton.Benchmark
dotnet run -c Release
```

Compare results against the previous baseline in `BenchmarkDotNet.Artifacts/results/` (committed report files). If no prior report exists, run the benchmark on `main` branch first to establish a baseline.

- **Mean**: must not increase by more than +10%
- **Allocated**: must not increase by more than +10%

Relevant benchmarks by change area:

| Changed area | Benchmark to check |
|---|---|
| `src/Seiton.Core/Parsing/` | `CoreParsingBenchmark` (Small/Medium/Large) |
| `src/Seiton.Core/Linting/` | `CoreLintBenchmark` (parse+lint Mean and Allocated) |

### 6. Update Specs

If the implementation changes observable behavior or adds new functionality, update the relevant specification:

- Parser changes → `Seiton_Parser_spec.md`, `Seiton_Parser_csharp_spec.md`
- Linter changes → `Seiton_Linter_spec.md`, `Seiton_Linter_csharp_spec.md`

## Test Conventions

### Naming

- Class: `{Feature}Tests` (e.g., `ParserTests`, `ExpressionTests`)
- Method: `{Action}_{Context}_{ExpectedOutcome}` (e.g., `Parse_MinimalWorkflow_NoDiagnostics`)

### Framework

This project uses **TUnit**. Use `--treenode-filter` for targeted runs. Do NOT use `dotnet test --filter`.

```shell
# Run all tests in a class
dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/ParserTests/*

# Run a single test
dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/ParserTests/Parse_MinimalWorkflow*
```

### Fixture Patterns

- **Inline YAML**: Use raw string literals for small, self-contained test cases.
- **File fixtures**: Use `tests/Seiton.Core.Tests/fixtures/` for corpus-based tests.
  - Valid inputs: `ok/` directory or `ok-*` prefix
  - Invalid inputs: `err/` directory or `ng-*` prefix
  - Expected output: `.out` files paired with `.yaml` files

### Assertions

Use TUnit async assertions:

```csharp
await Assert.That(result.IsFatal).IsEqualTo(false);
await Assert.That(result.Diagnostics).HasCount().EqualTo(0);
```

## Test Design Guardrails

- Prefer black-box tests that verify observable behavior through the public API or a stable integration seam.
- Do not use reflection to invoke private methods or read/write private fields in tests. Those tests are brittle and usually indicate the wrong test target.
- If a behavior is important but hard to reach through the public surface, first look for a user-visible scenario that exercises it end to end.
- Only add a narrow `internal` test seam with `InternalsVisibleTo` when a black-box test is not practical and the seam itself represents a stable concept worth naming.
- Avoid writing tests whose main assertion is about a private helper method. Test the behavior that helper exists to produce.
