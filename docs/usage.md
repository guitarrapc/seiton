# Usage

```shell
$ seiton --help
Usage: [command] [arguments...] [options...] [-h|--help] [--version]

Lint workflow files by default, or apply fixes when --fix is specified.

Arguments:
  [0] <string[]?>    Workflow files or directories to lint. Auto-discovers .github/workflows/ if omitted.

Options:
  --config <string?>           Path to config file. Auto-discovered from .github/seiton.yaml if omitted. [Default: null]
  --stdin-filename <string>    Filename used when reading from stdin (-). [Default: @"<stdin>"]
  --ignore <string[]?>         Substring patterns for messages to ignore (case-insensitive). [Default: null]
  --min-severity <string?>     Minimum severity to report: error | warning | info. [Default: null]
  --format <OutputFormat>      Output format: text | json | sarif. [Default: Text]
  --oneline                    Print each diagnostic on a single line.
  --color <ColorMode>          Color mode: auto | always | never. [Default: Auto]
  --no-color                   Disable color output (overrides --color).
  --verbose                    Print progress information to stderr.
  --fix                        Enable fix mode for the root command (equivalent to the fix subcommand).
  --dry-run                    Print unified diff without modifying files (requires --fix).
  --check                      Exit non-zero if fixable diagnostics exist, without applying fixes (requires --fix).
  --enable-pin-network         Allow network requests to resolve action SHA pins (requires --fix).
  --enable-image-network       Allow network requests to resolve container image digests (requires --fix).
  --include-actions            When no FILES are provided, include .github/actions/ in auto-discovery.

Commands:
  check              Lint workflow files.
  init               Generate a starter seiton config file.
  validate-config    Validate the seiton config file.
  version            Show version and runtime information.
```


---

## Basic Usage


With no arguments, `seiton` discovers and lints all `*.yml` / `*.yaml` files under `.github/workflows/` relative to the current working directory:

```sh
seiton
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
cat .github/workflows/ci.yml | seiton - --stdin-filename ci.yml
```

---

## Commands

### `seiton` (default)

Lint one or more GitHub Actions YAML files. This is the primary user-facing operation.

```sh
seiton [FILES...] [FLAGS]
```

### `seiton check`

Identical to the default command in check mode. Provided for scripting clarity.

```sh
seiton check [FILES...] [FLAGS]
```

### `seiton fix`

Apply auto-fixes in place for all fixable diagnostics.

```sh
seiton fix [FILES...] [FLAGS]
```

Use `--dry-run` to preview diffs without modifying files:

```sh
seiton fix --dry-run
```

Use `--check` to exit non-zero if any fixable diagnostic exists (without applying fixes):

```sh
seiton fix --check
```

### `seiton init`

Generate a starter config file at `.github/seiton.yaml`:

```sh
seiton init
```

Specify a custom output path:

```sh
seiton init --output seiton.yaml
```

Overwrite an existing config file:

```sh
seiton init --force
```

### `seiton validate-config`

Validate the resolved config file. Useful in CI jobs that maintain `.github/seiton.yaml`:

```sh
seiton validate-config
```

### `seiton version`

Print the version, build metadata, and target platform:

```sh
seiton version
```

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
| `--ignore` | `string[]` | (none) | Regex patterns to suppress diagnostics by message. **`MatchTimeout`** = **2 s** per pattern (same cap as config `ignore-actions`); on timeout the diagnostic is **not** suppressed. Repeatable. |

| `--min-severity` | `error\|warning\|info` | (none) | Suppress diagnostics below this severity. |
| `--fix` | `bool` | `false` | Run in fix mode (equivalent to `seiton fix`). |

### Fix Flags

These flags are valid only in fix mode (`--fix` or the `fix` subcommand).

| Flag | Type | Default | Description |
|---|---|---|---|
| `--dry-run` | `bool` | `false` | Print unified diffs to stdout without modifying files. |
| `--check` | `bool` | `false` | Exit non-zero if fixable diagnostics exist; do not apply fixes. |
| `--enable-pin-network` | `bool` | `false` | Enable network access for action SHA pinning resolution. |
| `--enable-image-network` | `bool` | `false` | Enable network access for container image digest resolution. |

### Output Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `--format` | `text\|json\|sarif` | `text` | Output format for diagnostics. |
| `--oneline` | `bool` | `false` | Emit one diagnostic per line (text format only). |
| `--color` | `auto\|always\|never` | `auto` | Color output control. |
| `--no-color` | `bool` | `false` | Alias for `--color=never`. |
| `--verbose` | `bool` | `false` | Enable verbose progress output to stderr. |

---

## Environment Variables

All CLI flags can alternatively be set via environment variables. A flag always takes precedence over its corresponding environment variable.

| Environment Variable | Equivalent | Description |
|---|---|---|
| `SEITON_CONFIG` | `--config` | Config file path. |
| `SEITON_FORMAT` | `--format` | Output format (`text`, `json`, `sarif`). |
| `SEITON_NO_COLOR` | `--no-color` | Any non-empty value disables color. |
| `NO_COLOR` | `--no-color` | Standard `NO_COLOR` convention (fallback). |
| `SEITON_GITHUB_TOKEN` | (internal) | GitHub API token for network-assisted operations. Takes priority over `GITHUB_TOKEN`. |
| `GITHUB_TOKEN` | (internal) | GitHub API token fallback. |
| `SEITON_LOG_LEVEL` | `--verbose` | `debug`, `info`, `warn`, `error`. `debug` implies `--verbose`. |

When `CI=true` (standard GitHub Actions variable), color output defaults to `never` and progress indicators are suppressed.

---

## Output Formats

### Text (default)

Human-readable output with file path, line, column, severity, rule ID, and message:

```
.github/workflows/ci.yml:18:7: [error] template-injection: untrusted value 'github.event.pull_request.title' interpolated directly into run script
.github/workflows/ci.yml:42:5: [warning] unpinned-uses: 'actions/checkout@v6' is not pinned to a full commit SHA
```

Use `--oneline` to produce one line per diagnostic (useful for `grep`/`awk` pipelines).

### JSON

Structured JSON array for programmatic consumption:

```sh
seiton --format json
```

```json
[
  {
    "file": ".github/workflows/ci.yml",
    "line": 18,
    "column": 7,
    "severity": "error",
    "rule_id": "template-injection",
    "message": "untrusted value 'github.event.pull_request.title' interpolated directly into run script"
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

Use `--ignore` with a regular expression to suppress diagnostics whose messages match:

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

## Integration with GitHub Actions

Preparing `seiton` with the download script from [Installation](installation.md#download-script) is recommended for shell-based CI setup. On GitHub Actions the script writes the absolute downloaded binary path to the `executable` step output, so later steps can invoke it directly. Please ensure `shell: bash` is set for steps running the download script, since Windows runners default to `pwsh`.

### Using SARIF (recommended for public repos and GitHub Enterprise with Advanced Security)

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
      - uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683 # v4.2.2
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

Or download and run `seiton` in one step:

```yaml
- name: Run seiton
  run: |
    curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/download.sh | bash
    ./seiton --format text
  shell: bash
```

If you need a specific version or download directory, pass `--version` and `--dir` to the script as described in [Installation](installation.md#download-script).

## Docker

Official container images are published to GHCR for `linux/amd64` and `linux/arm64`.

Available tags include:

- `ghcr.io/guitarrapc/seiton:latest`
- `ghcr.io/guitarrapc/seiton:0.9.6`
- `ghcr.io/guitarrapc/seiton:v0.9.6`

To confirm the image works:

```sh
docker run --rm ghcr.io/guitarrapc/seiton:latest version
```

To lint all workflow files in the current repository, mount the repository read-only and pass the repository root path:

```sh
docker run --rm -v "$PWD:/repo:ro" ghcr.io/guitarrapc/seiton:latest /repo
```

To lint specific files, pass them as explicit arguments inside the mounted repository:

```sh
docker run --rm -v "$PWD:/repo:ro" ghcr.io/guitarrapc/seiton:latest /repo/.github/workflows/ci.yml /repo/action.yml
```

To use the Docker image on GitHub Actions:

```yaml
name: Lint GitHub Actions

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

permissions: {}

jobs:
    permissions:
      security-events: write
      contents: read
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683 # v4.2.2
        with:
          persist-credentials: false

      - name: Run seiton in Docker
        run: docker run --rm -v "$PWD:/repo:ro" ghcr.io/guitarrapc/seiton:latest /repo --format sarif > seiton.sarif

      - name: Upload SARIF
        uses: github/codeql-action/upload-sarif@ce28f5bb42d3534e5d0f3a320ca0b28ee32a72d0 # v3
        if: always()
        with:
          sarif_file: seiton.sarif
```

---

## Integration with pre-commit

Add Seiton to your `.pre-commit-config.yaml`:

```yaml
repos:
  - repo: https://github.com/guitarrapc/seiton
    rev: v1.0.0
    hooks:
      - id: seiton
```

---

## Exit Codes

| Code | Meaning |
|---|---|
| `0` | No errors found (warnings may exist). |
| `1` | One or more errors or fixable diagnostics found. |
| `2` | Fatal error (config parse failure, invalid arguments, unreadable file). |
