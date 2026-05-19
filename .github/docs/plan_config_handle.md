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

## Current Mechanisms

| Method | Config | Limitation |
|---|---|---|
| `rules.unpinned-uses.ignore-actions` | `["MyOrg/*"]` | Owner-level blanket ignore; no ref distinction |
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
      # Simple string form (existing, unchanged behavior — ignore all refs)
      - "MyOrg/*"

      # Extended object form (new — ref-conditional ignore)
      - owner: "MyOrg/*"
        refs: [main, master]
```

**Semantics:**

- **String form** (backward compatible): Ignores the action for ALL refs. Matches against `owner/repo`.
- **Object form** (new): Ignores only when the ref matches one of the listed values. `owner` uses the same glob matching as string form. `refs` is exact string match (no glob).

**Validation rules:**

- `owner` is required in object form
- `refs` is required in object form (otherwise use string form)
- `refs` must be a non-empty list of strings
- Unknown keys in object form produce a config error

**Implementation notes:**

- Config parser: detect string vs mapping in the `ignore-actions` list
- Matcher: after extracting `owner/repo` and `ref` from uses value, check:
  1. If any string pattern matches → ignore
  2. If any object pattern matches owner AND ref is in refs list → ignore
  3. Otherwise → report diagnostic
- `fix.pinning.ignore-actions` already has `uses` + `ref` structure; align naming for consistency

**Migration:** No migration needed. Existing string configs continue to work unchanged.

## Priority & Sequencing

| Phase | What | Effort | Impact |
|---|---|---|---|
| 1 | Hint in warning message | Small (diagnostic formatting only) | High — eliminates discovery friction |
| 2 | Ref-conditional ignore-actions | Medium (config schema + matcher) | Medium — precision for security-conscious users |

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
