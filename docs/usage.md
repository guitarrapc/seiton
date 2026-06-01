# Usage

This page describes how to use `seiton` locally and in CI/CD.

## Current CLI Help

```shell
$ seiton --help
Usage: [command] [arguments...] [options...] [-h|--help] [--version]

Lint workflow files by default, or apply fixes when --fix is specified.

Arguments:
  [0] <string[]>     Workflow files or directories to lint. Auto-discovers .github/workflows/ if omitted.

Options:
  -c, --config <string?>       Path to config file. Auto-discovered from .github/seiton.yaml if omitted. [Default: null]
  --stdin-filename <string>    Filename used when reading from stdin (-). [Default: @"<stdin>"]
  --ignore <string[]?>         Substring patterns for messages to ignore (case-insensitive). [Default: null]
  --min-severity <string?>     Minimum severity to report: error | warning | info. [Default: null]
  --format <OutputFormat>      Output format: text | json | sarif | github-actions. [Default: Text; github-actions on GHA]
  --oneline                    Print each diagnostic on a single line.
  --color <ColorMode>          Color mode: auto | always | never. [Default: Auto]
  --no-color                   Disable color output (overrides --color).
  --verbose                    Print progress information to stderr.
  --fix                        Enable fix mode on the root command.
  --dry-run                    Print unified diff without modifying files (requires --fix).
  --show-diff                  Apply fixes and print unified diff (requires --fix; --dry-run takes precedence).
  --check                      Exit non-zero if fixable diagnostics remain after filtering, without applying fixes (requires --fix).
  --enable-pin-network         Allow network requests to resolve action SHA pins (requires --fix).
  --enable-image-network       Allow network requests to resolve container image digests (requires --fix).
  --include-actions            When no FILES are provided, include .github/actions/ in auto-discovery.

Commands:
  check              Lint workflow files.
  init               Generate a starter seiton config file.
  rules              List all available lint rules and their effective status.
  validate-config    Validate the seiton config file.
  version            Show version and runtime information.
```

The examples below clarify the current behavior where fixes are enabled with `--fix` on the root command. There is no separate `fix` subcommand.

---

## Basic Usage
With no arguments, `seiton` discovers and lints all `*.yml` / `*.yaml` files under `.github/workflows/` relative to the current working directory:

```sh
seiton
```

To lint composite actions under `.github/actions/` as well, pass `--include-actions` explicitly:

```sh
seiton --include-actions
```

Pass explicit file paths to lint specific files:

```sh
seiton .github/workflows/ci.yml .github/workflows/release.yml
```

Pass a directory to expand all `*.yml` / `*.yaml` files within it:

```sh
seiton .github/workflows/
```

Read from stdin with `-`. Use `--stdin-filename` to supply a filename for diagnostic messages:

```sh
cat .github/workflows/ci.yml | seiton --stdin-filename ci.yml -
```

---

## Commands

### seiton

Lint one or more GitHub Actions YAML files. This is the primary user-facing operation.

```sh
seiton [FILES...] [FLAGS]
```

Identical to the default command in check mode. Provided for scripting clarity.

```sh
seiton check [FILES...] [FLAGS]
```

### seiton --fix

To apply auto-fixes, use the root command with `--fix`.

> **Docker:** `--fix` requires a writable mount (omit `:ro`). `--dry-run` and `--check` work with `:ro`.

```sh
seiton --fix [FILES...] [FLAGS]
```

Use `--dry-run` to preview diffs without modifying files:

```sh
seiton --fix --dry-run
```

Recommended workflow: run `--fix --dry-run` first to review changes, then apply with `--fix`. To apply fixes and print the diff in one step (e.g. local development), use `--show-diff`:

```sh
seiton --fix --show-diff
```

When both `--dry-run` and `--show-diff` are given, `--dry-run` takes precedence (files are not modified).

Use `--check` to exit non-zero if any fixable diagnostic remains after filtering (for example, after `--min-severity`) without applying fixes:

```sh
seiton --fix --check
```

Use `--enable-pin-network` to allow network requests for resolving action SHA pins, and `--enable-image-network` for container image digest resolution. These are disabled by default to avoid unexpected network access.

```sh
seiton --fix --enable-pin-network --enable-image-network
```

After fixes are applied, a summary is printed to stderr showing per-file fix counts and remaining issues:

```
Fixed 6 of 7 issues in 2 files (1 remaining)

| File        | Fixed | Remaining |
|-------------|------:|----------:|
| ci.yml      |     4 |         0 |
| release.yml |     2 |         1 |
1 error remains in 1 file
```

### seiton init

Generate a starter config file at `.github/seiton.yaml`:

```sh
seiton init
```

**Recommended setup flow** for a new repository:

```sh
seiton init                      # 1. create .github/seiton.yaml
seiton validate-config           # 2. validate YAML and rule IDs
seiton --verbose                 # 3. lint and confirm config on stderr
```

In a **nested clone** inside a monorepo, pass `-c` explicitly so the parent repo's config is not picked up — see [Configuration: Nested repositories](configuration.md#nested-repositories-and-monorepos).

Specify a custom output path:

```sh
seiton init --output seiton.yaml
```

Overwrite an existing config file:

```sh
seiton init --force
```

### seiton rules

List all available lint rules and their effective enabled/disabled status:

```sh
seiton rules
```

Use `--config` to see how a specific config file affects rule states:

```sh
seiton rules --config .github/seiton.yaml
```

Output as JSON for programmatic consumption:

```sh
seiton rules --format json
```

Example text output:

```
Rule                                     Enabled   Type     Severity   Fix   Document   Reason
---------------------------------------------------------------------------------------------------------
job-structure                            yes       local    error      no    both       default
template-injection                       yes       local    error      yes   both       default
unpinned-uses                            yes       local    mixed      yes   both       default
concurrency-limits                       no        local    warning    no    workflow   opt-in (not configured)
known-vulnerable-actions                 no        online   error      no    workflow   opt-in (not configured)
...redacted for brevity...

61 rules total (56 enabled, 5 disabled)

To enable an opt-in rule, add to .github/seiton.yaml:
  rules:
    <rule-id>:
      enabled: true

Online rules use the GitHub API. Set GITHUB_TOKEN (or SEITON_GITHUB_TOKEN) to avoid rate limits.
```

Columns:

- **Rule** — Rule ID (the identifier used in config files and inline directives).
- **Enabled** — Whether the rule is active (`yes`) or inactive (`no`).
- **Type** — `local` (offline) or `online` (requires network).
- **Severity** — Default diagnostic severity: `error`, `warning`, or `mixed` (rule emits multiple severities depending on the condition; `info` can also occur in some cases).
- **Fix** — Whether the rule supports auto-fix (`yes` or `no`).
- **Document** — Which file types the rule applies to: `workflow`, `action`, or `both`.
- **Reason** — Why the rule has its current state: `default`, `config (enabled)`, `config (disabled)`, or `opt-in (not configured)`.

### seiton validate-config

Validate the resolved config file. Useful in CI jobs that maintain `.github/seiton.yaml`:

```sh
seiton validate-config
```

Use `--verbose` to inspect config resolution and quick validation stats (parse time, enabled rules, exclusions):

```sh
seiton validate-config --verbose
```

### seiton version

Print the version, build metadata, and target platform:

```sh
seiton version
```

### seiton install

Install agent skill files for coding agents (Claude Code, GitHub Copilot, Cursor, etc.) into the workspace:

```sh
seiton install --skills
```

Install for a specific target platform:

```sh
# Claude Code (default)
seiton install --skills --target claude

# GitHub Copilot
seiton install --skills --target copilot

# Cursor
seiton install --skills --target cursor
```

Override the output directory:

```sh
seiton install --skills --output path/to/custom/dir
```

Overwrite existing skill files:

```sh
seiton install --skills --force
```

Install a CI workflow template (`.github/workflows/seiton.yml`):

```sh
seiton install --ci
```

Install both skill files and CI workflow at once:

```sh
seiton install --skills --ci
```

Installed skill files include a `SKILL.md` (agent instruction manifest) and `references/` directory with detailed rule, fix-mode, and configuration documentation that agents can consult. The CI workflow template runs Seiton in Docker on pull requests and pushes with the default **`github-actions`** output (job log + job summary). A commented optional job shows how to enable SARIF upload for GitHub Code Scanning.

---

## Flags

### Input Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `--config`, `-c` | `string` | (auto-discover) | Explicit config file path. |
| `--stdin-filename` | `string` | `<stdin>` | Filename used for diagnostics when reading from stdin. |
| `--include-actions` | `bool` | `false` | Expand no-arg discovery to also include `.github/actions/`. |

### Lint Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `--ignore` | `string[]` | (none) | Case-insensitive substring patterns for messages to ignore. Repeatable. |
| `--min-severity` | `error\|warning\|info` | (none) | Suppress diagnostics below this severity. |
| `--fix` | `bool` | `false` | Run in fix mode on the root command. |

### Fix Flags

These flags are valid only when `--fix` is enabled on the root command.

| Flag | Type | Default | Description |
|---|---|---|---|
| `--dry-run` | `bool` | `false` | Print unified diffs to stdout without modifying files. |
| `--show-diff` | `bool` | `false` | Apply fixes and print unified diffs to stdout. Ignored when `--dry-run` or `--check` is active. |
| `--check` | `bool` | `false` | Exit non-zero if fixable diagnostics remain after filtering; do not apply fixes. |
| `--enable-pin-network` | `bool` | `false` | Enable network access for action SHA pinning resolution. |
| `--enable-image-network` | `bool` | `false` | Enable network access for container image digest resolution. |

### Output Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `--format` | `text\|json\|sarif\|github-actions` | `text` (see below) | Output format for diagnostics. |
| `--oneline` | `bool` | `false` | Emit one diagnostic per line (`text` format only). |
| `--color` | `auto\|always\|never` | `auto` | Color output control. |
| `--no-color` | `bool` | `false` | Alias for `--color=never`. |
| `--verbose` | `bool` | `false` | Enable verbose progress output to stderr. |

---

## Environment Variables

The following environment variables are recognized. A flag always takes precedence over its corresponding environment variable.

| Environment Variable | Equivalent | Description |
|---|---|---|
| `SEITON_CONFIG` | `--config` | Config file path. |
| `SEITON_FORMAT` | `--format` | Output format (`text`, `json`, `sarif`, `github-actions`). |
| `SEITON_NO_COLOR` | `--no-color` | Any non-empty value disables color. |
| `NO_COLOR` | `--no-color` | Standard `NO_COLOR` convention (fallback). |
| `SEITON_GITHUB_TOKEN` | (internal) | GitHub API token for online rules and network-assisted remediation. Takes priority over `GITHUB_TOKEN`. |
| `GITHUB_TOKEN` | (internal) | GitHub API token fallback for online rules and network-assisted remediation. |

When `CI` is set, automatic color detection behaves as `never`.

When `GITHUB_ACTIONS` is set and you do not pass an explicit `--format` (or `SEITON_FORMAT`), the default output format is **`github-actions`** instead of `text`. Other CI systems keep `text`. Use `--format text` to force the classic flat log on GitHub Actions.

---

## Output Formats

### GitHub Actions (`github-actions`)

Optimized for [GitHub Actions](https://docs.github.com/en/actions): readable diagnostics on stdout and a Markdown block on the job summary tab. This is the **default on GitHub Actions runners** when `--format` is omitted.

**Job log (stdout)** — same rich layout as **text** (snippets and help lines). Color is off. Per-file log folding via `::group::` is planned; see `.github/docs/plan_format.md` phase 2.

**Job summary** — when `GITHUB_STEP_SUMMARY` points to a **writable** file (normal on `ubuntu-latest` and other GitHub-hosted runners), Seiton **appends** UTF-8 Markdown with LF line endings:

- A `## Seiton` heading once per process run (fix + check summaries share one block).
- The same count lines and tables as the stderr summary (§6.4 in `Seiton_CLI_spec.md`), including metadata suffixes such as `(N excluded, M suppressed)` when applicable.
- A blank line before the block when the summary file already has content (does not overwrite other tools’ summaries).

If the variable is unset, blank, or not writable, the full summary is written to **stderr** instead (same content as local `text`, without the `## Seiton` heading).

**stderr** — progress (`--verbose`), configuration errors, init hints, and all `hint:` lines stay on stderr. They are never copied into the job summary.

`--oneline` is not supported with this format (exit code `2`).

`text`, `json`, and `sarif` **ignore** `GITHUB_STEP_SUMMARY` and always print the summary on stderr.

```yaml
# Simplest CI step — no --format flag needed on GitHub Actions
- name: Run seiton
  run: seiton
```

Force classic flat output: `seiton --format text`.

See `.github/docs/Seiton_CLI_spec.md` §6.5 and `.github/docs/plan_format.md` for the full contract.

### Text (default locally)

Human-readable output includes the severity/rule header plus a source excerpt:

```
error[template-injection]: "github.event.pull_request.title" is potentially untrusted. avoid using it directly in inline scripts. instead, pass it through an environment variable. see https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions#good-practices-for-mitigating-script-injection-attacks for more details
  --> .github/workflows/ci.yml:7:32
    |
   7 |       - run: 'echo "Title: ${{ github.event.pull_request.title }}"'
    |                                ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
    |
```

Use `--oneline` to produce one line per diagnostic (useful for `grep`/`awk` pipelines), for example:

```
.github/workflows/ci.yml:7:32: error [template-injection] "github.event.pull_request.title" is potentially untrusted. avoid using it directly in inline scripts. instead, pass it through an environment variable. see https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions#good-practices-for-mitigating-script-injection-attacks for more details
```

### JSON

Structured JSON array for programmatic consumption. **Diagnostics are written to stdout only.** Summary lines (`N errors, M warnings in K file(s)`) and all `--verbose` progress go to **stderr**, so piping stdout yields valid JSON.

```sh
# Bash: diagnostics only
seiton --format json 2>/dev/null

# Bash: keep summary on the terminal
seiton --format json
```

**PowerShell** merges stderr into the success stream by default, which breaks `ConvertFrom-Json`. Redirect stderr away from the pipeline:

```powershell
# Diagnostics only (valid JSON array)
$diagnostics = seiton --format json 2>$null | ConvertFrom-Json
```

```json
[
  {
    "file": ".github/workflows/ci.yml",
    "line": 7,
    "col": 32,
    "severity": "error",
    "ruleId": "template-injection",
    "message": "\"github.event.pull_request.title\" is potentially untrusted. avoid using it directly in inline scripts. instead, pass it through an environment variable. see https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions#good-practices-for-mitigating-script-injection-attacks for more details",
    "fixable": false
  }
]
```

### SARIF

[SARIF](https://sarifweb.azurewebsites.net/) output for GitHub Advanced Security code scanning:

```sh
seiton --format sarif > seiton.sarif
```

---

## Suppress Errors

### Command-Line

Use `--ignore` with one or more case-insensitive substrings to suppress diagnostics whose messages match:

```sh
# Suppress all runner-label warnings
seiton --ignore 'runner-label'

# Suppress a specific message
seiton --ignore 'label "self-hosted" is unknown'

# Combine multiple patterns
seiton --ignore 'runner-label' --ignore 'unpinned-uses'
```

### Inline Directives

Add `# seiton: disable-next-line <rule-id>` in your workflow file to suppress the next line:

```yaml
steps:
  # seiton: disable-next-line unpinned-uses
  - uses: actions/checkout@v6
```

Suppress for an entire job:

```yaml
# seiton: disable-job build unpinned-uses,job-permissions-required
jobs:
  build:
    ...
```

Suppress for the entire file (place at the top):

```yaml
# seiton: disable-file dangerous-triggers
```

Multiple rule IDs are comma-separated. See [Configuration](configuration.md) for file-level exclusions.

---

## Including Action Metadata Files

By default, no-arg discovery covers only `.github/workflows/`. To also include `.github/actions/`:

```sh
seiton --include-actions
```

Action metadata files are always accepted when passed explicitly:

```sh
seiton action.yml .github/actions/my-action/action.yml
```

---

## Docker

Official container images are published to GHCR for `linux/amd64` and `linux/arm64`.

Available tags include:

- `ghcr.io/guitarrapc/seiton:latest`
- `ghcr.io/guitarrapc/seiton:0.9.19`
- `ghcr.io/guitarrapc/seiton:v0.9.19`

To confirm the image works:

```sh
docker run --rm ghcr.io/guitarrapc/seiton:v0.9.19 version
```

Lint all workflow files (read-only mount):

```sh
docker run --rm -v "$PWD:/repo:ro" ghcr.io/guitarrapc/seiton:v0.9.19
```

Lint a specific file:

```sh
docker run --rm -v "$PWD:/repo:ro" ghcr.io/guitarrapc/seiton:v0.9.19 .github/workflows/ci.yml
```

Apply fixes (omit `:ro` — writable mount is required):

```sh
docker run --rm -v "$PWD:/repo" ghcr.io/guitarrapc/seiton:v0.9.19 --fix
```

> `--fix --dry-run` and `--fix --check` do not write files, so `:ro` is fine for those.

---

## GitHub Actions

For GitHub Actions, the Docker image is the simplest way to get started. It avoids a separate download step, does not depend on `bash`, and keeps the job setup minimal. If you prefer a shell-based setup without Docker, use the download script from [Installation](installation.md#download-script).

### Simplest setup: native binary or Docker (job summary + GHA default format)

On GitHub Actions, `seiton` defaults to `--format github-actions`: rich diagnostics on stdout and a Markdown summary on the run page (`GITHUB_STEP_SUMMARY`). No extra flags are required.

```yaml
- name: Run seiton
  run: ${{ steps.get-seiton.outputs.executable }}
  shell: bash
```

Docker on a GitHub-hosted runner also picks up `GITHUB_ACTIONS` when the job sets it (default for `runs-on: ubuntu-latest`):

```yaml
- name: Run seiton in Docker
  run: docker run --rm -v "$PWD:/repo:ro" -e GITHUB_ACTIONS -e GITHUB_STEP_SUMMARY ghcr.io/guitarrapc/seiton:latest
```

Pass `-e GITHUB_STEP_SUMMARY` so the container can write the job summary (the host path is forwarded via the env var GitHub sets on the runner).

### Code Scanning: Docker with SARIF

Lint-only (read-only mount). For in-place `--fix`, use the download script or omit `:ro`.

```yaml
name: Lint GitHub Actions

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

permissions: {}

jobs:
  seiton:
    permissions:
      security-events: write
      contents: read
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@v6
        with:
          persist-credentials: false

      - name: Run seiton in Docker
        run: docker run --rm -v "$PWD:/repo:ro" ghcr.io/guitarrapc/seiton:v0.9.19 --format sarif > seiton.sarif

      - name: Upload SARIF
        uses: github/codeql-action/upload-sarif@ce28f5bb42d3534e5d0f3a320ca0b28ee32a72d0 # v3
        if: always()
        with:
          sarif_file: seiton.sarif
```

Use this when you want the least amount of setup. Prefer the download script instead if your environment does not allow Docker or if you want to run the native binary directly in shell steps.

### Shell-based setup: download script

The download script is a good fit when you do not want to depend on Docker. On GitHub Actions it writes the absolute downloaded binary path to the `executable` step output, so later steps can invoke it directly. Ensure `shell: bash` is set for steps running the script, since Windows runners default to `pwsh`.

```yaml
name: Lint GitHub Actions

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

permissions: {}

jobs:
  seiton:
    permissions:
      security-events: write
      contents: read
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@v6
        with:
          persist-credentials: false

      - name: Download seiton
        id: get-seiton
        run: curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/download.sh | bash
        shell: bash

      - name: Run seiton
        run: ${{ steps.get-seiton.outputs.executable }} --format sarif > seiton.sarif
        shell: bash

      - name: Upload SARIF
        uses: github/codeql-action/upload-sarif@ce28f5bb42d3534e5d0f3a320ca0b28ee32a72d0 # v3
        if: always()
        with:
          sarif_file: seiton.sarif
```

- If you need a specific version or download directory, pass `--version` and `--dir` to the script as described in [Installation](installation.md#download-script).

---

## Exit Codes

| Code | Meaning |
|---|---|
| `0` | No warnings or errors found (info diagnostics may exist). |
| `1` | One or more warnings, errors, or fixable diagnostics found. |
| `2` | Invalid command-line options. |
| `3` | Fatal error (for example config parse failure or unreadable file). |

---

## Troubleshooting

### YAML parse error on `run:` steps containing `: `

A common mistake in GitHub Actions workflows is writing a `run:` value that contains a colon followed by a space (`: `). YAML interprets this as a mapping value indicator, causing a fatal parse error.

**Broken:**

```yaml
- run: echo "Title: ${{ github.event.pull_request.title }}"
```

**Fix — use a block scalar:**

```yaml
- run: |
    echo "Title: ${{ github.event.pull_request.title }}"
```

**Fix — use quotes:**

```yaml
- run: 'echo "Title: ${{ github.event.pull_request.title }}"'
```

Seiton reports an explanatory hint alongside the YAML parse error to help identify this issue.
