---
name: test-first-development
description: Mandatory test-first workflow for all implementation, modification, and bug fix tasks in this project. Covers red-green test cycle, regression tests, benchmark verification, spec updates, equivalence-class coverage, and Playground (WASM/UI) test constraints. Applies whenever code under src/ is added or changed.
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

**When fixing classification logic bugs**: Step 1 writes the single failing test that reproduces the bug. After the fix passes (Step 2), add the remaining equivalence-class tests HERE in Step 4 — not in Step 1. This keeps the red-green cycle tight while still achieving full class coverage.

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
- Playground changes → `Seiton_Playground_spec.md`, `Seiton_Playground_csharp_spec.md`

## Playground tests (`tests/Seiton.Playground.Tests`)

Playground changes touch **Core logic** (`src/Seiton.Playground.Core`), **WASM host** (`src/Seiton.Playground`), and **browser UI** (`wwwroot/`). Use the **cheapest test layer that can fail first**, then add broader coverage.

### Test layers (run in this order when bisecting)

| Layer | When to use | Examples |
|-------|-------------|----------|
| **Fast / no browser** | Logic, JSON, share payload, HTML contract | `PlaygroundLintRunnerTests`, `PlaygroundSharePayloadTests`, `PlaygroundHtmlContractTests` |
| **Incremental parse/lint** | `IncrementalParseContext`, D-5b/c/d | `IncrementalParseTests`, `IncrementalLintCacheTests` |
| **Desktop WASM repro** | Bug is WASM/AOT-specific but avoid Playwright | `PlaygroundWasmMemoryOobDesktopTests` — calls `PlaygroundLintRunner` on CoreCLR |
| **Browser + published WASM** | Real Mono WASM, Release+AOT bundle, UI hooks | `PlaygroundUiLayoutTests`, `PlaygroundWasmMemoryOobUiTests` — needs Playwright + `PlaygroundUiTestHost` |

**Rule**: Add or extend **`PlaygroundWasmMemoryOobDesktopTests` (or `PlaygroundLintRunnerTests`) before** adding Playwright tests. Desktop green + browser red narrows the problem to WASM/interop/JS.

### Running Playground tests (TUnit)

Project: `tests/Seiton.Playground.Tests`. Use `--project` and `--treenode-filter` (never `--filter`).

```shell
# Entire playground assembly (slow if UI tests publish WASM in-process)
dotnet test --project tests/Seiton.Playground.Tests --maximum-parallel-tests 1

# Single class (preferred for red-green)
dotnet test --project tests/Seiton.Playground.Tests --treenode-filter /*/*/PlaygroundLintRunnerTests/*

# Single method
dotnet test --project tests/Seiton.Playground.Tests --treenode-filter /*/*/PlaygroundLintRunnerTests/RunToJson_ValidMinimalWorkflow*
```

**Prerequisites for UI tests**: `dotnet workload install wasm-tools`, Playwright Chromium (`pwsh tests/Seiton.Playground.Tests/bin/Release/net10.0/playwright.ps1 install chromium` after build).

### Memory and parallelism (mandatory on dev machines)

Playground tests share **static** `PlaygroundLintRunner` / `PlaygroundUiTestHost` and can trigger **in-test `dotnet publish` (WASM AOT)** — peak RAM can reach **tens of GB**. All classes use `[NotInParallel(PlaygroundTestParallelism.AssemblyLockKey)]`; do not remove without updating `.github/docs/plan_memory.md`.

**Local / CI-safe run** — pre-publish and point the test host at artifacts (same as `.github/workflows/build.yaml`):

```shell
dotnet publish src/Seiton.Playground/Seiton.Playground.csproj -c Debug -o publish/playground-dbg/ \
  -p:RunAOTCompilation=false -p:PublishTrimmed=false -p:PlaygroundSoftFingerprint=true
dotnet publish src/Seiton.Playground/Seiton.Playground.csproj -c Release -o publish/playground-aot/ \
  -p:RunAOTCompilation=true -p:PublishTrimmed=true -p:PlaygroundSoftFingerprint=true

export SEITON_PLAYGROUND_PUBLISH_DIR_DEBUG="$(pwd)/publish/playground-dbg"
export SEITON_PLAYGROUND_PUBLISH_DIR_RELEASE="$(pwd)/publish/playground-aot"

dotnet test --project tests/Seiton.Playground.Tests --maximum-parallel-tests 1
```

(PowerShell: set `$env:SEITON_PLAYGROUND_PUBLISH_DIR_DEBUG` / `_RELEASE` instead of `export`.)

Without these env vars, the first UI test that needs `PlaygroundWasmPublishMode.ReleaseAot` runs **AOT publish inside the test process** — avoid that during routine red-green loops.

Further context: `.github/docs/plan_memory.md`.

### Playground-specific red-green patterns

| Change | First failing test to write | Notes |
|--------|----------------------------|-------|
| `PlaygroundLintRunner` / config cache | `PlaygroundLintRunnerTests` | Config-mutating tests use `[NotInParallel(ConfigLockKey)]` on methods |
| Incremental parse | `IncrementalParseTests` or `IncrementalParseContextTests` | New `IncrementalParseContext` per test unless testing shared runner |
| WASM OOB / incomplete YAML while typing | `PlaygroundWasmMemoryOobDesktopTests` then `PlaygroundWasmMemoryOobUiTests` | Bare trailing `- uses:` may trap AOT WASM; UI defers lint in `main.js` |
| `wwwroot` UI behavior | `PlaygroundHtmlContractTests` or `PlaygroundUiLayoutTests` | Layout tests use `PlaygroundWasmPublishMode.DebugFast` unless production bundle is required |
| Browser-only hooks | `?seitonTestHooks=1` + `__SEITON_PLAYGROUND_TEST__` | See `PlaygroundWasmMemoryOobUiTests` |

**Browser test host modes**:

- `PlaygroundUiTestHost.GetOrCreateAsync()` → Debug, fast iteration (layout tests).
- `GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot)` → matches GitHub Pages; use only when WASM/AOT behavior is under test.

When adding Playwright tests: prefer **new browser context per scenario** that can kill WASM; call `page.CloseAsync()` when reusing a shared browser session. Avoid unbounded loops of `runLint` on one page (WASM heap may grow up to `EmccMaximumHeapSize`).

### Benchmarks (Playground hot path)

When changing `PlaygroundLintRunner` or incremental playground paths:

```shell
cd src/Seiton.Benchmark
dotnet run -c Release --filter "*PlaygroundLintBenchmark*"
```

Same +10% Mean / Allocated rule as core benchmarks.

### New Playground test classes

- Add `[NotInParallel(PlaygroundTestParallelism.AssemblyLockKey)]` on every new test class in this assembly.
- Do not introduce a second parallel lock key unless config isolation truly requires it (`ConfigLockKey` on individual methods is fine).
- Assembly hooks reset shared state: `PlaygroundUiTestAssemblyHooks` → `PlaygroundLintRunner.ResetSharedStateForTests()`.

## Test Conventions

### Naming

- Class: `{Feature}Tests` (e.g., `ParserTests`, `ExpressionTests`)
- Method: `{Action}_{Context}_{ExpectedOutcome}` (e.g., `Parse_MinimalWorkflow_NoDiagnostics`)

### Framework

**This project uses TUnit. Always use `--treenode-filter` — do NOT use `dotnet test --filter` (that is xUnit/MSTest syntax and will not work).**

```shell
# Run all tests in a class
dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/ParserTests/*

# Run a single test
dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/ParserTests/Parse_MinimalWorkflow*
```

More examples:

```shell
# Run all tests in ExpressionTests
dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/ExpressionTests/*

# Run a single method by prefix match
dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/ExpressionTests/InferType_GitHubRetentionDays*
```

### Fixture Patterns

- **Inline YAML**: Use raw string literals for small, self-contained test cases.
- **File fixtures**: Use `tests/Seiton.Core.Tests/fixtures/` for corpus-based tests.
  - Valid inputs: `ok/` directory or `ok-*` prefix
  - Invalid inputs: `err/` directory or `ng-*` prefix
  - Expected output: `.out` files paired with `.yaml` files

**Line endings**: `tests/Seiton.Core.Tests/fixtures` are stored with LF line endings (`.gitattributes`). Actionlint compat tests normalize YAML newlines when loading fixtures so a Windows checkout with `core.autocrlf` does not shift byte indexes in golden `.out` / `.seiton.out` expectations.

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

## Classification Logic: Equivalence Class Coverage

When implementing or modifying **classification/decision logic** (e.g., path danger classification, version detection, expression type inference), apply equivalence class partitioning to ensure both positive AND negative cases are covered.

### Mandatory Steps

1. **Enumerate input variables** that affect the decision (e.g., `dotDotSegments`, `namedSegments`, `isRunnerTemp`).
2. **Build a truth table** of variable combinations that make each branch true/false. Each combination is an equivalence class.
3. **Write at least one test per class**, with priority on:
   - Cases where the condition is true AND should be true (true positive)
   - Cases where the condition is true BUT should be false (false positive — **these are the most commonly missed**)
   - Cases where the condition is false AND should be false (true negative)
   - Cases where the condition is false BUT should be true (false negative)
4. **For security rules**: negative tests (inputs that should NOT produce a diagnostic) must be **equal or greater in count** to positive tests (inputs that should produce a diagnostic). Security rules with high false-positive rates erode user trust.

### Example: Multi-Variable Condition

For a condition like `reachesRunnerTemp = (A >= 2 && B == 0) || (A >= 2 && C == 1 && D)`:

| A | B | C | D | Expected | Test case |
|---|---|---|---|---|---|
| 2 | 0 | - | - | true | `../..` (sweeps level) |
| 2 | 1 | 1 | true | true | `../../_temp` (targets temp) |
| 2 | 1 | 1 | false | **false** | `../../some-dir` (specific non-temp) |
| 1 | 0 | - | - | **false** | `..` (wrong depth) |
| 1 | 1 | 1 | true | **false** | `../_temp` (wrong depth for real temp) |

The bolded **false** rows are the ones most likely to be missed — they represent inputs where a naive/broad condition would fire but shouldn't.

### When to Apply

- Any `if`/`switch` with 3+ input variables affecting the decision
- Any heuristic that models real-world constraints (filesystem layout, version semantics)
- Any security rule that can produce false positives
- **Bug fixes on classification logic**: even when fixing a single false positive/negative, build the full truth table first. This prevents the fix from introducing new false positives in adjacent equivalence classes.
