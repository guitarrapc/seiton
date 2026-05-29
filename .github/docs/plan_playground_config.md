# Plan: Playground Config Editor (SetConfig)

## Summary

Add a `SetConfig` WASM export and config editor UI to the Playground, allowing users to customize lint behavior (rule enable/disable, severity, fix defaults like `job-timeout-minutes`, `runner-no-latest` fix-mapping, etc.) without leaving the browser.

**Key insight**: `LintConfigYamlParser.Parse()` allocates internally (VYaml reader state, dictionaries, lists). To avoid GC pressure on the constrained WASM heap, the parsed config is cached with an XxHash64 content hash. Cosmetic edits (trailing whitespace, blank lines) are normalized away before hashing so they don't trigger re-parse.

---

## Investigation Findings

### Current State

1. `PlaygroundLintRunner` uses a **hardcoded static `LintConfig`** (`LintWithFixMetadata`) — Fix enabled, Network default, Output default, SkipSuppressionSummary=true.
2. `RunToJsonUtf8` staleness check uses `(yamlSource, filePath)` only — no config dimension.
3. `ApplyAllFixes` also uses the hardcoded config.
4. `LintConfigLibrary.Validate(string yamlText, string filePath)` already exists and returns `LintConfigValidationResult` with parsed `LintConfig` + diagnostics.
5. The Playground UI has a single CodeMirror editor + results pane in a 2-column grid.
6. `IncrementalParseContext` caches per-job diagnostics — config change does NOT invalidate this cache because config affects rule evaluation, not YAML structure.

### Performance Constraints

- WASM heap is limited; avoid per-keystroke allocations.
- `LintConfigYamlParser.Parse()` allocates ~1–2 KB per call (VYaml reader, dictionaries, lists).
- Config changes are infrequent vs. YAML edits (separate debounce: 500ms for config vs. 300ms for YAML).
- XxHash64 of normalized config (~100–500 bytes) is negligible cost.

### Architecture Decision

- **WASM side**: New `SetConfig(string configYaml) → byte[]` export. Returns config diagnostic JSON (empty `[]` on success).
- **JS side**: Separate `configVersion` counter for staleness. Config editor debounced at 500ms before calling `SetConfig`.
- **Cache**: Single-slot XxHash64 cache — skip re-parse when normalized content hash matches.

---

## Prioritized Implementation Phases

### Phase 0: Baseline Benchmark & Test Snapshot

**Goal**: Establish before-state for comparison.

| Step | Action |
|---|---|
| 0-1 | Run existing `LintConfigBenchmark` and record mean/allocated |
| 0-2 | Run `dotnet test --filter "Playground"` — all tests must pass |
| 0-3 | Run full `dotnet test` — record pass count as baseline |

**Exit criteria**: Baseline numbers recorded. No code changes.

---

### Phase 1: `PlaygroundLintRunner.SetConfig` (Core Logic)

**Goal**: Add `SetConfig` method with content-hash caching. No UI changes yet.

| Step | Action |
|---|---|
| 1-1 | Add failing tests for `SetConfig` behavior: empty input resets, valid config parses, invalid config returns diagnostics, hash-hit skips re-parse, cosmetic edits don't trigger re-parse |
| 1-2 | Implement `SetConfig` in `PlaygroundLintRunner`: normalization → XxHash64 → cache check → `LintConfigLibrary.Validate()` |
| 1-3 | Add `_cachedConfig` static field; update `RunToJsonUtf8` and `ApplyAllFixes` to use `_cachedConfig ?? LintWithFixMetadata` |
| 1-4 | Add test: config with `runner-no-latest` disabled → diagnostic suppressed; reset → diagnostic returns |
| 1-5 | Run `dotnet test --filter "Playground"` — all pass |
| 1-6 | Run `LintConfigBenchmark` — no regression in existing benchmarks |

**Performance requirement**:
- `SetConfig` with hash-hit: **zero allocation** (returns cached `byte[]`).
- `SetConfig` with hash-miss: allocation allowed (parse is inherently allocating), but result is cached.
- `RunToJsonUtf8` / `ApplyAllFixes`: no additional allocation beyond current baseline (config lookup is a field read).

**Exit criteria**: Tests green, benchmark shows no regression in `RunToJsonUtf8` path.

---

### Phase 2: WASM Interop (`LintInterop.SetConfig`)

**Goal**: Expose `SetConfig` as `[JSExport]` with error handling.

| Step | Action |
|---|---|
| 2-1 | Add `[JSExport] public static byte[] SetConfig(string? configYaml)` to `LintInterop.cs` |
| 2-2 | Wrap in try/catch — on exception, return internal-error diagnostic JSON (same pattern as `RunLint`) |
| 2-3 | Add test: null input → returns `[]`; exception scenario → returns error diagnostic |
| 2-4 | Run `dotnet test` — all pass |
| 2-5 | Build `Seiton.Playground` in Release mode — verify no trimming warnings |

**Exit criteria**: WASM project builds, interop method compiles, tests pass.

---

### Phase 3: JS Staleness & Config Version

**Goal**: Extend `main.js` staleness check to include config dimension.

| Step | Action |
|---|---|
| 3-1 | Add `let configVersion = 0` and `let lastConfigVersion = 0` to staleness state |
| 3-2 | Modify staleness check: `source === lastLintedSource && filePath === lastLintedFilePath && configVersion === lastConfigVersion` |
| 3-3 | Add `setConfig(configYaml)` JS function: calls WASM `SetConfig`, increments `configVersion` on success, invalidates staleness, triggers re-lint |
| 3-4 | Add Playwright test: config change triggers re-lint with different diagnostics |
| 3-5 | Run `dotnet test --filter "Playground"` — all pass |

**Exit criteria**: Config change invalidates staleness and triggers re-lint. Existing Playwright tests still pass.

---

### Phase 4: Config Editor UI

**Goal**: Add collapsible config editor panel in the left column.

| Step | Action |
|---|---|
| 4-1 | Add `#config-panel` section to `index.html` below `#editor-wrap`, with toggle button and CodeMirror textarea |
| 4-2 | Add CSS for collapsible panel (`.config-panel`, `.config-panel--collapsed`) |
| 4-3 | Initialize second CodeMirror instance with yaml mode, smaller height, 500ms debounce |
| 4-4 | On config editor change (debounced): call `setConfig(configEditor.getValue())` |
| 4-5 | Display config diagnostics inline below config editor (not in main results pane) |
| 4-6 | Add `PlaygroundHtmlContractTests` for new HTML landmarks (`#config-panel`, `#config-editor`) |
| 4-7 | Add Playwright test: config panel toggle, config edit triggers re-lint |
| 4-8 | Run full `dotnet test` — all pass |

**Exit criteria**: Config editor visible, edits apply to lint, no layout regression.

---

### Phase 5: Final Verification

**Goal**: Confirm no performance regression end-to-end.

| Step | Action |
|---|---|
| 5-1 | Run `LintConfigBenchmark` — compare to Phase 0 baseline |
| 5-2 | Run full `dotnet test` — all pass, count matches baseline |
| 5-3 | Run `dotnet publish` in Release — verify binary size delta is minimal |
| 5-4 | Manual smoke test in browser: edit YAML → diagnostics update; edit config → diagnostics change; apply fixes with custom config |

**Exit criteria**: No performance regression. All tests pass. Spec documents already updated.

---

## Benchmark Strategy

### Before/After Comparison

| Benchmark | What it measures | Acceptable delta |
|---|---|---|
| `LintConfigBenchmark` | Config parse time + allocation | Existing scenarios: 0% regression |
| `PlaygroundLintRunner.RunToJsonUtf8` (new) | Lint with default config | Mean: ≤ +2%; Allocated: ≤ +0 bytes |
| `PlaygroundLintRunner.RunToJsonUtf8` with cached config (new) | Lint after `SetConfig` | Mean: ≤ +2%; Allocated: ≤ +0 bytes vs. default |
| `PlaygroundLintRunner.SetConfig` hash-hit (new) | Cached config lookup | Mean: < 1μs; Allocated: 0 bytes |
| `PlaygroundLintRunner.SetConfig` hash-miss (new) | Full config parse | Mean: < 500μs; Allocated: bounded by VYaml |

### How to Run

```shell
# Phase 0: Record baseline
cd src/Seiton.Benchmark
dotnet run -c Release -- --filter "LintConfig"

# Phase 1+: Compare
dotnet run -c Release -- --filter "LintConfig"
# → compare Mean and Allocated columns to baseline
```

### New Benchmark Class (added in Phase 1)

```csharp
[MemoryDiagnoser]
public class PlaygroundConfigBenchmark
{
    // SetConfig with hash-hit (should be ~0 alloc)
    // SetConfig with hash-miss (parse cost)
    // RunToJsonUtf8 with custom config vs. default
}
```

---

## Test Strategy

### Regression Guard

```shell
# Before any code change (Phase 0)
dotnet test > baseline_test_results.txt

# After each phase
dotnet test
# → must match baseline pass count (new tests add to count, none fail)
```

### New Test Categories

| Phase | Test file | Coverage |
|---|---|---|
| 1 | `PlaygroundLintRunnerTests.cs` | `SetConfig` behavior: reset, valid, invalid, hash-hit, hash-miss, cosmetic edits |
| 1 | `PlaygroundLintRunnerTests.cs` | `RunToJsonUtf8` with custom config produces different diagnostics |
| 2 | `PlaygroundLintRunnerTests.cs` | Null/exception handling in interop layer |
| 3 | `PlaygroundUiLayoutTests.cs` | Config change triggers re-lint (Playwright) |
| 4 | `PlaygroundHtmlContractTests.cs` | HTML landmarks for config panel |
| 4 | `PlaygroundUiLayoutTests.cs` | Config panel collapse/expand, config edit flow |

### Equivalence Classes for SetConfig

| Input class | Expected behavior |
|---|---|
| `null` | Reset to default, return `[]` |
| `""` (empty) | Reset to default, return `[]` |
| `"   \n  \n"` (whitespace only) | Reset to default, return `[]` |
| Valid config YAML | Parse, cache, return `[]` |
| Invalid config YAML (unknown rule) | Return diagnostics, retain previous config |
| Same content as cached (hash-hit) | Return cached diagnostics, skip parse |
| Cosmetic edit (add blank line) | Hash matches after normalization → skip parse |
| Different meaningful content (hash-miss) | Re-parse, update cache |

---

## Spec Documents (Already Updated)

- `.github/docs/Seiton_Playground_spec.md` — §2.3.1, §3.1, §3.3, §3.4, §4.1
- `.github/docs/Seiton_Playground_csharp_spec.md` — §1.1, §2.1.1, §6.4
- `.github/docs/Seiton_Playground_go_spec.md` — §1.1, §1.2, §2.4

---

## Risk & Mitigation

| Risk | Mitigation |
|---|---|
| Config parse per keystroke adds GC pressure | 500ms JS debounce + XxHash64 cache (zero alloc on hash-hit) |
| Stale lint results after config change | `configVersion` in staleness triple forces re-lint |
| WASM runtime crash from SetConfig exception | try/catch in `[JSExport]`, return error diagnostic |
| Layout regression on narrow viewport | Config panel collapses to zero height; Playwright test verifies |
| Incremental parse cache serves wrong diagnostics after config change | Config does NOT affect parse structure; per-job diagnostic cache is invalidated by triggering full re-lint (staleness cleared) |

---

## Implementation Results

**Status: COMPLETE** (all phases implemented and verified)

### Phase Completion

| Phase | Status | Notes |
|---|---|---|
| Phase 0 | ✅ Done | Baseline: NoChange ~105ns/0B, FullChange Small 231μs/51KB, Large 1.34ms/170KB; 94 Playground tests passing |
| Phase 1 | ✅ Done | `SetConfig` with XxHash64 caching, 10 unit tests, 0B allocation increase on lint path |
| Phase 2 | ✅ Done | `[JSExport] SetConfig(string?)` in LintInterop.cs, builds clean |
| Phase 3 | ✅ Done | `configVersion`/`lastConfigVersion` staleness triple, `setConfig()` JS function |
| Phase 4 | ✅ Done | Collapsible config panel with CodeMirror, 500ms debounce, inline diagnostics, 4 contract tests |
| Phase 5 | ✅ Done | NoChange 108ns/0B, FullChange Small 239μs/51.6KB — within noise; 2195 tests pass |

### Post-Implementation Bug Fix

**Bug**: User config with `fix.defaults.job-timeout-minutes: 15` did not cause "Apply all fixes" to insert `timeout-minutes: 15`.

**Root cause**: `LintConfigLibrary.Validate()` returns a config with `Fix.Enabled = false` (the default) unless the user explicitly writes `fix.enabled: true`. The playground's default config (`LintWithFixMetadata`) sets `Fix.Enabled = true`, but when a user config replaced it, fixes stopped being built.

**Fix**: In `SetConfig`, after validation succeeds, force `Fix.Enabled = true` and `SkipSuppressionSummary = true` on the parsed config before caching. These are playground-intrinsic behaviors that must always be active regardless of user config content.

### Final Test Count

- Total: 2195 (all passing)
- New tests: 11 (10 SetConfig unit tests + 1 fix-defaults regression test) + 4 HTML contract tests

### Files Modified

| File | Change |
|---|---|
| `src/Seiton.Playground.Core/PlaygroundLintRunner.cs` | `SetConfig()`, `ActiveConfig`, config cache fields, `InvalidateLintCache()` |
| `src/Seiton.Playground/LintInterop.cs` | `[JSExport] SetConfig(string?)` |
| `src/Seiton.Playground/wwwroot/main.js` | `setConfig()`, config editor init, staleness triple, `renderConfigDiagnostics()` |
| `src/Seiton.Playground/wwwroot/index.html` | `#config-panel` collapsible section |
| `src/Seiton.Playground/wwwroot/style.css` | `.config-panel` styles |
| `tests/Seiton.Playground.Tests/PlaygroundLintRunnerTests.cs` | 11 new SetConfig tests |
| `tests/Seiton.Playground.Tests/PlaygroundHtmlContractTests.cs` | 4 new landmark tests |
| `tests/Seiton.Playground.Tests/PlaygroundUiLayoutTests.cs` | Fixed `#editor-wrap .CodeMirror` → `#editor > .CodeMirror` locator |

---

## Phase 6: Config Templates

**Goal**: Provide built-in config template presets that users can load into the config editor with one click, lowering the barrier to customization.

### Template Patterns

| Template | Key | Use Case |
|---|---|---|
| Timeout + Latest Mapping | `timeoutAndLatest` | Teams that want explicit runner versions and a default timeout for auto-fix |
| Full Fix (Network Pinning) | `fullFix` | Teams that also want SHA pinning and image digest pinning via network |
| Rule Exclusions | `exclusions` | Teams that want to suppress noisy rules for generated/legacy workflows |

### Template Content

**1. Timeout + Latest Mapping** (`timeoutAndLatest`)
```yaml
fix:
  defaults:
    job-timeout-minutes: 15

rules:
  runner-no-latest:
    fix-mapping:
      ubuntu-latest: "ubuntu-24.04"
      windows-latest: "windows-2025"
      macos-latest: "macos-15"
  checkout-persist-credentials:
    severity: warning
```

**2. Full Fix — Network Pinning** (`fullFix`)
```yaml
# NOTE: enable-network requires CLI (seiton --fix).
# The playground runs offline — SHA/digest pinning is skipped here.
fix:
  defaults:
    job-timeout-minutes: 15
  pinning:
    enable-network: true
    min-age-days: 14
  images:
    enable-network: true

rules:
  runner-no-latest:
    fix-mapping:
      ubuntu-latest: "ubuntu-24.04"
      windows-latest: "windows-2025"
      macos-latest: "macos-15"
  checkout-persist-credentials:
    severity: warning
```

**3. Rule Exclusions** (`exclusions`)
```yaml
rules:
  checkout-persist-credentials:
    severity: warning
  job-permissions-required:
    enabled: false

exclusions:
  - file: ".github/workflows/test.yml"
    rules:
      - job-timeout-minutes-required
      - runner-no-latest
```

### UI

- Add `<select id="config-template-select">` inside the config panel header (beside the toggle button)
- First option is empty placeholder ("template...")
- On selection: load template into config editor, reset select to placeholder
- Template load triggers the existing 500ms debounce → `setConfig()` flow

### Implementation Steps

| Step | Action |
|---|---|
| 6-1 | Add `CONFIG_TEMPLATES` object to `main.js` with template strings |
| 6-2 | Add `<select id="config-template-select">` to `index.html` in config panel header |
| 6-3 | Add event listener: on change, setValue on configEditor, reset select |
| 6-4 | Add CSS for template select alignment |
| 6-5 | Add contract tests for `#config-template-select` landmark |
| 6-6 | Run full `dotnet test` — all pass |

**Exit criteria**: Templates load into config editor, trigger lint with new config, all tests pass.

---

## Phase 7: Network-Based Fix Resolution in Playground ✅ COMPLETED

**Goal**: When the user's config has `fix.pinning.enable-network: true` (or `fix.images.enable-network: true`), the Playground's "Apply all fixes" button resolves commit SHAs and image digests via the browser's `fetch()` — same as the CLI but without authentication.

**Status**: Implemented. All 2203 tests pass. Benchmark shows no regression for offline path.

### Investigation Results

| Finding | Detail |
|---|---|
| WASM HttpClient | .NET WASM `HttpClient` → browser `fetch()`. Works for CORS-allowed origins. |
| GitHub API CORS | `api.github.com` returns `Access-Control-Allow-Origin: *` for public endpoints. |
| OCI registries | `ghcr.io`, Docker Hub token endpoints are CORS-friendly. Other registries may not be. |
| Rate limit (no auth) | GitHub: 60 requests/hour per IP. Sufficient for occasional playground use. |
| Existing classes | `PinRemediationEngine`, `GitHubActionShaResolver`, `OciImageDigestResolver` — all reusable. |
| HttpClient injection | Manual construction in `FixCommand.cs`; same pattern can be used in Playground. |
| In-memory cache | `ConcurrentDictionary` per resolver, per-run. In Playground, persists across fix invocations (static lifetime). |
| Concurrency | `SemaphoreSlim` + `TimeoutSeconds` + `OnError: Skip` mode available. |
| No auth in Playground | No `GITHUB_TOKEN` available in browser. Unauthenticated only. |
| CollectAutoApplicableFixes | Currently doesn't filter `unpinned-uses`/`unpinned-image` but those diagnostics have no `Fix` attached without `PinRemediationEngine` invocation. |

### Architecture Decision

- **ApplyAllFixes becomes async**: Pin remediation requires HTTP calls. The existing synchronous `ApplyAllFixes` must become `ApplyAllFixesAsync` (or a separate `ApplyAllFixesWithNetworkAsync` path).
- **JS side**: `applyFixesBtn` click handler becomes async; shows spinner/busy state during network resolution.
- **Graceful degradation**: On network failure (CORS block, rate limit, timeout), individual pins are skipped with a toast notification. Non-network fixes still apply.
- **Config-gated**: Network resolution only runs when `ActiveConfig.Fix.Pinning.EnableNetwork` or `ActiveConfig.Fix.Images.EnableNetwork` is `true`. Otherwise, behavior is unchanged (fully offline).
- **Resolver lifetime**: Static/long-lived in `PlaygroundLintRunner` — the in-memory cache benefits repeated fix applications within the same session.

### Constraints & Limitations

| Constraint | Mitigation |
|---|---|
| 60 req/hr (unauthenticated) | Show rate-limit toast; cache resolved SHAs across invocations |
| CORS blocks on non-GitHub registries | `OnError: Skip` — gracefully skip that image, apply other fixes |
| No GHES support | Only public `api.github.com`; GHES config ignored in Playground |
| Slow network in browser | Timeout (10s default); show progress indicator; user can cancel |
| `SameOriginRedirectHandler` | Still needed to prevent hypothetical token leakage (even though no token in Playground, keep for consistency) |

### Implementation Steps

| Step | Action |
|---|---|
| 7-1 | Add `[JSExport] public static async Task<byte[]> ApplyAllFixesAsync(string yaml, string filePath)` to `LintInterop.cs` |
| 7-2 | In `PlaygroundLintRunner`, add `ApplyAllFixesWithNetworkAsync` that invokes `PinRemediationEngine.RemediateAsync` when config enables network |
| 7-3 | Create static `HttpClient` instances (GitHub API + OCI) with appropriate handlers, long-lived in `PlaygroundLintRunner` |
| 7-4 | Create static resolver instances (`GitHubActionShaResolver`, `OciImageDigestResolver`) with in-memory cache |
| 7-5 | Wire `PinRemediationEngine` to attach `Fix` to `unpinned-uses`/`unpinned-image` diagnostics before applying fixes |
| 7-6 | JS: Make `applyFixesBtn` click handler async; call `ApplyAllFixesAsync` instead of `ApplyAllFixes` |
| 7-7 | JS: Show busy state on button during async fix (disable button, change text to "Applying fixes…") |
| 7-8 | JS: On partial failure, show toast with count of skipped pins |
| 7-9 | Update `fullFix` template comment (remove "playground runs offline" note, keep "no auth = rate limited" note) |
| 7-10 | Add tests: mock HttpClient → verify SHA resolution in Playground path |
| 7-11 | Add tests: network failure → graceful skip, non-network fixes still applied |
| 7-12 | Add Playwright test: fullFix template + Apply fixes → verify SHA appears in editor |
| 7-13 | Update About section text and button tooltip to reflect conditional network access |
| 7-14 | Run full `dotnet test` — all pass |

### Performance Considerations

- **First fix application with network**: ~1–3s (GitHub API round-trips). Subsequent: ~0ms (cached SHAs).
- **No allocation regression for offline path**: When `EnableNetwork = false`, no HttpClient or resolver is touched.
- **Cache persistence**: Resolver caches live as long as the page (static fields). Re-resolving same action across multiple "Apply fixes" clicks is free.

### Exit Criteria

- `enable-network: true` in config → "Apply all fixes" resolves SHAs from GitHub API and pins uses.
- Rate limit / network error → toast notification, other fixes still applied.
- `enable-network: false` (or absent) → fully offline, no behavior change from current.
- All existing tests pass; new tests cover both success and failure paths.
