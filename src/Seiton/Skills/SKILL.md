---
name: seiton
description: Lint and fix GitHub Actions workflow files and action metadata files using seiton CLI.
---

# seiton

Seiton is a linter and auto-fixer for GitHub Actions workflow files (`.github/workflows/*.yml`) and action metadata files (`action.yml`).

## Quick Start

```bash
# Lint all workflows in the current repository
seiton

# Preview fixes without applying
seiton --fix --dry-run

# Apply auto-fixes
seiton --fix

# Pin actions and images via network
seiton --fix --enable-pin-network --enable-image-network
```

## Commands

| Command | Description |
|---------|-------------|
| `seiton` | Lint workflows (default) |
| `seiton check` | Explicit lint (same as default) |
| `seiton --fix` | Apply auto-fixes in place |
| `seiton --fix --dry-run` | Preview fixes as unified diff |
| `seiton --fix --show-diff` | Apply fixes and print unified diff |
| `seiton --fix --check` | Exit non-zero if fixable issues exist |
| `seiton init` | Generate starter config at `.github/seiton.yaml` |
| `seiton install` | Install agent skill files and CI workflow templates |
| `seiton rules` | List all lint rules and their status |
| `seiton validate-config` | Validate the config file |
| `seiton version` | Show version info |

## Key Flags

| Flag | Description |
|------|-------------|
| `--config PATH` | Explicit config file path |
| `--format text, json, sarif, github-actions` | Output format (`github-actions` default on GHA when flag omitted) |
| `--min-severity error, warning, info` | Filter by severity |
| `--ignore PATTERN` | Suppress diagnostics matching pattern |
| `--oneline` | One diagnostic per line |
| `--verbose` | Show progress and timing info |
| `--include-actions` | Include `<cwd>/.github/actions/` in discovery (CWD-scoped; no parent walk) |
| `--enable-pin-network` | Resolve action SHAs via network (fix mode) |
| `--enable-image-network` | Resolve image digests via network (fix mode) |

## Output Format

### GitHub Actions (`github-actions`)

Default on GitHub Actions runners when `--format` is omitted (`GITHUB_ACTIONS` set). Use plain `seiton` in workflow steps.

- **stdout** — diagnostics grouped per file with `::group::` / `::endgroup::`; body is rich text by default or one-line with `--oneline`.
- **Job summary** — appends Markdown to `GITHUB_STEP_SUMMARY` when writable (`## Seiton`, counts, tables). Pass `-e GITHUB_STEP_SUMMARY` in Docker.
- **stderr** — verbose progress, config errors, and `hint:` lines only (never duplicated into the summary).

Force local-style flat output on GHA: `seiton --format text`. For Code Scanning use `--format sarif` (separate from job summary).

`seiton rules` accepts only `text` or `json` (not `github-actions` / `sarif`).

### Rich text (`text`, default locally)

Default rich text output shows:

```
error[rule-id]: message
  --> file.yml:12:5
     |
  12 |     source line
     |     ^^^^^^^^^^^
     |
   = help: suggestion
```

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | No issues found |
| `1` | Lint issues found |
| `2` | Invalid CLI options |
| `3` | Fatal error (config error, I/O failure) |

## Recommended Workflow

1. Run `seiton` to identify all diagnostics
2. Review results — distinguish genuine issues from acceptable patterns
3. Tune per-rule config (`rules.<rule-id>`) for repository policy choices
4. Re-run `seiton` and repeat steps 2–3 until only actionable issues remain
5. Run `seiton --fix --dry-run` to preview available auto-fixes
6. Run `seiton --fix` to apply fixes (or `seiton --fix --show-diff` to apply and print the diff)
7. For pinning: `seiton --fix --enable-pin-network --enable-image-network`

### First adoption (many new diagnostics)

**More findings than before is normal** — not a seiton bug. Default rules are broader than a minimal syntax-only check.

Phased rollout:

1. `seiton --min-severity error` — fix or exclude blocking issues first
2. Full `seiton` + `--fix` — address warnings
3. Enable opt-in / online rules in config when ready (`impostor-commit`, etc.)

Rules that often dominate first runs: `run-env-context-direct-use`, `deny-inherit-secrets`, `if-expr-wrapper`, `unpinned-uses`. See `references/adoption-workflow.md` for the full table, verbose output interpretation, and agent checklist.

## Best Practices

### Fix first, exclude only when necessary

When a diagnostic is reported, follow this decision flow:

1. **Can `--fix` resolve it?** → Run `seiton --fix` (or `--fix --dry-run` to preview).
   Most issues have auto-fix support. Fix them rather than suppressing.
2. **Is it a genuine issue without auto-fix?** → Fix it manually in the workflow file.
3. **Is this a repository-wide rule policy choice?**
   (e.g., "we always use `-latest` runners", or severity should be warning instead of error)
   → Adjust `rules: <rule-id>:` (`enabled` / `severity`) first.
4. **Is the violation intentional only for specific files/jobs?**
   (demo file, legacy constraint, deliberate pattern)
   → Add an `exclusions` entry scoped to that file/job.

**Exclusions are for exceptions, not for avoiding fixes.** If a diagnostic has a fix
available, apply the fix. Reserve `exclusions` for:
- Auto-generated / uneditable files (see below)
- Intentional bad-practice demos or test fixtures
- Temporary constraints where a specific file cannot comply yet

### Exclude auto-generated and uneditable workflows

Some workflow files should not be linted because they are generated or uneditable:

- **Agentic workflows (gh-aw)** — see below for `skip-agentic-workflows` vs `exclusions`
- **Tool-generated workflows** — output of code generators with "DO NOT EDIT" headers
- **Intentional bad-practice demos** — files that deliberately showcase insecure or
  incorrect patterns for learning/testing purposes

**gh-aw (Agentic Workflow) — two mechanisms (do not confuse them):**

| Mechanism | Matches | Example |
|---|---|---|
| `discovery.skip-agentic-workflows: true` | `# gh-aw-metadata:` in the **first 10 lines** only | `monthly-oss-repo-status.lock.yml` |
| `exclusions` with `file` only | Paths you list explicitly | `agentics-maintenance.yml` (often has `DO NOT EDIT` but **no** metadata line) |

```yaml
discovery:
  skip-agentic-workflows: true

exclusions:
  # gh-aw file without # gh-aw-metadata: header
  - file: ".github/workflows/agentics-maintenance.yml"
  # Other generated or demo files (file-only = skip all rules for that file)
  - file: ".github/workflows/copilot-*.yml"
  - file: ".github/workflows/injection-attack-demo.yaml"
```

The `file:` value is a glob matched against the repository-relative path
(e.g., `.github/workflows/foo.yml`). Omitting `rules` suppresses all diagnostics for matching files.
`rules: ["*"]` is an explicit alias for the same behavior.

### Use help messages to tune config

Each diagnostic includes a `= help:` line with actionable guidance — often showing the
exact config key or exclusion pattern to suppress it. Read these hints to decide whether
to fix the issue or suppress it via config.

### Enable online rules explicitly in config

Online rules are opt-in and should be enabled in `.github/seiton.yaml` via `rules.<rule-id>.enabled: true`, not by exclusions.

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

### Prefer `exclusions` over `rules: enabled: false`

- `rules: <rule-id>: enabled: false` disables a rule **globally for all files, permanently**.
  Use it only as a last resort.
- `exclusions` lets you suppress by file glob, job ID, and rule — scoped and reversible.
- **Decision criteria:** "Should this rule be ignored for ALL future files too?"
  If Yes → `rules: enabled: false`. If No → `exclusions` for the specific files/jobs.

```yaml
# Good: scoped exclusion for a legacy file
exclusions:
  - file: ".github/workflows/legacy-deploy.yml"
    rules:
      - unpinned-uses

# Last resort: disable rule globally
rules:
  runner-no-latest:
    enabled: false
```

### Suppressing diagnostics (config vs inline)

**Prefer `.github/seiton.yaml` `exclusions`** for file-wide, job-wide, or repeated suppressions. Use inline comments only for one-off cases in a single workflow.

| Situation | Mechanism |
|-----------|-----------|
| Many files or a glob pattern | `exclusions` with `file:` |
| One job in one file | `exclusions` with `file:` + `jobs:` — or `# seiton: disable-job` |
| Single line | `# seiton: disable-next-line <rule-id>` |
| Entire workflow (rare) | `# seiton: disable-file` at top of file |

When a workflow still has **unrecognized inline comments** from another linter, translate the intent into `exclusions` or seiton directives — seiton does not read foreign comment syntax. See `references/inline-suppression.md` for full syntax, placement pitfalls (`if-cond`, `matrix`), and comma-separated rule IDs.

```yaml
# One-off: unpinned action on the next line only
steps:
  # seiton: disable-next-line unpinned-uses
  - uses: actions/checkout@v6
```

### Iterate until clean

The tuning loop is: **run → review → configure → re-run**. Repeat until only genuine,
actionable issues remain. Don't try to get to zero diagnostics on the first pass.

## Configuration

Config is auto-discovered from `<cwd>/.github/seiton.yaml` (or `.github/seiton.yml`, `seiton.yaml`, `seiton.yml` under cwd). Use `--config`, `-c` or `SEITON_CONFIG` for paths outside cwd.

Setup flow:

```bash
seiton init
seiton validate-config
seiton --verbose    # confirm resolved config on stderr
```

## Troubleshooting

- **Config errors**: Run `seiton validate-config` to check configuration
- **Unknown option**: seiton suggests the closest valid option with a `Did you mean` hint
- **Too many warnings**: Use `--min-severity error` to focus on errors only
- **More diagnostics than expected**: Usually broader default rules, not a defect — see `references/adoption-workflow.md`
- **CI integration (GHA)**: Default `github-actions` writes the job summary and rich stdout; use `--format sarif` for Code Scanning upload. Docker: `-e GITHUB_ACTIONS -e GITHUB_STEP_SUMMARY`

## References

- `references/rules.md` — All rule IDs, severities, fix support, and categories
- `references/fix-mode.md` — Auto-fix commands, flags, and configuration
- `references/configuration.md` — Full seiton.yaml schema and common patterns
- `references/inline-suppression.md` — Config vs inline suppression, directive syntax, placement pitfalls
- `references/adoption-workflow.md` — First-run diagnostic volume, phased rollout, common high-count rules
