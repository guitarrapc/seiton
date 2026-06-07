---
name: seiton
description: Lint and fix GitHub Actions workflow files and action metadata files using seiton CLI.
---

# seiton

Seiton lints and auto-fixes GitHub Actions workflow files (`.github/workflows/*.yml`) and action metadata (`action.yml`).

## Recommended Workflow

1. Initialize and validate config:
   ```bash
   seiton init
   seiton validate-config
   ```
2. Lint and fix:
   ```bash
   seiton --min-severity error
   seiton --fix --dry-run
   seiton --fix
   seiton
   ```
3. Repeat `run -> review -> config tune -> re-run` until only intended findings remain.

### First adoption (many new diagnostics)

More findings than before is normal. Start with errors, then warnings, then opt-in and online rules.
See `references/adoption-workflow.md` for phased rollout details.

## Best Practices

### Fix first, exclude only when necessary

When diagnostics are reported, use this order:

1. Fix with `--fix` when possible.
2. Manually fix non-auto-fix diagnostics.
3. If it is repository policy, tune rule config first:
   - `rules.<rule-id>.enabled`
   - `rules.<rule-id>.severity`
4. Use `exclusions` only for scoped exceptions (specific files/jobs).

Do not use `exclusions` to avoid legitimate fixes.

### Exclude auto-generated and uneditable workflows

Use `exclusions` for generated files, intentional demo fixtures, or temporary legacy constraints.

For gh-aw files, keep this split clear:
- `discovery.skip-agentic-workflows: true` matches only files with `# gh-aw-metadata:` in the first 10 lines.
- Files without that metadata (for example, `agentics-maintenance.yml`) need explicit `exclusions`.

```yaml
discovery:
  skip-agentic-workflows: true

exclusions:
  - file: ".github/workflows/agentics-maintenance.yml"
  - file: ".github/workflows/copilot-*.yml"
```

`file:` is repository-relative glob. Omitting `rules` (or using `rules: ["*"]`) suppresses all rules for matching files.

### Use help messages to tune config

Read each diagnostic `= help:` line; it often gives the exact config key or suppression pattern.

### Enable online rules explicitly in config

Online rules are opt-in. Enable them in `.github/seiton.yaml` via `rules.<rule-id>.enabled: true` (not by exclusions).

```yaml
rules:
  known-vulnerable-actions:
    enabled: true
  impostor-commit:
    enabled: true
  ref-confusion:
    enabled: true
  stale-action-refs:
    enabled: true
```

These rules require `GITHUB_TOKEN` or `SEITON_GITHUB_TOKEN`.

### Suppressing diagnostics (config vs inline)

Prefer `.github/seiton.yaml` for repeated or broad suppressions. Use inline directives only for one-off local cases.

```yaml
steps:
  # seiton: disable-next-line unpinned-uses
  - uses: actions/checkout@v6
```

## Configuration

Config is auto-discovered from `<cwd>/.github/seiton.yaml` (or `.yml`, plus `seiton.yaml/.yml` under cwd).
Use `--config`/`-c` or `SEITON_CONFIG` for custom paths.

## Troubleshooting

- Config errors: run `seiton validate-config`.
- Too many findings: start with `--min-severity error`.
- More findings than expected: see `references/adoption-workflow.md`.
- CI output format and SARIF usage: see `references/fix-mode.md` and `references/configuration.md`.

## References

- `references/rules.md` — rule IDs, defaults, severity, opt-in/online notes
- `references/fix-mode.md` — fix commands, network flags, output formats
- `references/configuration.md` — full `seiton.yaml` schema and examples
- `references/inline-suppression.md` — inline directive syntax and placement pitfalls
- `references/adoption-workflow.md` — first-adoption rollout and common high-count rules
