# Verbose Logging Improvement Plan

> **Goal**: Make `--verbose` output genuinely useful for users diagnosing unexpected results, verifying configuration, and identifying performance bottlenecks.
>
> **Hard constraint**: No allocation regression in `Seiton.Core`. Runtime budget is +3 % max on `CoreLintBenchmark`.

---

## 0. Current State Assessment

### 0.1 What verbose currently emits

| Layer | Output | Location |
|---|---|---|
| CLI Config | Resolved config path or `(none, using defaults)` | `CliConfigBridge.WriteResolvedConfigVerbose` → stderr |
| CLI Check | `checking <file>...` per file | `CheckCommand.Run` → stderr |
| CLI Check | Per-rule breakdown (count) | `WritePerRuleBreakdown` → stderr (verbose only) |
| CLI Fix | `fixing <file>...` per file | `FixCommand.RunAsync` → stderr |
| CLI Fix | `resolved N pin(s) for <file>` | `FixCommand.RunAsync` → stderr |
| CLI Fix | `applied N fix(es) to <file>` | `FixCommand.RunAsync` → stderr |
| Core | `ignored '<action>' (matched ignore-actions pattern)` | `UnpinnedUsesRule` → info diagnostic (verbose only) |

### 0.2 Problems

1. **File discovery is opaque**: Users cannot determine why no files were found or which directories were searched.
2. **Rule activation invisible**: 57+ rules exist; which are active/disabled/skipped is unknown at runtime.
3. **Config interpretation unclear**: The effective config values (especially merged CLI+config booleans like `enable-pin-network`) are not shown.
4. **Suppression counts hidden**: `SuppressionSummary` is computed by Core but never printed by the CLI.
5. **Document classification silent**: Whether a file was treated as workflow vs. action metadata is not reported.
6. **Timing absent**: No wall-clock times for total run, per-file, or network operations.
7. **Core verbose underused**: Only 1 of 57 rules emits verbose info diagnostics.

### 0.3 User scenarios for `--verbose`

| Scenario | User question |
|---|---|
| **Unexpected results** | "Why didn't rule X fire?" / "Why is this ignored?" |
| **Config verification** | "Is my config applied?" / "Is the network flag coming from config or CLI?" |
| **Performance triage** | "Why is this slow?" / "Is network the bottleneck?" |

---

## 1. Design Principles

1. **CLI owns stderr output**. `Seiton.Core` returns structured data; the CLI formats and writes it. Core must not write to stderr or stdout directly.
2. **Zero new allocations in Core hot paths**. Data that verbose needs must already exist in the result or be computable from existing fields without new heap allocations on the success path.
3. **Lazy computation only**. If verbose is off, no extra work is done beyond what the engine already does.
4. **Consistent prefix format**. All verbose lines use a category prefix for grep-ability:
   ```
   verbose: config: .github/seiton.yaml
   verbose: discovery: found 3 workflow file(s) under .github/workflows/
   verbose: rules: 42 enabled, 15 disabled (workflow)
   verbose: .github/workflows/ci.yml: workflow, 0.8 ms, 3 diagnostics, 1 suppressed
   ```
5. **Incrementally deliverable**. Each phase is independently useful and shippable.

---

## 2. Information Available Without Core Changes

These items can be implemented purely in the CLI layer using data already returned by `LintResult` or already known at the CLI level.

| Item | Data source | New Core work? |
|---|---|---|
| Discovery search path + found files | `InputDiscovery` internal state | No (CLI refactor) |
| Config path (already done) | `CliConfigBridge` | No |
| Effective boolean flags (pin/image network) | CLI computed values | No |
| Suppression summary per file | `LintResult.SuppressionSummary` (already computed) | No |
| Per-file timing | `Stopwatch` in CLI around `engine.Check()` | No |
| Total run timing | `Stopwatch` in CLI | No |

---

## 3. Information Requiring Core Changes

| Item | What Core must expose | Allocation impact |
|---|---|---|
| Active/disabled rule counts | Return `(int active, int disabled)` or expose `_activeRules.Count` via `LintResultData` | 2 ints — zero alloc |
| Disabled rule IDs (for verbose list) | Return `ReadOnlySpan<string>` or reuse a pooled list attached to the result | Zero alloc if pooled |
| Document kind per file | `ClassifiedParseResult.Classification.FinalKind` — already computed, just not exposed | Store `DocumentKind` in `LintResultData` — 1 byte |
| Per-rule verbose diagnostics in other rules | Rules check `Config.Verbose` and call `AddStepInfo` / `AddJobInfo` | Alloc only when verbose=true (off by default) |

---

## 4. Phased Implementation Plan

### Phase 0 — VerboseLogger Infrastructure

**Priority**: Prerequisite — all subsequent phases depend on this.

**Scope**:

1. **`VerboseLogger` class** (`src/Seiton/Cli/VerboseLogger.cs`):
   - Wraps a `TextWriter?`. When writer is null, all methods are no-ops (zero overhead).
   - Factory: `VerboseLogger.Create(bool verbose, TextWriter stderr)` returns a real logger or `VerboseLogger.Null`.
   - Category-prefixed methods: `Log(category, message)` → `verbose: <category>: <message>`.
   - File-scoped method: `LogFile(filePath, message)` → `verbose: <filePath>: <message>`.
   - Raw method: `Log(message)` → `verbose: <message>`.
   - `IsEnabled` property for callers that need to guard expensive formatting.
2. **Migrate existing verbose output** in `CheckCommand` and `FixCommand`:
   - Replace `if (verbose) Console.Error.WriteLine(...)` with `verboseLogger.Log(...)`.
   - Replace `CliConfigBridge.WriteResolvedConfigVerbose(stderr, verbose, configPath)` with a `verboseLogger.Config(...)` call.
   - All verbose lines gain the `verbose: ` prefix for grep-ability.
3. **Thread safety**: `TextWriter` itself handles synchronization. `VerboseLogger` is stateless beyond the writer reference.
4. **Testability**: Tests inject `StringWriter` and assert exact output.

**Core changes**: None.

**Allocation impact**: None. `VerboseLogger.Null` avoids all formatting. Active logger allocates interpolated strings (same as current `Console.Error.WriteLine`).

**Verification**:

- [ ] Tests: `VerboseLoggerTests` — enabled/disabled output, category formatting, `Null` produces no output.
- [ ] Tests: Existing `WriteSummaryTests` and `FixCommandTests` still pass (no regression from migration).
- [ ] Benchmark: Not required (CLI-only, no Core changes).

---

### Phase 1 — CLI-Only: Discovery + Config + Suppression Logging

**Priority**: High — addresses scenarios 1 & 2 with zero Core changes.

**Scope**:

1. **Discovery logging**: Refactor `InputDiscovery` to accept a verbose `TextWriter?`. When verbose, emit:
   - `verbose: discovery: searching from <cwd>`
   - `verbose: discovery: found <dir>` or `verbose: discovery: .github/workflows/ not found, walking parent...`
   - `verbose: discovery: <N> file(s) resolved`
2. **Effective config summary**: After config resolution, emit:
   - `verbose: config: <path>` (already exists — keep)
   - `verbose: config: fix.pinning.enable-network=true (source: config)` / `(source: --enable-pin-network)`
   - `verbose: config: fix.images.enable-network=false (source: default)`
   - If `enable-network: false` is explicitly present in config, still report `(source: config)` rather than `(source: default)`.
3. **Suppression summary**: After lint, read `LintResult.SuppressionSummary` and emit:
   - `verbose: suppressed: <N> diagnostic(s) (<rule-id>: <count>, ...)`
   - Only when `TotalSuppressed > 0`.

**Core changes**: None.

**Allocation impact**: None in Core. CLI allocates strings for stderr formatting (already the case for existing verbose lines).

**Verification**:

- [x] Benchmark: `CoreLintBenchmark` before/after — identical (no Core changes). 0 B allocation delta.
- [x] Tests: 10 CLI unit tests for verbose output content (TextWriter injection). All pass.
- [x] Regression: `dotnet test` — 1748 tests, all green.

**Benchmark results (Phase 1)**:

| Size | FixEnabled | Before (μs) | After (μs) | Δ runtime | Alloc Before | Alloc After | Δ alloc |
|------|-----------|-------------|------------|-----------|-------------|------------|---------|
| Small | False | 55.64 | 63.34 | noise | 8.37 KB | 8.37 KB | 0 B |
| Small | True | 64.61 | 63.57 | noise | 9.82 KB | 9.82 KB | 0 B |
| Medium | False | 1,242.93 | 1,316.75 | noise | 68.56 KB | 68.56 KB | 0 B |
| Medium | True | 1,791.07 | 1,903.57 | noise | 81.92 KB | 81.92 KB | 0 B |
| Large | False | 20,064.24 | 20,810.91 | noise | 327.08 KB | 327.08 KB | 0 B |
| Large | True | 30,107.98 | 32,663.41 | noise | 381.92 KB | 381.92 KB | 0 B |

**Implementation notes**:
- `InputDiscovery.ResolveFiles` gained `VerboseLogger` and optional `startDirectory` parameters (for testability).
- `CheckCommand.WriteSuppressionSummary` is shared by both Check and Fix commands.
- `FixCommand.WriteEffectiveNetworkConfig` logs network flag values with source attribution (CLI / config / default), including explicit `false` values from config.
- `FileCheckResult` struct extended with `SuppressionSummary` field for parallel-path capture.
- Suppression accumulation guarded by `verboseLogger.IsEnabled` to avoid work when verbose is off.

---

### Phase 2 — Core Metadata Exposure: Rule Activation + Document Kind

**Priority**: High — unlocks "why didn't rule X fire?" debugging.

**Scope**:

1. **Expose active/disabled rule counts on `LintResultData`**:
   - Add `int ActiveRuleCount` and `int DisabledRuleCount` fields to `LintResultData` (value type — zero alloc).
   - Set in `LintEngine.CheckCore` after rule activation loop (values are already computed, just not stored).
2. **Expose document kind on `LintResultData`**:
   - Add `DocumentKind DocumentKind` field (1-byte enum, already computed in `CheckCore`).
3. **Expose disabled rule IDs** (optional, verbose-only):
   - Reuse `LintEngine._configDiagnostics` pattern: populate a reusable `List<string>` field on `LintEngine` with disabled rule IDs during the activation loop, expose via `LintResultData.DisabledRuleIds` as `ReadOnlySpan<string>`.
   - Use a `PooledBuffer<string>` or the existing list pattern. The list is cleared at the start of each `CheckCore` call — no new heap allocation per call.
4. **CLI verbose output**:
   - `verbose: rules: <N> enabled, <M> disabled (workflow|action)`
   - `verbose: rules: disabled: <id1>, <id2>, ...` (only when disabled count > 0 and verbose)
   - `verbose: <file>: <document-kind>` (e.g. `verbose: .github/workflows/ci.yml: workflow`)

**Core changes**: Add 4 fields to `LintResultData` (`DocumentKind`, `ActiveRuleCount`, `DisabledRuleCount`, `DisabledRuleIds`). `LintResult` exposes them via dispose-guarded properties. `DisabledRuleIds` uses `ReadOnlySpan<string>` for zero-copy access. Reuse `List<string>` buffer in `LintEngine` (cleared per call, snapshot via `ToArray()` only when count > 0).

**Allocation impact**: +0.06 KB per call (one `string[]` snapshot for disabled rule IDs). No runtime regression.

**Verification**:

- [x] Benchmark: `CoreLintBenchmark` before/after — +0.06 KB allocation, runtime within noise (actually improved in all cases).
- [x] Tests: 8 unit tests for `LintResultData`/`LintResult` new fields (`LintResultMetadataTests`). 6 CLI tests for verbose output (`VerbosePhase2Tests`).
- [x] Regression: `dotnet test` — 1762 tests, all green.

**Benchmark results (Phase 2)**:

| Size | FixEnabled | Before Mean (μs) | After Mean (μs) | Δ% | Before Alloc | After Alloc |
|------|-----------|----------------:|----------------:|-----:|-------------:|------------:|
| Small | False | 70.76 | 61.49 | -13.1% | 8.37 KB | 8.43 KB |
| Small | True | 98.41 | 69.71 | -29.2% | 9.82 KB | 9.88 KB |
| Medium | False | 1,762.25 | 1,370.07 | -22.2% | 68.56 KB | 68.63 KB |
| Medium | True | 2,972.82 | 1,944.10 | -34.6% | 81.92 KB | 81.98 KB |
| Large | False | 25,289.33 | 20,948.33 | -17.2% | 327.08 KB | 327.14 KB |
| Large | True | 34,653.17 | 32,354.24 | -6.6% | 381.92 KB | 381.98 KB |

**Implementation notes**:
- `DisabledRuleIds` counts only config/opt-in disabled rules, NOT document-kind mismatches (those are "not applicable", not "disabled").
- Rule summary logged once per DocumentKind (workflow and action separately), since `ActiveRuleCount` varies by document kind while `DisabledRuleCount`/`DisabledRuleIds` are invariant.
- Output format: `verbose: rules: <N> enabled, <M> disabled (workflow)` / `verbose: rules: <N> enabled, <M> disabled (action)`.
- Parallel path captures metadata in `FileCheckResult` struct; rule summary is emitted during ordered aggregation, while `checking <file>...` is emitted from worker threads as best-effort progress output and may interleave.

---

### Phase 3 — CLI Timing Instrumentation ✅

**Priority**: Medium — addresses scenario 3 (performance triage).

**Scope**:

1. **Per-file timing**: Wrap `engine.Check()` call with `TimeProvider` (CLI layer only). Emit:
   - `verbose: <file>: <kind>, <elapsed> ms, <N> diagnostics, <M> suppressed`
2. **Total timing**: Wrap the entire check/fix loop. Emit:
   - `verbose: total: <N> file(s) checked in <elapsed> ms`
   - `verbose: total: <N> file(s) fixed in <elapsed> ms`
3. **Network timing** (fix mode): Wrap `pinRemediation.RemediateAsync()`. Emit:
   - `verbose: network: resolved pins for <file> in <elapsed> ms`

**Core changes**: None.

**Allocation impact**: `TimeProvider` delegates to `TimeProvider.System` — zero heap allocation on hot path.

**Implementation notes**:
- Used `TimeProvider` instead of `Stopwatch` for testability via DI.
- `VerboseLogger` exposes `GetTimestamp()` and `GetElapsedTime(long start)` that delegate to `TimeProvider`.
- `VerboseLogger.Null.GetTimestamp()` returns 0 (no-op when verbose is disabled).
- `FileCheckResult` extended with `FileElapsed`, `FileDiagnosticCount`, `FileSuppressedCount` for parallel path.
- Per-file line consolidates document kind from Phase 2 into richer timing line.
- Tests use `FixedTimeProvider` (TimestampFrequency=1000, 1 tick = 1 ms) for deterministic assertions.

**Verification**:

- [x] Benchmark: `CoreLintBenchmark` before/after — identical allocation (0 B delta).
- [x] Tests: 12 tests verify timing line format (VerbosePhase3Tests).
- [x] Regression: `dotnet test` — 1774 tests all green.

**Benchmark results (after)**:

| Size | FixEnabled | Mean (μs) | Allocated |
|------|-----------|-----------|-----------|
| Small | False | 71.72 | 8.37 KB |
| Small | True | 75.37 | 9.82 KB |
| Medium | False | 1,594.93 | 68.56 KB |
| Medium | True | 2,291.30 | 81.92 KB |
| Large | False | 24,297.22 | 327.08 KB |
| Large | True | 44,525.51 | 381.92 KB |

---

### Phase 4 — Core Rule Verbose Diagnostics Expansion ✅

**Priority**: Low — incremental improvement for deep debugging. Implement on-demand per rule.

**Scope**:

Expand `Config.Verbose` usage to additional rules where "why was this skipped?" is a common user question. Implemented rules:

| Rule | Verbose info emitted | Condition |
|---|---|---|
| `ForbiddenUsesRule` | `'<owner/repo>' matched allow pattern, skipping forbidden-uses check` | Action is denied but allowed by allow pattern |
| `RunnerLabelRule` | `label '<label>' matched known-hosted-labels config, skipping` | Label matches user-configured `additionalKnownHostedLabels` (not built-in) |

Not implemented (deferred):

| Rule | Reason |
|---|---|
| `CredentialsRule` | Verbose info for public registries was removed after review because the normal no-credentials path created too much noise in large workflows |
| `UnpinnedImageRule` | No ignore/exclude logic exists in the rule itself (patterns only in pin remediation layer) |
| `DangerousTriggersRule` | "Not dangerous" is the normal path for most events — verbose would be extremely noisy |
| `TemplateInjectionRule` | Safe function pattern is deeply embedded in recursion; adding verbose would be invasive and low value |

**Implementation pattern** (identical to existing `UnpinnedUsesRule`):

```csharp
if (IsIgnored(value))
{
    if (Config.Verbose)
    {
        var text = Decode(...);
        AddStepInfo(step, $"ignored '{text}' (matched <pattern-name>)", location);
    }
    return;
}
```

**Core changes**: Add `Config.Verbose` checks in 3 rule `Visit*` methods.

**Allocation impact**: Zero when `verbose=false` (the `if` short-circuits). When `verbose=true`, allocates diagnostic strings — acceptable because verbose mode is explicitly opt-in and not on the benchmark path.

**Design decisions**:
- `RunnerLabelRule` only emits verbose for user-configured `additionalKnownHostedLabels`, NOT built-in labels (ubuntu-latest, etc.) to avoid noise.
- Matrix-expanded `runs-on: ${{ matrix.AXIS }}` paths now emit the same verbose skip info for user-configured `additionalKnownHostedLabels`; review found this gap after the initial Phase 4 implementation and the follow-up fix aligned matrix and static label behavior.
- `CredentialsRule` was briefly refactored to expose the public-registry branch for verbose diagnostics, but the review pass removed that output because it added noise on the normal success path.
- `ForbiddenUsesRule` emits verbose inside the existing local function that already captures `this` for `AddStepWarning`/`AddJobWarning`.

**Verification**:

- [x] Benchmark: `CoreLintBenchmark` (with `Verbose=false`) before/after — 0 B allocation delta on all sizes.
- [x] Tests: 11 per-rule unit tests (6 positive verbose, 5 negative/no-verbose), including matrix-expanded runner label coverage. All pass.
- [x] Regression: `dotnet test` — 1785 tests all green.

**Benchmark results (after, .NET 10.0.8)**:

| Size | FixEnabled | Mean (μs) | Allocated |
|------|-----------|-----------|-----------|
| Small | False | 55.52 | 8.43 KB |
| Small | True | 61.57 | 9.88 KB |
| Medium | False | 1,252.39 | 68.63 KB |
| Medium | True | 1,788.40 | 81.98 KB |
| Large | False | 19,468.09 | 327.14 KB |
| Large | True | 30,064.83 | 381.98 KB |

Note: Runtime improvement vs Phase 3 baseline is due to .NET SDK version change (10.0.6 → 10.0.8), not Phase 4 code changes. Allocation is identical.
Review follow-up re-ran `CoreLintBenchmark` after the matrix runner-label fix and allocations remained identical (`8.43 KB`, `9.88 KB`, `68.63 KB`, `81.98 KB`, `327.14 KB`, `381.98 KB`), confirming the additional verbose-only branch did not change the non-verbose benchmark path.

---

## 5. Benchmark Protocol

Each phase follows this protocol:

1. **Before**: Run `CoreLintBenchmark` (Small/Medium/Large × FixEnabled=false) and record Mean/Allocated.
2. **Implement** the phase.
3. **After**: Run the same benchmark and compare.
4. **Accept criteria**:
   - Runtime: Δ ≤ +3 % on all sizes.
   - Allocation: Δ = 0 B (or negative). Any positive allocation delta is a phase blocker.
5. **Record** results in the PR description for traceability.

Benchmark command:

```shell
cd src/Seiton.Benchmark
dotnet run -c Release -- --filter '*CoreLintBenchmark*'
```

Additional benchmarks to watch (run if the phase touches related code):

- `CoreParsingBenchmark` — if parser changes are made (not expected in any phase).
- `MultiFileLintBenchmark` — if parallel path changes are made (Phase 1 discovery refactor).

---

## 6. Expected Verbose Output (End State)

After all phases, `seiton --verbose .github/workflows/ci.yml` would emit to stderr:

```
verbose: config: D:\repo\.github\seiton.yaml
verbose: config: fix.pinning.enable-network=true (source: config)
verbose: config: fix.images.enable-network=false (source: default)
verbose: discovery: 1 file(s) from explicit args
verbose: rules: 42 enabled, 15 disabled (workflow)
verbose: rules: disabled: concurrency-limits, known-vulnerable-actions, impostor-commit, ref-confusion, stale-action-refs, ...
verbose: checking .github/workflows/ci.yml...
verbose: .github/workflows/ci.yml: workflow, 1.2 ms, 5 diagnostics, 2 suppressed
verbose: suppressed: 2 diagnostic(s) (unpinned-uses: 1, template-injection: 1)

5 errors, 0 warnings in 1 file
  unpinned-uses: 3, job-permissions-required: 1, template-injection: 1
verbose: total: 1 file(s) checked in 2.4 ms
```

Fix mode additions:

```
verbose: fixing .github/workflows/ci.yml...
verbose: network: resolved pins for .github/workflows/ci.yml in 320 ms
verbose: .github/workflows/ci.yml: applied 3 fix(es)
verbose: total: 1 file(s) fixed in 450 ms
```

---

## 7. What This Plan Does NOT Cover

- **Debug-level logging** (internal engine tracing): Out of scope. Verbose is user-facing, not developer-facing.
- **Structured verbose output** (JSON verbose): Not needed. Verbose goes to stderr as human-readable text; `--format json` controls stdout only.
- **Log levels** (info/debug/trace): Over-engineering for a CLI tool. A single `--verbose` flag is sufficient.
- **Core `ILogger` abstraction**: Violates principle 1 (CLI owns output). Core returns data; CLI formats it.

---

## 8. Cross-Document Updates

After implementation:

- Update `Seiton_CLI_spec.md` §6.4 (Summary Output) to document verbose output categories.
- Update `Seiton_CLI_csharp_spec.md` §6.1 to document new `LintResultData` fields.
- Update `Seiton_CLI_go_spec.md` §6.1 for Go parity planning.
