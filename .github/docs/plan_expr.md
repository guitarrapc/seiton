# Plan: Local Reusable Workflow Output Resolution for `expr-undefined-var`

## 1. Goal

Enable `expr-undefined-var` to validate `needs.<reusable-job>.outputs.*` references when the called reusable workflow is **local** (`uses: ./.github/workflows/...`). Currently these are treated as loose (any property access is accepted). After this change, local reusable workflow outputs will be resolved to strict types, and unknown output names will be flagged.

Scope:

- **In scope**: Local reusable workflow references (`./` or `../` prefix, no `@ref`)
- **Out of scope**: Remote reusable workflow references (`owner/repo/path@ref`) — these remain loose. Remote resolution requires network access and belongs to a future online rule extension.

## 2. Current Behavior

In `DynamicContextTypeBuilder.BuildJobOutputsType()`:

```csharp
if (job.WorkflowCall is not null)
{
    // Return loose type so needs.<job>.outputs.* is not flagged as undefined.
    return ExprType.Object(dynamicPropertyType: ExprType.String);
}
```

All reusable workflow call jobs (both local and remote) return a **loose** `ObjectExprType`. This means `needs.<reusable-job>.outputs.anything` is always accepted without validation.

Existing test coverage confirms this in `RuleRegression_ExprUndefinedVarRule_ReusableWorkflowCallNeedsOutputs_TableDriven`:

- `ok-reusable-workflow-call-needs-outputs` — remote uses, any output accepted (loose)
- `ok-local-reusable-workflow-call-needs-outputs` — local uses, any output accepted (loose)

## 3. Design

### 3.1 Architecture

Introduce `LocalReusableWorkflowOutputResolver` — a resolver class analogous to the existing `LocalActionOutputResolver`. It:

1. Accepts a local `uses:` reference (`./.github/workflows/reusable.yml`)
2. Resolves the file path relative to the current workflow
3. Parses the target workflow with `WorkflowParser.Parse`
4. Extracts `on.workflow_call.outputs` keys
5. Caches results per resolved path (same workflow referenced by multiple jobs)
6. Returns output names as `string[]?` (null = unresolvable, empty = no outputs)

### 3.2 Integration Point

`DynamicContextTypeBuilder.BuildJobOutputsType()` currently takes `(Job job, byte[]? utf8Yaml)`. It will gain an optional resolver parameter:

```
BuildJobOutputsType(Job job, byte[]? utf8Yaml, Func<ReadOnlyMemory<byte>, string[]?>? localReusableOutputResolver = null)
```

When `job.WorkflowCall is not null`:

1. Check if `uses:` starts with `./` or `../` (local reference)
2. If local and resolver is available → call resolver → return strict type if resolved
3. Otherwise → return loose type (existing behavior)

This also affects `FindJobOutputsType` which calls `BuildJobOutputsType`, and the two `BuildNeedsOverride` / `BuildNeedsOverrideInto` methods that call `FindJobOutputsType`.

### 3.3 Resolver Wiring

In `ExprUndefinedVarRule`:

- `VisitWorkflowPre`: Create `LocalReusableWorkflowOutputResolver` (same pattern as `_localActionOutputResolver`), store as `_localReusableOutputResolver` and `_localReusableOutputResolverFunc`.
- Pass the resolver func through `BuildNeedsOverrideInto` → `FindJobOutputsType` → `BuildJobOutputsType`.

### 3.4 Code Reuse

The existing `ReusableWorkflowRule` has nearly identical local workflow resolution logic (`TryResolveLocalWorkflowPath`, `GetLocalWorkflowContract`, path resolution helpers). However, directly reusing `ReusableWorkflowRule` internals is not ideal because:

- `ReusableWorkflowRule` methods are private and coupled to rule diagnostics
- `LocalWorkflowContract` extracts inputs/secrets but **not outputs**

The new resolver will share the same path resolution pattern (extract from `LocalActionOutputResolver` which has the identical `TryGetRepositoryRoot` / `ResolveLocalReferenceBaseDirectory` / `TrimCurrentDirectoryPrefix` helpers). The output extraction is a small addition: iterate `WorkflowCallEvent.Outputs` keys.

If refactoring to share path helpers between `LocalActionOutputResolver`, `LocalReusableWorkflowOutputResolver`, and `ReusableWorkflowRule` is clean and doesn't increase complexity, it may be done. Otherwise, the duplication is minimal (3 small static methods) and acceptable.

### 3.5 Signature Changes

Methods that need the resolver parameter added:

| Method | Current Signature | Change |
|---|---|---|
| `BuildJobOutputsType` | `(Job, byte[]?)` | Add `Func<ReadOnlyMemory<byte>, string[]?>?` |
| `FindJobOutputsType` | `(ReadOnlySpan<byte>, SliceMap<Job>, byte[]?)` | Add resolver func, plus `AstArena` (to read `uses:` value) |
| `BuildNeedsOverride` | `(StringNodeId[]?, SliceMap<Job>, AstArena, byte[]?)` | Add resolver func |
| `BuildNeedsOverrideInto` | `(Dictionary, StringNodeId[]?, SliceMap<Job>, AstArena, byte[]?)` | Add resolver func |

All new parameters are optional (`= null`) to avoid breaking existing call sites (e.g. `BuildJobsOverride` for workflow_call output validation does not need this resolver).

## 4. Performance Constraints

**Hard limit: +1% max regression on both Mean and Allocated.**

### 4.1 Why This Change Is Zero-Cost for Normal Workflows

The benchmark fixtures (`WorkflowYamlBuilder.Build`) generate workflows with **no reusable workflow calls** — all jobs are normal `runs-on` + `steps` jobs. The new code path is behind `if (job.WorkflowCall is not null)` which is already the existing branch. When `WorkflowCall is null`, no new code executes.

For workflows **with** reusable workflow calls:

- The resolver is only created if `Config.FilePath` is fully qualified (same guard as `LocalActionOutputResolver`)
- File I/O only occurs for local `./` references; remote references return immediately
- Results are cached per resolved path — multiple `needs:` references to the same reusable workflow parse once
- No new allocations in the hot path for non-reusable jobs

### 4.2 Allocation Budget

| Component | Allocation | When |
|---|---|---|
| `LocalReusableWorkflowOutputResolver` instance | 1 object + 1 `Dictionary<string, string[]?>` | Once per workflow, only when `Config.FilePath` is set |
| File.ReadAllBytes | 1 byte[] per unique local workflow | Once per unique local workflow file (cached) |
| WorkflowParser.Parse | Parser arena + AST | Once per unique local workflow file (cached) |
| string[] output names | 1 array per unique local workflow | Once per unique local workflow file (cached) |
| Dictionary entries in strict type | Per output name | Only when outputs are found |

All allocations are **per-unique-local-workflow**, not per-job or per-expression. For workflows with zero local reusable calls, only the resolver instance is allocated (if `Config.FilePath` is set — which already happens for `LocalActionOutputResolver`).

### 4.3 Verification Plan

1. Run `CoreLintBenchmark` before and after — compare Mean and Allocated for all 6 scenarios (Small/Medium/Large × Fix off/on)
2. Run `CoreParsingBenchmark` before and after — parsing should be completely unaffected
3. Acceptance criteria: **all scenarios within +1% on both Mean and Allocated**

## 5. Baseline Benchmarks (Pre-Implementation)

### CoreLintBenchmark

| Size | FixEnabled | Mean (μs) | Allocated (KB) |
|---|---|---|---|
| Small | False | 261.3 | 24.06 |
| Small | True | 287.6 | 25.52 |
| Medium | False | 4,649.2 | 137.28 |
| Medium | True | 5,615.2 | 150.64 |
| Large | False | 53,120.9 | 710.08 |
| Large | True | 84,969.3 | 764.91 |

### CoreParsingBenchmark

| Size | Benchmark | Mean (μs) | Allocated (KB) |
|---|---|---|---|
| Small | WorkflowParser.Parse | 121.3 | 3.87 |
| Medium | WorkflowParser.Parse | 2,852.6 | 35.70 |
| Large | WorkflowParser.Parse | 41,286.3 | 215.72 |

## 6. Implementation Steps

### Step 1: Write Failing Tests (Red)

Add test cases to `RuleInterfaceTests`:

1. **`ng-local-reusable-workflow-unknown-output`**: Local reusable workflow with `on.workflow_call.outputs` that defines `version`, caller references `needs.<job>.outputs.typo_output` → should flag `"typo_output" is not defined in "needs" context`.

2. **`ok-local-reusable-workflow-known-output`**: Local reusable workflow with `on.workflow_call.outputs` that defines `version`, caller references `needs.<job>.outputs.version` → should produce zero diagnostics.

3. **`ng-local-reusable-workflow-no-outputs`**: Local reusable workflow with `on.workflow_call` but no `outputs:`, caller references `needs.<job>.outputs.something` → should flag `"something" is not defined in "needs" context`, because a called workflow with no declared outputs resolves to a strict empty outputs object.

4. **`ok-remote-reusable-workflow-unchanged`**: Remote reusable workflow reference (`owner/repo/path@ref`) → should remain loose (no diagnostic). Existing test covers this but re-confirm no regression.

Test fixtures: Create local reusable workflow YAML files under `tests/Seiton.Core.Tests/fixtures/` for the resolver to find. Use a temporary directory structure or the existing project-relative fixture pattern.

Verify all new tests **fail** (the ng cases produce no diagnostic, the ok cases may already pass).

### Step 2: Implement `LocalReusableWorkflowOutputResolver`

Create `src/Seiton.Core/Linting/LocalReusableWorkflowOutputResolver.cs`:

- Constructor: `(string workflowFilePath)`
- Method: `string[]? ResolveOutputNames(ReadOnlySpan<byte> usesValue)`
  - Guard: only `./ ` or `../` prefix, no `@` in value
  - Resolve path using same pattern as `LocalActionOutputResolver`
  - Parse workflow, find `WorkflowCallEvent`, extract `Outputs` keys
  - Cache results per resolved path
  - Return `null` if unresolvable, `string[0]` if no outputs, `string[N]` for N output names

### Step 3: Thread Resolver Through `DynamicContextTypeBuilder`

1. Add optional `Func<ReadOnlyMemory<byte>, string[]?>? localReusableOutputResolver = null` parameter to `BuildJobOutputsType`, `FindJobOutputsType`, `BuildNeedsOverride`, `BuildNeedsOverrideInto`.

2. In `BuildJobOutputsType`, when `job.WorkflowCall is not null`:
   - Extract `uses:` value from `job.WorkflowCall.Uses`
   - Check if local reference (`./` or `../`)
   - If local and resolver available → call resolver
   - If resolver returns `string[]` with names → build strict outputs type
   - If resolver returns `string[0]` → could return strict empty (caller explicitly declared no outputs) or loose. **Decision**: return strict empty — if the called workflow declares no `on.workflow_call.outputs`, then `needs.<job>.outputs.anything` is genuinely undefined.
   - If resolver returns `null` → return loose (file not found, parse error, etc.)
   - If remote reference → return loose (existing behavior)

3. Note: `BuildJobOutputsType` will also need `AstArena` to read `job.WorkflowCall.Uses` value. Add `AstArena?` parameter.

### Step 4: Wire Into `ExprUndefinedVarRule`

1. In `VisitWorkflowPre`: Create `LocalReusableWorkflowOutputResolver` alongside `_localActionOutputResolver`. Store as field + func delegate.

2. Pass the resolver func to `BuildNeedsOverrideInto` call in `VisitJobPre`.

### Step 5: Run Targeted Tests (Green)

```shell
dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/RuleInterfaceTests/RuleRegression_ExprUndefinedVarRule_ReusableWorkflowCallNeedsOutputs*
```

Verify:
- New ng test now detects the unknown output
- New ok tests produce zero diagnostics
- Existing reusable workflow tests still pass

### Step 6: Run Full Test Suite

```shell
dotnet test
```

All 1594+ tests must pass.

### Step 7: Run Benchmarks

```shell
cd src/Seiton.Benchmark
dotnet run -c Release -- --filter "*CoreLintBenchmark*"
dotnet run -c Release -- --filter "*CoreParsingBenchmark*"
```

Compare against baseline in §5. Acceptance: +1% max on Mean and Allocated for all scenarios.

### Step 8: Update Specs

Update the following documents:

1. **`Seiton_Linter_spec.md`** §4.4 `expr-undefined-var` entry: Replace "For reusable workflow call jobs (`uses:` at job level), `needs.<job>.outputs.*` is treated as loose (non-strict)" with distinction between local (strict) and remote (loose).

2. **`Seiton_Linter_csharp_spec.md`** §3.4 `expr-undefined-var` entry: Same update plus mention `LocalReusableWorkflowOutputResolver`.

3. **`Seiton_Parser_csharp_spec.md`** §0.1.2 table: No change needed (this is a linter-side feature).

## 7. Risk Assessment

| Risk | Mitigation |
|---|---|
| File I/O in linter hot path | Cached per resolved path; only triggered for local `./` references; same pattern as `LocalActionOutputResolver` which is already in production |
| False positive: workflow has dynamic outputs not declared in `on.workflow_call.outputs` | GitHub Actions requires `on.workflow_call.outputs` to declare all outputs that callers can consume. Undeclared outputs are not available to callers. Strict validation is correct. |
| False positive: file not found (checkout depth, working directory) | Resolver returns `null` → falls back to loose (no diagnostic). Same safety net as `LocalActionOutputResolver` |
| Benchmark regression | Benchmark fixtures have zero reusable workflow calls → new code path is not exercised → zero impact. Verified by running benchmarks pre/post. |

## 8. Design Decision: Strict Empty vs Loose for No-Outputs Workflows

When a local reusable workflow is successfully parsed and has `on.workflow_call` but no `outputs:` key:

- **Option A (strict empty)**: `needs.<job>.outputs.anything` → error. Correct per GitHub Actions semantics — undeclared outputs are not available.
- **Option B (loose)**: `needs.<job>.outputs.anything` → no error. Permissive, avoids false positives if `on.workflow_call.outputs` is omitted but job-level `outputs:` are used in the called workflow.

**Decision: Option A (strict empty)**. Rationale: GitHub Actions documentation states that reusable workflow outputs must be declared in `on.workflow_call.outputs` to be available to callers. A called workflow that sets `GITHUB_OUTPUT` but doesn't declare it in `on.workflow_call.outputs` has a bug — flagging the caller's reference is correct and useful.

## 9. Files to Create/Modify

| File | Action |
|---|---|
| `src/Seiton.Core/Linting/LocalReusableWorkflowOutputResolver.cs` | **Create** — new resolver class |
| `src/Seiton.Core/Parsing/DynamicContextTypeBuilder.cs` | **Modify** — add resolver param to `BuildJobOutputsType`, `FindJobOutputsType`, `BuildNeedsOverride`, `BuildNeedsOverrideInto` |
| `src/Seiton.Core/Linting/Rules/ExprUndefinedVarRule.cs` | **Modify** — create resolver in `VisitWorkflowPre`, pass to `BuildNeedsOverrideInto` |
| `tests/Seiton.Core.Tests/RuleInterfaceTests.cs` | **Modify** — add new test cases |
| `tests/Seiton.Core.Tests/fixtures/` | **Create** — local reusable workflow fixture files for tests |
| `.github/docs/Seiton_Linter_spec.md` | **Modify** — update `expr-undefined-var` description |
| `.github/docs/Seiton_Linter_csharp_spec.md` | **Modify** — update `expr-undefined-var` description |
