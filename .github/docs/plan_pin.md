# plan_pin: pinact-like tag comment resolution for action pinning

## Scope

This document captures the investigation and implementation progress for making `seiton --fix` action pin comments behave closer to pinact when resolving branch aliases such as `@v1`.

In-scope phase implemented in this change:

- **P0 (Highest): lock expected behavior with tests first**
- Minimal production implementation to satisfy P0 tests
- Benchmark and regression verification

---

## Problem statement

Given:

```yaml
- uses: guitarrapc/setup-seiton@v1
```

where `v1` is a branch and the same commit SHA is tagged as `v1.0.2`,
Seiton previously produced:

```yaml
- uses: guitarrapc/setup-seiton@<sha40> # v1
```

Expected (pinact-like) behavior:

```yaml
- uses: guitarrapc/setup-seiton@<sha40> # v1.0.2
```

---

## Root cause (investigation)

Before this change, `GitHubActionShaResolver` stored `resolvedRef` as `TagComment` and `PinFixFormatter` emitted it verbatim.

- Tag lookup path: `git/ref/tags/{ref}`
- Branch fallback path: `git/ref/heads/{ref}` (only when tag lookup is 404)
- Comment text was the resolved ref string (`v1` in branch fallback), with no reverse lookup from resolved SHA to concrete tags.

This made comments stable but less informative for alias branches.

---

## P0 implementation (test-first)

## 1) Red: new failing behavior tests

Added tests in `tests/Seiton.Core.Tests/GitHubActionShaResolverTests.cs`:

1. `ResolveAsync_PrefersConcreteTagComment_WhenBranchAliasResolvesToTaggedCommit`
   - `tags/v1` is missing
   - `heads/v1` resolves to SHA `X`
   - `tags?per_page=100` contains `v1.0.2 -> X`
   - expected comment: `v1.0.2`

2. `ResolveAsync_PrefersHighestSemverTagComment_WhenMultipleCompatibleTagsPointToSameCommit`
   - same SHA has `v1`, `v1.0.1`, `v1.0.2`
   - expected comment: highest compatible semver (`v1.0.2`)

3. `ResolveAsync_KeepsBranchAliasComment_WhenNoCompatibleTagPointsToResolvedCommit`
   - branch resolves to SHA `X`
   - tags exist but not pointing to `X`
   - expected comment remains alias (`v1`)

Red verification command:

```bash
dotnet test --project tests/Seiton.Core.Tests --treenode-filter "/*/*/GitHubActionShaResolverTests/ResolveAsync_PrefersConcreteTagComment_WhenBranchAliasResolvesToTaggedCommit*"
```

Result: **failed as expected** (`expected v1.0.2, got v1`).

## 2) Green: minimal production code

Updated `src/Seiton.Core/Linting/PinRemediation/GitHubActionShaResolver.cs`:

- Added branch-fallback marker (`UsedBranchFallback`) in `ResolveAttemptResult`.
- On successful branch fallback and version-family input (`vN`, `vN.M`, `vN.M.P`):
  - fetch `repos/{owner}/{repo}/tags?per_page=100`
  - filter tags by:
    - same commit SHA as resolved branch SHA
    - compatible version family
  - choose highest semver via existing `CompareVersionTag`
  - promote `TagComment` to that concrete tag when found
- Added per-repo+sha cache (`_canonicalTagByShaCache`) to avoid repeated reverse lookups.

Performance-sensitive design choices:

- Reverse lookup is only executed for **branch fallback** path, not normal tag resolution.
- Reuses existing semver comparator and JSON parsing style.
- Uses bounded endpoint (`per_page=100`) and short-circuit cache.

## 3) Regression tests

Targeted test run:

```bash
dotnet test --project tests/Seiton.Core.Tests --treenode-filter "/*/*/GitHubActionShaResolverTests/*"
```

Result: **18/18 passed**.

Full suite run:

```bash
dotnet test
```

Result: **passed** (all test projects green, 2463 total).

---

## Benchmark verification

Benchmark used:

```bash
cd src/Seiton.Benchmark
dotnet run -c Release --filter "*FixApplyBenchmark*"
```

Rationale:

- This change affects fix-time pin remediation path and comment construction.
- `FixApplyBenchmark` is the closest existing benchmark that exercises fix application overhead.

### Before -> After

| Scenario | Mean before | Mean after | Delta |
|---|---:|---:|---:|
| NoConflict | 22.26 us | 20.09 us | -9.75% |
| SingleJobConflict | 38.39 us | 34.81 us | -9.33% |
| MultiJobConflict | 105.56 us | 94.88 us | -10.12% |

Allocated bytes remained unchanged for all scenarios:

- NoConflict: 10.57 KB -> 10.57 KB
- SingleJobConflict: 16.95 KB -> 16.95 KB
- MultiJobConflict: 39.72 KB -> 39.72 KB

Interpretation:

- No regression detected in fix application benchmark.
- Measured improvement is likely run-to-run noise plus benchmark process variance; no intentional hot-path speedup was introduced in this change.
- The new resolver path does not affect these local-only benchmark scenarios because it activates only in networked branch-fallback resolution.

---

## API / UX review (user-first)

User intent: `@v1` often means "track the latest v1 line". After pinning, users want the comment to explain **which concrete release** the pinned SHA corresponds to.

This change improves UX by:

- keeping deterministic SHA pinning output
- improving comment specificity (`# v1.0.2`) when discoverable
- preserving previous fallback (`# v1`) when no concrete matching tag exists

No CLI option or config key changes were introduced in P0.

---

## Spec alignment

Updated `.github/docs/Seiton_Linter_spec.md` to match implementation:

- resolver return semantics now describe branch-alias comment promotion
- fix format section now states comment promotion from alias to concrete matching tag on same commit

This resolves the prior ambiguity between:

- example showing concrete comment (`# v6.0.2`)
- sentence claiming comment always preserves original ref verbatim

---

## Self-review rounds

### Round 1

Finding:

- Risk of extra API overhead on every resolution.

Action:

- Limited reverse tag lookup to branch-fallback cases only.
- Added SHA-based cache to suppress repeated tag scans.

### Round 2

Finding:

- Potential behavior ambiguity for multi-tag same-SHA cases.

Action:

- Added explicit test requiring highest compatible semver selection.

### Round 3

Finding:

- Documentation mismatch with new behavior.

Action:

- Updated linter spec sections (resolver contract and fix format).

### Round 4

Finding:

- Canonical-tag cache keyed only by `{owner/repo@sha}` could return family-incompatible entries when the same SHA is tagged under multiple major families.

Action:

- Switched canonical-tag cache key to include version-family context (`{owner/repo@sha|family}`).
- Added regression test `ResolveAsync_CachesCanonicalTagComment_PerVersionFamily`.

No further blocking findings.

---

## Remaining phases (not yet implemented here)

- **P1**: explicit configurability for canonical tag comment preference (if needed for rollout control)
- **P2**: GHES/fallback focused edge-case tests for canonical comment selection
- **P3**: user-facing docs/examples beyond internal spec and release notes
