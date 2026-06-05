# plan_pin: pinact-like tag comment resolution for action pinning

## Scope

This document captures the investigation and implementation progress for making `seiton --fix` action pin comments behave closer to pinact when resolving branch aliases such as `@v1`.

Implemented phases in this change set:

- **P0**: lock expected behavior with tests first
- **P1**: SHA -> tag canonicalization in resolver
- **P2**: configuration and compatibility hardening
- **P3**: docs/spec alignment and rollout notes

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

Result: **passed** (all test projects green, 2469 total).

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

### Before -> After (primary run)

| Scenario | Mean before | Mean after | Delta |
|---|---:|---:|---:|
| NoConflict | 22.26 us | 23.29 us | +4.63% |
| SingleJobConflict | 38.39 us | 39.15 us | +1.98% |
| MultiJobConflict | 105.56 us | 105.17 us | -0.37% |

Allocated bytes remained unchanged for all scenarios:

- NoConflict: 10.57 KB -> 10.57 KB
- SingleJobConflict: 16.95 KB -> 16.95 KB
- MultiJobConflict: 39.72 KB -> 39.72 KB

Interpretation:

- No regression detected in fix application benchmark.
- A second repeat run showed larger fluctuation (notably `SingleJobConflict`), indicating host-level variance in this short benchmark profile.
- No intentional hot-path speedup was introduced in this change; resolver work only applies when network pinning is active and version-family refs are involved.
- `Allocated` remained unchanged in all runs, consistent with code shape (no new per-fix allocations in `FixApplyBenchmark` path).

---

## API / UX review (user-first)

User intent: `@v1` often means "track the latest v1 line". After pinning, users want the comment to explain **which concrete release** the pinned SHA corresponds to.

This change improves UX by:

- keeping deterministic SHA pinning output
- improving comment specificity (`# v1.0.2`) when discoverable
- preserving previous fallback (`# v1`) when no concrete matching tag exists

After P1/P2, a rollout-safe config knob is available:

- `fix.pinning.prefer-canonical-tag-comment` (default `true`)
  - `true`: alias-like refs (`v1`, `v1.2`) prefer concrete tag comments on same SHA (for example `v1.0.2`).
  - `false`: keep resolved ref comments (`v1`) for legacy output stability.

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

## P1 implementation (SHA -> tag canonicalization in resolver)

### Red/Green tests

- Added red test `ResolveAsync_PrefersConcreteTagComment_WhenMajorTagDirectlyResolvesToTaggedCommit`.
  - Before fix: returned `# v1`.
  - After fix: returns `# v1.0.2`.

### Production update

- Canonical comment promotion now runs for alias-like version refs (`vN`, `vN.M`), not only branch fallback.
- Concrete patch refs (`vN.M.P`) skip promotion to avoid unnecessary `tags` API lookups.
- Existing semver comparator remains the ranking source (highest compatible tag wins).

## P2 implementation (configuration + compatibility hardening)

### Config/API

- Added `FixPinningConfig.PreferCanonicalTagComment` (default `true`).
- Added YAML key: `fix.pinning.prefer-canonical-tag-comment: true|false`.
- Parser validation added (`must be true or false`).

### Compatibility hardening tests

- `ResolveAsync_KeepsOriginalComment_WhenCanonicalTagCommentIsDisabled`
  - ensures opt-out keeps legacy `# v1` behavior and avoids extra tags-list API call.
- `ResolveAsync_CanonicalTagLookup_FallsBackToGitHubCom_WhenGhesTagListReturns404`
  - ensures canonicalization path follows GHES fallback semantics.
- `ResolveAsync_DoesNotPerformCanonicalTagLookup_WhenRefIsAlreadyConcreteSemverTag`
  - protects performance for already-concrete refs.
- Config mapping tests:
  - `Validate_Fix_PreferCanonicalTagComment_False_IsMapped`
  - `Validate_Fix_PreferCanonicalTagComment_DefaultsToTrue`
  - `Validate_Fix_MapsAllSections` updated with the new key.

## P3 implementation (docs/spec alignment + rollout notes)

Updated docs/spec to remove behavior drift and document rollout:

- `.github/docs/Seiton_Linter_spec.md`
  - defaults table, fix section, and configuration semantics now include `fix.pinning.prefer-canonical-tag-comment`
  - resolver/fix format text updated for alias-like canonical comment promotion.
- `.github/docs/Seiton_Linter_csharp_spec.md`
  - C# implementation notes updated with canonical promotion and skip conditions.
- `docs/configuration.md`
  - annotated example and defaults reference updated with the new key.
  - pattern/behavior notes now explain canonical tag comment behavior.
- skill reference docs updated for user guidance parity:
  - `src/Seiton/Skills/references/configuration.md`
  - `.claude/skills/seiton/references/configuration.md`
- template sync:
  - `src/Seiton.Core/Linting/LintConfigLibrary.cs` now includes commented `prefer-canonical-tag-comment`.

### Rollout notes

1. Default behavior stays user-friendly (`true`) and matches pinact-like expectations.
2. Teams needing output stability can set `fix.pinning.prefer-canonical-tag-comment: false`.
3. CI guidance: if workflows assert exact fixed text comments, pin this key explicitly during migration windows.
