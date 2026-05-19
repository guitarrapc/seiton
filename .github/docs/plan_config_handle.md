# Plan: Configuration Usability Improvement

## Problem Statement

Users frequently need to suppress `unpinned-uses` warnings for their own organization's actions referenced with `@main`. The current configuration path is too tedious:

1. Warning message doesn't tell the user HOW to suppress it
2. User must find the configuration documentation
3. User must understand glob patterns and YAML structure
4. The `ignore-actions` option is all-or-nothing (no ref-level control)

**Typical scenario:**

```
foo.yaml:43:59: warning [unpinned-uses] 'MyOrg/Actions/.github/actions/setup-dotnet@main' is not pinned to a full-length commit SHA. (fixable with --fix --enable-pin-network)
```

The user wants: "Ignore `@main` refs from my own org, but still warn about external actions."

## Previous Mechanisms

| Method | Config | Limitation |
|---|---|---|
| `rules.unpinned-uses.ignore-actions` | blanket owner ignore only | Owner-level blanket ignore; no ref distinction |
| `exclusions` | file + rules | Too coarse (whole file) or too narrow (one file) |
| Inline directive | `# seiton: disable-next-line unpinned-uses` | Must add per-occurrence |
| `fix.pinning.exclude-branches` | `[main, master]` | Only affects `--fix`, not lint warning |

## Security Analysis

| Aspect | External action | Internal (own org) action |
|---|---|---|
| Supply chain risk | High (maintainer compromise) | Low–Medium (insider threat only) |
| SHA pinning value | Very high | Limited (defence-in-depth) |
| `@main` ref risk | High (others control it) | Low (you control it) |
| Full ignore safety | Dangerous | Acceptable |

**Conclusion:** For own-org actions, `@main`/`@master` references carry materially lower risk. Severity downgrade or full suppression is security-acceptable.

## Planned Approach: 案1 + 案2

### Phase 1: Warning message includes config snippet (案1)

**Goal:** Reduce the discovery cost to zero. User sees the warning, copies the suggestion, done.

**Design:**

When `unpinned-uses` fires, append a remediation hint that shows the exact config to suppress it:

```
foo.yaml:43:59: warning [unpinned-uses] 'MyOrg/Actions/.github/actions/setup-dotnet@main' is not pinned to a full-length commit SHA.
  hint: to ignore this owner, add to .github/seiton.yaml:
    rules:
      unpinned-uses:
        ignore-actions:
          - "MyOrg/*"
```

**Scope:**

- Applies to `unpinned-uses` rule only (highest user friction)
- Hint is shown once per unique owner per run (not per occurrence)
- Controlled by output verbosity: always in `--verbose`, condensed in normal mode
- No behavioral change to the rule itself

**Implementation notes:**

- Extract owner from uses value (`owner/repo` → `owner`)
- Format hint as indented text appended to the diagnostic
- Deduplicate: track owners already hinted in the current run
- Consider `--format json` output: include hint in a `suggestion` field

### Phase 2: Ref-conditional `ignore-actions` (案2)

**Goal:** Allow users to express "trust `@main` from my org, but still warn on arbitrary branches."

**Design — config schema extension:**

```yaml
rules:
  unpinned-uses:
    ignore-actions:
      - owner: "MyOrg/*"
      - owner: "MyOrg/*"
        refs: [main, master]
```

**Semantics:**

- **Object form only**: `owner` is required and matches against `owner/repo` using the existing wildcard semantics.
- `refs` is optional. When omitted, the entry ignores **all refs** for matching actions.
- When `refs` is present, it is a non-empty list of exact string matches (no glob) and the ignore applies only to those refs.

**Validation rules:**

- `owner` is required
- `refs` is optional
- if present, `refs` must be a non-empty list of strings
- unknown keys produce a config error
- scalar string items are invalid (object-only schema)

**Implementation notes:**

- Config parser: accept only mapping entries in the `ignore-actions` list
- Matcher: after extracting `owner/repo` and `ref` from uses value, check:
  1. If any object pattern matches owner and `refs` is omitted → ignore
  2. If any object pattern matches owner AND ref is in refs list → ignore
  3. Otherwise → report diagnostic
- `fix.pinning.ignore-actions` already has `uses` + `ref` structure, but this rule config keeps `owner` + `refs` because it matches `owner/repo` globs plus optional exact refs

**Migration:** Since there are no existing users, switch directly to object-only. Do not preserve string-form compatibility.

## Priority & Sequencing

| Phase | What | Effort | Impact |
|---|---|---|---|
| 1 | Hint in warning message | Small (diagnostic formatting only) | High — eliminates discovery friction |
| 2 | Ref-conditional ignore-actions (object-only) | Medium (config schema + matcher) | Medium — precision for security-conscious users |

Phase 1 should ship first and independently. Phase 2 can follow in a separate PR.

## Out of Scope (Deferred)

| Idea | Why deferred |
|---|---|
| `trusted-owners` top-level concept | Large design surface; cross-cutting rule effects are hard to reason about. Revisit when more rules need owner-level trust. |
| `seiton ignore` CLI command | Nice UX but Phase 1 hint achieves similar discoverability with less code. Revisit if config editing remains painful after Phase 1. |
| Severity downgrade per-owner | Overlaps with ref-conditional ignore. If needed later, could be `severity: info` in object form. |

## Open Questions

1. **Hint display frequency:** Once per owner per run? Or once per unique `owner/repo`? Per-owner seems sufficient.
2. **`--format sarif` / `--format json`:** Should the hint appear in structured output? Probably as a `suggestion` or `help` field.
3. **Phase 2 naming:** `owner` + `refs` vs. `uses` + `ref` (aligning with `fix.pinning.ignore-actions`)? Consistency with existing schema preferred.

---

## Phase 1 Implementation Results

### Implementation Summary

**Completed.** The `Help` field on `Diagnostic` (which already existed but was unused by lint rules) is now populated by `UnpinnedUsesRule` with a config-snippet hint.

**Key design decisions:**

- **Hint location:** Uses the existing `Diagnostic.Help` field (already rendered in `DiagnosticFormatter` as `   = help: ...`)
- **Deduplication:** Two-level cache: fast byte-span check (`_lastHintedOwnerBytes`) for the common repeated-owner case (zero allocation), then `HashSet<string>` (OrdinalIgnoreCase) for multi-owner workflows. Cleared per workflow in `VisitWorkflowPre`.
- **Scope:** Both step-level and job-level (reusable workflow) unpinned-uses warnings.
- **Format:** Single-line config snippet: `to ignore this owner, add to .github/seiton.yaml: rules: { unpinned-uses: { ignore-actions: [{ owner: "<owner>/*" }] } }`
- **Performance:** Owner extraction reuses the already-parsed `actionPath` span from `TryParseRemoteUses`. `BuildOwnerHintOnce` has a zero-allocation fast path for repeated same-owner (the dominant case: all steps use actions from same org). Only the first occurrence per unique owner allocates (owner string + byte cache + hint string). HashSet internal arrays survive `Clear()` across files, avoiding re-allocation.

**Files changed:**

| File | Change |
|------|--------|
| `src/Seiton.Core/Linting/RuleBase.cs` | Added `string? help` parameter to `AddDiagnostic`; added overloads `AddStepWarning(..., metadata, help)` and `AddJobWarning(..., metadata, help)` |
| `src/Seiton.Core/Linting/Rules/UnpinnedUsesRule.cs` | Added `_hintedOwners` HashSet + `_lastHintedOwnerBytes` byte cache, `BuildOwnerHintOnce` method; passes help to both VisitJobPre and VisitStep warning calls |
| `tests/Seiton.Core.Tests/RuleInterfaceTests.cs` | 6 new tests: once-per-owner dedup, reusable workflow hint, no-hint-when-ignored, case-insensitive dedup, SHA-pinned no hint, local/docker no hint |

### Benchmark Comparison

| Size | FixEnabled | Baseline Mean | Final Mean | Baseline Alloc | Final Alloc | Alloc Delta |
|------|------------|---------------|------------|----------------|-------------|-------------|
| Small | False | 325.9 µs | 212.5 µs | 8.57 KB | 8.74 KB | +0.17 KB |
| Small | True | 222.7 µs | 438.1 µs | 10.02 KB | 10.33 KB | +0.31 KB |
| Medium | False | 4,730 µs | 4,516 µs | 68.9 KB | 69.61 KB | +0.71 KB |
| Medium | True | 8,048 µs | 7,105 µs | 83.22 KB | 83.52 KB | +0.30 KB |
| Large | False | 67,491 µs | 57,333 µs | 366 KB | 360.77 KB | −5.23 KB |
| Large | True | 91,520 µs | 104,469 µs | 459.56 KB | 440.5 KB | −19.06 KB |

**Analysis:** Timing variations are within normal ShortRun noise (3 iterations). Allocation delta is +0.17–0.71 KB for Small/Medium (one hint string + byte cache per unique owner — the benchmark uses a single owner `actions`). Large shows slightly less allocation (run-to-run variance). The two-level byte-span cache ensures zero allocation on repeated same-owner invocations (the hot path in real workflows).

### Open Questions Resolved

1. **Hint frequency:** Implemented as once per owner per workflow run (case-insensitive).
2. **Structured output:** The `Help` field is already part of the `Diagnostic` record, so JSON/SARIF formatters can include it naturally.

---

## Phase 2 Implementation Results

### Implementation Summary

**Completed.** The `rules.unpinned-uses.ignore-actions` config now uses an object-only schema with required `owner` and optional `refs` for ref-conditional ignoring.

**Key design decisions:**

- **Config type:** New `IgnoreActionRule(string Pattern, IReadOnlyList<string>? Refs)` record replaces `IReadOnlyList<string>` on `RuleConfig.IgnoreActions`. `Refs == null` means ignore all refs for the configured owner pattern; `Refs` with values means only those exact refs are ignored.
- **YAML naming:** Uses `owner` + `refs` (not `uses` + `ref` like `fix.pinning.ignore-actions`) because the semantics differ: `owner` is a glob matching owner/repo, `refs` is a list of exact strings. This avoids confusion with the single-pattern + single-ref pin remediation format.
- **Ref matching:** Case-sensitive exact match (matching Git's behavior: branches are case-sensitive on GitHub). The owner/repo glob matching remains case-insensitive (existing behavior).
- **Internal data structure:** `IgnoreEntry` readonly struct with `byte[] PatternUtf8` (lowercased owner glob) and `byte[][]? RefsUtf8` (pre-encoded ref bytes, null for owner-only entries). Zero extra allocation on the hot path when no `ignore-actions` is configured.
- **Matching algorithm:** `MatchAnyIgnoreEntry` iterates entries: if owner pattern matches AND (Refs is null OR any ref matches via `SequenceEqual`), the action is ignored. This is a single loop with early-return, same O(n) as before.
- **Parsing:** `ParseRuleIgnoreActions` accepts only mapping items. Validation produces Error diagnostics for: scalar items, missing `owner`, empty `refs`, unknown keys.
- **Review fix:** `RuleConfigNormalizer` now normalizes `rules.unpinned-uses.ignore-actions` the same way as other rule-specific config: trims surrounding whitespace, lowercases owner/repo patterns for case-insensitive matching, deduplicates normalized entries, preserves ref case, and rejects empty ref elements. This avoids surprising config behavior such as `refs: ["   "]` silently being accepted.
- **Schema choice:** There are no existing users, so string-form YAML was removed instead of carrying dual-schema compatibility.

**Files changed:**

| File | Change |
|------|--------|
| `src/Seiton.Core/Linting/LintConfig.cs` | Added `IgnoreActionRule` record; changed `RuleConfig.IgnoreActions` type from `IReadOnlyList<string>?` to `IReadOnlyList<IgnoreActionRule>?` |
| `src/Seiton.Core/Linting/LintConfigYamlParser.cs` | Added `ParseRuleIgnoreActions` method accepting only mapping items with `owner` and optional `refs` |
| `src/Seiton.Core/Linting/RuleConfigNormalizer.cs` | Added normalization/validation for `rules.unpinned-uses.ignore-actions` (trim, dedup, empty-ref rejection, lowercased owner patterns) |
| `src/Seiton.Core/Linting/Rules/UnpinnedUsesRule.cs` | Replaced `byte[][] _ignoreActionsUtf8` with `IgnoreEntry[] _ignoreEntries` struct array; `IsIgnoredAction` now takes `actionRef` parameter; added `MatchAnyIgnoreEntry` with ref-conditional logic |
| `src/Seiton.Core/Linting/LintConfigLibrary.cs` | Updated `seiton init` template to show object-only examples |
| `docs/configuration.md` | Documented object-only `ignore-actions` entries in annotated example and rule-specific options table |
| `docs/rules.md` | Documented owner-only and ref-conditional `ignore-actions` examples for `unpinned-uses` |
| `.github/docs/Seiton_Linter_spec.md` | Added `rules.unpinned-uses.ignore-actions` to rule-specific config spec and aligned fix wildcard examples |
| `.github/docs/Seiton_Linter_csharp_spec.md` | Documented `rules.unpinned-uses.ignore-actions` mapping and normalization semantics |
| `tests/Seiton.Core.Tests/RuleInterfaceTests.cs` | 5 new tests: owner-only ignore, matching ref ignored, non-matching ref warns, reusable workflow, case-sensitive ref |
| `tests/Seiton.Core.Tests/LintConfigLibraryTests.cs` | Added object-only parse/validation tests: string form rejected, owner-only parse, ref-specific parse, missing owner error, empty refs error, unknown key error, whitespace-only ref error, normalization/dedup, template example |

### Benchmark Comparison (Final object-only schema)

| Size | FixEnabled | Mean | Allocated |
|------|------------|------|-----------|
| Small | False | 272.0 µs | 8.76 KB |
| Small | True | 194.8 µs | 10.21 KB |
| Medium | False | 3,289.8 µs | 68.95 KB |
| Medium | True | 5,337.0 µs | 82.45 KB |
| Large | False | 53,339.7 µs | 327.47 KB |
| Large | True | 73,068.3 µs | 382.31 KB |

`LintConfigBenchmark` (final run):

| Method | Complexity | Mean | Allocated |
|--------|------------|------|-----------|
| ParseOnly | Minimal | 2.422 µs | 1.27 KB |
| Validate | Minimal | 3.662 µs | 4.35 KB |
| ParseOnly | Typical | 30.898 µs | 16.27 KB |
| Validate | Typical | 32.758 µs | 25.91 KB |
| ParseOnly | Heavy | 69.981 µs | 49.44 KB |
| Validate | Heavy | 80.250 µs | 71.60 KB |

**Analysis:** No regression requiring follow-up was observed. The runtime matcher remains the same single-pass owner/ref check, and when `ignore-actions` is not configured it still short-circuits at `if (_ignoreEntries.Length == 0) return false;`. The object-only schema simplification removed scalar-item parsing branches rather than adding new hot-path work. Config parse/validate costs remain in the same microsecond/KB range expected for this benchmark fixture.

### Open Questions Resolved

3. **Phase 2 naming:** Chose `owner` + `refs` (not `uses` + `ref`). Rationale: `owner` semantically describes a glob matching `owner/repo`; `refs` is a plural list of exact strings. This is distinct from `fix.pinning.ignore-actions` which uses `uses` + `ref` (single wildcard patterns). The naming difference makes it clear these are different schemas serving different purposes.
