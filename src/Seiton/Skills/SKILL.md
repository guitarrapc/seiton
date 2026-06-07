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
3. Configure exclusions for intentional patterns (see Best Practices below)
4. Re-run `seiton` and repeat steps 2–3 until only actionable issues remain
5. Run `seiton --fix --dry-run` to preview available auto-fixes
6. Run `seiton --fix` to apply fixes (or `seiton --fix --show-diff` to apply and print the diff)
7. For pinning: `seiton --fix --enable-pin-network --enable-image-network`

## Best Practices

### Fix first, exclude only when necessary

When a diagnostic is reported, follow this decision flow:

1. **Can `--fix` resolve it?** → Run `seiton --fix` (or `--fix --dry-run` to preview).
   Most issues have auto-fix support. Fix them rather than suppressing.
2. **Is it a genuine issue without auto-fix?** → Fix it manually in the workflow file.
3. **Is the violation intentional?** (demo file, legacy constraint, deliberate pattern)
   → Add an `exclusions` entry scoped to that file/job.
4. **Does the rule conflict with the repository's permanent policy?**
   (e.g., "we always use `-latest` runners") → `rules: <rule-id>: enabled: false`.

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

### Use help messages to tune config

Each diagnostic includes a `= help:` line with actionable guidance — often showing the
exact config key or exclusion pattern to suppress it. Read these hints to decide whether
to fix the issue or suppress it via config.

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
- **CI integration (GHA)**: Default `github-actions` writes the job summary and rich stdout; use `--format sarif` for Code Scanning upload. Docker: `-e GITHUB_ACTIONS -e GITHUB_STEP_SUMMARY`

## References

- `references/rules.md` — All rule IDs, severities, fix support, and categories
- `references/fix-mode.md` — Auto-fix commands, flags, and configuration
- `references/configuration.md` — Full seiton.yaml schema and common patterns
