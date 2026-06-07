# Adoption Workflow

Use this reference when a repository **first runs seiton** and the user is surprised by diagnostic volume, or asks whether new findings are a bug.

**Key message:** more diagnostics than before does **not** mean seiton is broken. Seiton ships a broad default rule set (57 local rules on by default, plus optional online rules). Many findings are genuine issues that were simply not checked previously.

## Phased adoption

Roll out in stages instead of fixing everything at once.

### Phase 1 — errors only

Focus CI and first local runs on blocking issues:

```bash
seiton init
seiton validate-config
seiton --min-severity error
```

- Port intentional suppressions into `.github/seiton.yaml` `exclusions` (see `references/configuration.md` and `references/inline-suppression.md`).
- Use `seiton --verbose` to confirm config path, discovery count, and `suppressed:` totals.
- Exclude generated or demo workflows before debating individual rules.

### Phase 2 — warnings

When error count is manageable, drop the severity filter and tune warnings:

```bash
seiton
seiton --fix --dry-run
```

- Fix what `--fix` can handle first.
- For acceptable patterns, use scoped `exclusions` — not global `rules: enabled: false`.
- Read each diagnostic's `= help:` line for config hints.

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
| `run-env-context-direct-use` | error | Flags `${{ env.* }}` in `run:` scripts; many repos use env this way | `--fix` where available; migrate to `$ENV_VAR` / `env:` block, or scoped exclusion for legacy files |
| `run-secrets-context-direct-use` | error | Same pattern for `${{ secrets.* }}` in shell | Prefer `env:` mapping; fix rather than exclude |
| `run-inputs-context-direct-use` | error | Same pattern for `${{ inputs.* }}` | Same as secrets |
| `deny-inherit-secrets` | error | `secrets: inherit` on `workflow_call` | Fix caller/callee design, or job-scoped exclusion if intentional |
| `if-expr-wrapper` | warning | `${{ }}` wrapper style in conditions | Often auto-fixable with `--fix` |
| `if-cond` | warning | Redundant or non-boolean `if:` expressions | Review; exclude only when intentional |
| `ref-version-mismatch` | warning | Tag/ref drift between caller and reusable workflow | Pin refs or exclude specific call sites |
| `job-permissions-required` | warning | Missing explicit `permissions:` | `--fix` adds minimal permissions |
| `unpinned-uses` | mixed | Unpinned action refs | `--fix` with `--enable-pin-network` in CI |

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
2. Run `seiton --verbose --min-severity error` and capture file table + counts.
3. Separate **demo/generated files** → `exclusions` or `skip-agentic-workflows`.
4. Check whether opt-in / online rules were recently enabled in config.
5. Apply Phase 1 → 2 → 3; do not aim for zero diagnostics on day one.
6. Use `seiton rules` / `references/rules.md` for rule IDs, severities, and fix support.

## Related references

- `references/configuration.md` — `exclusions`, `discovery`, rule overrides
- `references/inline-suppression.md` — one-off suppressions in workflow files
- `references/fix-mode.md` — auto-fix and pinning flags
- `references/rules.md` — full rule table and opt-in list
