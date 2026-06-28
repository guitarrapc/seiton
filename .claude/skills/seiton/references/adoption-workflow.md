# Adoption Workflow

Use this reference when a repository **first runs seiton** and the user is surprised by diagnostic volume, or asks whether new findings are a bug.

**Key message:** more diagnostics than before does **not** mean seiton is broken. Seiton ships a broad default rule set (58 local rules on by default, plus optional online rules). Many findings are genuine issues that were simply not checked previously.

## Phased adoption

Roll out in stages instead of fixing everything at once.

### Phase 1 — errors only

Focus CI and first local runs on blocking issues:

```bash
seiton init
seiton validate-config
seiton --min-severity error
```

- **Exit code:** Warnings alone still produce exit code `1` unless you pass `--min-severity error`; keep that flag in CI until you are ready to enforce warnings.
- Port intentional suppressions into `.github/seiton.yaml` `exclusions` (see `references/configuration.md` and `references/inline-suppression.md`).
- Use `seiton --verbose` to confirm config path, discovery count, and `suppressed:` totals.
- Exclude generated or demo workflows before debating individual rules.

### Phase 2 — warnings

When error count is manageable, drop the severity filter and tune warnings:

```bash
seiton
seiton --fix --dry-run
seiton --fix
seiton
```

- **Try `--fix` before `exclusions`.** Run `seiton --fix --dry-run`, review the diff summary, then `seiton --fix`. Many rules (context direct-use, `if-expr-wrapper`, permissions, timeouts, pinning with network enabled in config) are auto-fixable; exclusions should cover only what remains intentional.
- For acceptable patterns that are **not** fixable (demo warnings, policy exceptions), use scoped `exclusions` — not global `rules: enabled: false`.
- Read each diagnostic's `= help:` line for config hints.

### Fix before exclusions (all rules)

Use this order whenever diagnostic volume feels high:

1. `seiton --fix --dry-run` — inspect per-file / per-rule fix counts and unified diffs.
2. `seiton --fix` — apply safe mechanical fixes.
3. `seiton` — see what still fails lint.
4. Manual edits for diagnostics without auto-fix.
5. Scoped `exclusions` or inline suppressions **only** for deliberate exceptions (demo fixtures, generated files, accepted policy).

Do **not** add bulk `exclusions` for `run-*-context-direct-use` (or other fixable rules) without step 1. Real-world adoption often shows most context hits are fixable in one pass (e.g. dozens of `${{ env.* }}` / `${{ secrets.* }}` / `${{ inputs.* }}` in `run:` scripts).

### Phase 3 — opt-in and online rules

Enable stricter or network-backed rules only when the repo is ready:

```yaml
rules:
  impostor-commit:
    enabled: true
  known-vulnerable-actions:
    enabled: true
  ref-confusion:
    enabled: true
  stale-action-refs:
    enabled: true
  concurrency-limits:
    enabled: true
```

Online rules need `GITHUB_TOKEN` or `SEITON_GITHUB_TOKEN` in CI. See `references/rules.md` (Opt-in Rules).

## Rules that often dominate a first run

These are **default-on** rules that frequently produce many hits on existing repos. Treat them as tuning targets, not product defects.

| Rule ID | Default severity | Why it surprises | First response |
|---------|------------------|------------------|----------------|
| `run-env-context-direct-use` | error | Flags `${{ env.* }}` in `run:` scripts; many repos use env this way | `seiton --fix --dry-run` then `--fix` (rewrites to shell vars / step `env:`); exclude only leftover intentional cases |
| `run-secrets-context-direct-use` | error | Same pattern for `${{ secrets.* }}` in shell | Same: `--fix --dry-run` first; inserts step `env:` when needed |
| `run-inputs-context-direct-use` | error | Same pattern for `${{ inputs.* }}` | Same as secrets |
| `deny-inherit-secrets` | error | `secrets: inherit` on `workflow_call` | Fix caller/callee design, or job-scoped exclusion if intentional |
| `if-expr-wrapper` | warning | `${{ }}` wrapper style in conditions | `seiton --fix --dry-run` then `--fix` |
| `if-cond` | warning | Redundant or non-boolean `if:` expressions | Review; exclude only when intentional (not auto-fixed) |
| `ref-version-mismatch` | warning | Tag/ref drift between caller and reusable workflow | Pin refs or exclude specific call sites |
| `job-permissions-required` | warning | Missing explicit `permissions:` | `seiton --fix` adds minimal permissions |
| `unpinned-uses` | mixed | Unpinned action refs | `seiton --fix --dry-run` with `fix.pinning.enable-network: true` or `--enable-pin-network` |

When the user enabled **opt-in** rules (e.g. `impostor-commit`), expect **additional** errors — that is by design.

## Interpreting summary output

```
verbose: discovery: 120 file(s) resolved
verbose: discovery: skipped foo.lock.yml (agentic workflow)
verbose: suppressed: 3 diagnostic(s) (dangerous-triggers: 2, deny-inherit-secrets: 1)
verbose: total: 119 file(s) checked in ~10 ms
```

| Line | Meaning |
|------|---------|
| `skipped` | File excluded by discovery (`skip-agentic-workflows`) or config |
| `suppressed` | Diagnostics matched an `exclusions` entry or inline directive |
| `excluded` (in summary table) | File-level exclusion — no lint run for that file |

`suppressed > 0` with remaining errors means exclusions are **partial** — not a failed suppression.

## Agent checklist

When the user says "seiton reports too many errors":

1. Confirm this is **first adoption**, not a regression after a seiton upgrade.
2. Run `seiton --verbose --min-severity error` and capture file + rule breakdown tables.
3. Run `seiton --fix --dry-run` before proposing bulk `exclusions` — report how many issues are fixable.
4. Separate **demo/generated files** → `exclusions` or `skip-agentic-workflows` (files that should not be linted at all).
5. Check whether opt-in / online rules were recently enabled in config.
6. Apply Phase 1 → 2 → 3; do not aim for zero diagnostics on day one.
7. Use `seiton rules` / `references/rules.md` for rule IDs, severities, and fix support.

## Related references

- `references/configuration.md` — `exclusions`, `discovery`, rule overrides
- `references/inline-suppression.md` — one-off suppressions in workflow files
- `references/fix-mode.md` — auto-fix and pinning flags
- `references/rules.md` — full rule table and opt-in list
