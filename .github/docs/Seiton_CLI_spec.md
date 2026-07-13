# Seiton CLI Specification

> This document is language-neutral — it specifies WHAT the CLI does, not HOW a specific implementation achieves it. Defines the CLI contract for command execution, flag parsing, and output processing. For C#-specific implementation details, see `Seiton_CLI_csharp_spec.md`, For Go-specific implementation details, see `Seiton_CLI_go_spec.md`. Parser and linter behavior are specified in `Seiton_Parser_spec.md` and `Seiton_Linter_spec.md`.

> **Cross-document rule**: This spec is the source of truth. When revised, also review and update `Seiton_CLI_csharp_spec.md`, `Seiton_CLI_go_spec.md` for consistency.

---

## 0. Preamble

### 0.1 Scope

This document specifies:

- Command surface (commands, flags, environment variables)
- Input discovery and file routing
- Output format contracts
- Exit code contract
- Config bridge semantics (mapping CLI flags → lint config)

Out of scope:

- Core lint or parser logic (see `Seiton_Linter_spec.md`)
- Rule behavior (see `Seiton_Linter_spec.md`)
- Data update pipeline (`Seiton.Update`)
- Language-specific implementation details (see `Seiton_CLI_csharp_spec.md`)

### 0.2 Design Principles

1. The CLI is a thin wrapper. No lint/parse/fix logic lives here.
2. All user-supplied flags and environment variables are translated into a lint config before passing to the core engine.
3. **Config-first**: every CLI flag that has a corresponding config-file key must use the config value as its effective default. The CLI flag only *overrides* the config value — it never ignores it. If a feature is enabled in the config file, it must be active even when the CLI flag is not explicitly passed.
4. A single config path flows through the lint engine — CLI options that overlap with config keys override or supplement the loaded config in a defined precedence order.
5. Deterministic diagnostics and summaries: given identical inputs, stdout diagnostics and stderr summaries must be identical. Best-effort verbose progress lines may interleave in parallel mode.

---

## 1. Commands

### 1.1 Default (root) — `seiton [FILES...] [--fix]`

Lint one or more GitHub Actions YAML files (workflow files and action metadata files). This is the primary user-facing operation.

When `--fix` is specified, the root command switches to fix mode: runs lint, then applies all available fix payloads to the source files in place.

- If `--dry-run` is given, prints unified diffs to stdout without modifying files.
- If `--show-diff` is given (with `--fix` and without `--dry-run` or `--check`), applies fixes and also prints unified diffs to stdout (same format as `--dry-run`). When both `--dry-run` and `--show-diff` are given, `--dry-run` takes precedence.
- If `--check` is given, exits with a non-zero code when any fixable diagnostic exists (does not apply fixes). When both `--check` and `--dry-run` are given, `--check` takes precedence (no diffs are printed, no fixes are applied).
- Network-assisted pin remediation is activated when `fix.pinning.enable-network: true` or `fix.images.enable-network: true` is set in config (or via `--enable-pin-network` / `--enable-image-network` flags).

When no `FILES` are given, discovers all `*.yml` / `*.yaml` files under `<cwd>/.github/workflows/` (current working directory only).

When `--include-actions` is specified, no-arg discovery also includes `*.yml` / `*.yaml` files under `<cwd>/.github/actions/`.

Action metadata files are accepted when explicitly passed via `FILES` (for example `action.yml`, `action.yaml`, `.github/actions/<name>/action.yml`, `.github/actions/<name>/action.yaml`).

`-` (hyphen) as a file argument reads from stdin. Requires `--stdin-filename` to give the input a meaningful path for diagnostics. Stdin is not supported in fix mode.

### 1.2 `seiton check [FILES...]`

Identical behavior to the default root command in check mode (`seiton` without `--fix`). Provided for explicit subcommand users and scripting clarity.

### 1.3 `seiton init`

Generate a starter config file at `.github/seiton.yaml` (or at the path given by `--output`).

- Fails if file already exists, unless `--force` is given.
- Emits a minimal commented YAML with common sections pre-populated.

### 1.4 `seiton validate-config`

Parse and validate the resolved config file.

- If no config file is found (neither explicit nor discovered), exits with code 3 (fatal error).
- If the config file exists but has validation errors, reports them and exits with code 1.
- If valid, prints a success message and exits with code 0.
- `--verbose` is supported and emits to stderr:
  - resolved config source/path (`verbose: config: ...`)
  - parse elapsed time (`verbose: parse: ... ms`)
  - effective enabled-rule count (`verbose: rules: ... enabled`)
  - configured exclusion entry count (`verbose: exclusions: ... entry(s)`)
  - job-scoped exclusion cross-check stats (`verbose: job-id-check: N workflow file(s) scanned for M job-scoped exclusion(s)`)

When the config contains job-scoped `exclusions` (`jobs:` array), discovered workflow files matching each `file` pattern are parsed to validate job IDs **before** lint runs. Unknown job IDs are reported as errors on the config file path. Unmatched `file` patterns produce warnings only.

Useful in CI jobs that maintain `.github/seiton.yaml` to catch configuration drift before lint runs.

### 1.5 `seiton rules`

List all available lint rules and their effective enabled/disabled status.

```
seiton rules [--config PATH] [--format text|json]
```

- `--config`: Explicit config file path. Auto-discovered if omitted.
- `--format`: Output format (`text` or `json`). Defaults to `text`. Also resolved from `SEITON_FORMAT` env var. `sarif` and `github-actions` are not supported and return exit code 2.

Resolves configuration (if available) and reports each rule's status:
- Whether it is enabled or disabled
- Whether it is local or online
- The rule's default severity (`error`, `warning`, or `mixed`)
- Whether the rule supports auto-fix
- Which document kinds it supports (workflow, action, or both)
- The reason for its current state (default, config, opt-in)

Exit codes:
- `0`: Success (rule list printed).
- `2`: Invalid options (e.g. `--format sarif` or `--format github-actions`).
- `3`: Fatal error (e.g. config file not found or validation failure).

### 1.6 `seiton version`

Print version and target platform to stdout.

Output structure:

```
seiton <semver>
built with <runtime-description>, <platform-identifier>
```

The exact content of each placeholder is implementation-defined. Examples:

- `built with .NET 10.0.0, win-x64` (C# NativeAOT build)
- `built with go1.24.0, linux/amd64` (Go build)

### 1.7 `seiton install`

Install agent skill files and other workspace assets.

```
seiton install --skills [--target claude|copilot|cursor] [--ci] [--output PATH] [--force]
```

- `--skills`: Install agent skill files to the workspace.
- `--ci`: Install a CI workflow template to `.github/workflows/seiton.yml`. The template runs Seiton in Docker on `ubuntu-24.04` with `GITHUB_ACTIONS` and `GITHUB_STEP_SUMMARY` so the default **`github-actions`** output (rich stdout + job summary) applies without an explicit `--format` flag. An optional **commented** `code-scanning` job shows `--format sarif` and `upload-sarif` for GitHub Code Scanning adopters.
- `--target`: Target agent platform (`claude`, `copilot`, or `cursor`). Defaults to `claude`. Applies only to `--skills`.
  - `claude` → `.claude/skills/seiton/`
  - `copilot` → `.github/instructions/seiton/`
  - `cursor` → `.cursor/rules/seiton/`
- `--output`: Override the output path. When only `--skills` is active, overrides the skill destination directory. When only `--ci` is active, overrides the workflow file path. When both `--skills` and `--ci` are active, `--output` applies to `--skills` only; `--ci` uses the default path.
- `--force`: Overwrite existing files if the destination already exists.

When neither `--skills` nor `--ci` is specified, the command prints usage help and exits 0.

Both `--skills` and `--ci` can be specified together; each asset is installed independently.

Skill files and CI templates are embedded in the CLI binary and copied to the workspace on install. No network access is required.

Exit codes:
- `0`: Success (files installed) or help displayed (when no action flag is given).
- `2`: Invalid options (unknown `--target` value).
- `3`: Fatal error (destination already exists without `--force`, or I/O failure).

---

## 2. Flags

All flags apply to the default root command unless otherwise noted.

### 2.1 Input Flags

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--config` | `-c` | `string` | (auto-discovery) | Explicit config file path. If specified, that file is used exclusively. If omitted, Seiton auto-discovers `.github/seiton.yaml`, `.github/seiton.yml`, `seiton.yaml`, `seiton.yml` under `cwd` only. |
| `--stdin-filename` | | `string` | `<stdin>` | Filename used for diagnostics when reading from stdin (`-`). |
| `--include-actions` | | `bool` | `false` | Expand no-arg discovery scope to include `.github/actions/` in addition to `.github/workflows/`. |

### 2.2 Lint Flags (root check/fix)

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--ignore` | | `string[]` | (none) | Substring patterns matched against diagnostic messages to suppress (case-insensitive). Repeatable. |
| `--min-severity` | | `error\|warning\|info` | (none) | Suppress diagnostics below this severity. |
| `--fix` | | `bool` | `false` | Run the root command in fix mode. |

### 2.3 Fix Flags (root with `--fix`)

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--dry-run` | | `bool` | `false` | Print unified diffs to stdout; do not modify files. |
| `--show-diff` | | `bool` | `false` | Apply fixes and print unified diffs to stdout (same format as `--dry-run`). Ignored when `--dry-run` or `--check` is active. |
| `--check` | | `bool` | `false` | Exit non-zero if fixable diagnostics remain after filtering; do not apply fixes. |
| `--enable-pin-network` | | `bool` | `false` | Force-enable network access for action SHA resolution. When omitted, the effective value comes from `fix.pinning.enable-network` in config. |
| `--enable-image-network` | | `bool` | `false` | Force-enable network access for container image digest resolution. When omitted, the effective value comes from `fix.images.enable-network` in config. |

Operational rule:

- `--dry-run`, `--show-diff`, `--check`, `--enable-pin-network`, and `--enable-image-network` are valid only when `--fix` is active.
- Network-assisted remediation is active when **either** the CLI flag is passed **or** the corresponding config key is `true`. The CLI flag is a force-enable override — it cannot disable a config-enabled setting.

### 2.4 Init Flags (init subcommand)

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--output` | `-o` | `string` | `.github/seiton.yaml` | Output path for the generated config file. |
| `--force` | `-f` | `bool` | `false` | Overwrite existing config file. |

### 2.5 Output Flags

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--format` | | `text\|json\|sarif\|github-actions\|flow-json\|flow-mermaid` | `text` (see §3.1.1) | Output format. The flow formats (§6.6) are check-only, emit workflow structure instead of diagnostics, and are rejected by fix mode (exit code 2). |
| `--oneline` | | `bool` | `false` | Emit one diagnostic per line (`text` and `github-actions`). |
| `--color` | | `auto\|always\|never` | `auto` | Color output control. `auto` enables color when stdout is not a TTY or CI is detected. |
| `--no-color` | | `bool` | `false` | Alias for `--color=never`. |
| `--verbose` | `-v` | `bool` | `false` | Enable summary-level verbose output to stderr (config, discovery, rules, timing, suppression totals). |
| `--skip-agentic-workflows` | | `bool` | `false` | Skip workflow files whose first lines contain `# gh-aw-metadata:` (Agentic Workflow). Also configurable via `discovery.skip-agentic-workflows`. |

**Verbose levels** (parsed from raw argv before framework binding):

| Invocation | Level | stderr output |
|---|---|---|
| (default) | off | none |
| `-v` / `--verbose` | summary | config path, discovery counts, rules enabled/disabled, total timing, suppression aggregate |
| `-vv` | files | summary level **plus** per-file `checking`/`fixing`, per-file timing, per-file fix counts |

`-vv` is stripped from argv before the CLI framework parses options so it does not conflict with `-v`.

### 2.6 Unknown Option Suggestions

When an unrecognized long option is provided, `seiton` reports the unknown argument and, when a close match exists, prints a `Did you mean` suggestion.

Matching behavior:

- Candidate matching is based on normalized long option names (`-` differences are ignored).
- Closest option is selected by edit distance with conservative thresholds to reduce noisy suggestions.
- All unknown long options in the invocation are reported (not only the first one).

When at least one actionable suggestion is found, `seiton` also emits a synthesized command hint line:

```
Try: seiton <suggested-options...>
```

Command hint behavior:

- Preserves argument order from the original invocation.
- Replaces unknown long options only when a safe suggestion exists.
- Keeps values attached to value-taking options (for example `--config`, `--color`).
- Drops unknown options that have no suggestion.

---

## 3. Environment Variables

Certain CLI flags have environment variable equivalents (listed below). When both a flag and its env var are set, the flag takes precedence.

| Environment Variable | Equivalent Flag | Notes |
|---|---|---|
| `SEITON_CONFIG` | `--config` | Config file path. |
| `SEITON_FORMAT` | `--format` | Output format (`text`, `json`, `sarif`, `github-actions`, `flow-json`, `flow-mermaid`). |
| `SEITON_NO_COLOR` | `--no-color` | Any non-empty value disables color. |
| `NO_COLOR` | `--no-color` | Standard `NO_COLOR` convention honored as fallback after `SEITON_NO_COLOR`. |
| `SEITON_GITHUB_TOKEN` | (internal) | GitHub API token for network-assisted operations. Checked before `GITHUB_TOKEN`. Hardcoded resolution order; not configurable via config file. |
| `GITHUB_TOKEN` | (internal) | GitHub API token fallback. |

Token resolution order (`SEITON_GITHUB_TOKEN` → `GITHUB_TOKEN`) is a hardcoded constant in the CLI layer. This order is identical to the order defined in `Seiton_Linter_csharp_spec.md` §4.5.3 and is not configurable via the config file.

### 3.1 CI Environment Auto-Detection

When the `CI` environment variable is set to any non-empty value (e.g. `CI=true` in GitHub Actions):

- `--color` defaults to `never` unless explicitly overridden.
- Verbose progress output is suppressed unless `--verbose` is explicitly set.

#### 3.1.1 GitHub Actions Default Output Format

When **all** of the following hold:

1. The user did not pass `--format` / `-f` on the CLI (the built-in default `text` applies). An explicit `--format text` or `-f text` counts as user-specified and keeps `text` on GitHub Actions runners.
2. `SEITON_FORMAT` does not override the format (or resolves to `text`).
3. `GITHUB_ACTIONS` is set to any non-empty value.

Then the effective output format for `check` and root lint/fix invocations is **`github-actions`** instead of `text`.

This targets GitHub Actions runners only. Other CI systems that set `CI` but not `GITHUB_ACTIONS` keep `text` as the default. Users can force flat logs with `--format text` or `SEITON_FORMAT=text`.

---

## 4. Config Bridge: CLI Flags → Lint Config

The CLI never passes raw flags directly to the lint engine. All inputs are normalized into a lint config structure first.

### 4.1 Resolution Order

For each configurable setting, the effective value is resolved in this order (highest to lowest precedence):

1. CLI flag (explicit, current invocation)
2. Environment variable
3. Config file value (loaded from resolved config path)
4. Built-in default

This ensures users can always override config-file defaults at the command line without editing the file.

**Implication for boolean enable-flags**: For flags like `--enable-pin-network` that map to config boolean keys (`fix.pinning.enable-network`), the effective value is `CLI flag || config value`. The CLI flag can only force-enable; it cannot force-disable a config-enabled setting. To disable, the user must change the config file.

### 4.2 Config Path Resolution

1. If `--config` / `SEITON_CONFIG` is set, use that path exclusively. Error if file does not exist.
2. Otherwise, run discovery under the current working directory only (`<cwd>/`), checking in order:
   - `.github/seiton.yaml`
   - `.github/seiton.yml`
   - `seiton.yaml`
   - `seiton.yml`
3. If no config file is found under `cwd`, use built-in defaults.

In `-v` / `--verbose` mode, the resolved config is logged to stderr:

```
verbose: config: /repo/nested/.github/seiton.yaml (discovered under cwd /repo/nested)
verbose: config: /repo/.github/seiton.yaml (from --config)
verbose: config: /repo/.github/seiton.yaml (from SEITON_CONFIG)
verbose: config: (none, using defaults) (searched under cwd /repo/nested)
```

An empty config file is valid and equivalent to built-in defaults.

Operational clarification:

- `check` / `fix` both follow the same resolution order above.
- If a config file is discovered and valid, it is applied even when `--config` is omitted.

### 4.3 Flag-to-Config Mapping

| CLI flag / env var | Config field | Merge semantics |
|---|---|---|
| `--ignore` | (post-lint filter) | CLI-only post-lint filter applied after lint. Not merged with config-file `ignore-patterns`. |
| `--min-severity` | (post-lint filter) | Applied after lint; not stored in config |
| `--verbose` | (CLI-only) | No config key; sets verbose when passed |
| `--enable-pin-network` | `fix.pinning.enable-network` | `CLI \|\| config` — force-enable override |
| `--enable-image-network` | `fix.images.enable-network` | `CLI \|\| config` — force-enable override |
| `SEITON_GITHUB_TOKEN` / `GITHUB_TOKEN` | (runtime token) | Resolved at runtime; not stored in config file |

All other behavior comes from the resolved config loaded from the config file.

**Design rule**: When adding a new CLI flag that maps to a config key, always compute the effective value as `cliFlag || configValue` (for booleans) or `cliValue ?? configValue ?? default` (for nullable types). The CLI must never silently ignore a config-file setting.

---

## 5. Input Discovery

When no `FILES` arguments are given to `check` / `fix`:

1. Resolve the current working directory (`cwd`) to an absolute path.
2. If `<cwd>/.github/workflows/` exists, collect all files matching `*.yml` and `*.yaml` under that directory recursively.
3. When `--include-actions` is enabled and `<cwd>/.github/actions/` exists, collect all matching files under that directory recursively as well.
4. Sort collected paths deterministically (lexicographic, ordinal comparison) before passing to the lint engine.

Only `<cwd>/.github/workflows/` and `<cwd>/.github/actions/` are considered. To lint files outside `cwd`, pass explicit `FILES` paths or run `seiton` from the intended repository root.

When `<cwd>/.github/actions/` exists and `--include-actions` is not set, `check` emits a one-line stderr notice **before** file discovery begins (regardless of `--verbose` or eventual diagnostic count):

```
notice: composite actions are not included; re-run with --include-actions
```

`notice:` lines are informational only; they do not affect exit code. They are not written to the job summary.

**Lessons learned (nested CI):** When a job checks out a parent repository at the workspace root and a child repository into a subdirectory (`path:` checkout), running `seiton` with `working-directory` set to the child must lint only the child's `.github/` tree. Parent-directory walks caused unintended lint of sibling checkouts (for example parent `.github/actions/` mixed with child `.github/workflows/`).

Local action and reusable-workflow references inside workflow YAML (`uses: ./.github/actions/foo`, `uses: ../other/action.yml`) are resolved separately by the lint engine when analyzing each file; that reference resolution is not part of input discovery.

Action metadata files are always accepted when explicitly passed in `FILES`.

When `FILES` are given:

- Each argument is treated as a file path.
- `-` reads from stdin; `--stdin-filename` is used as the file path in diagnostics.
- Directories are expanded to all `*.yml` / `*.yaml` files within them recursively.
- Non-existent paths produce a fatal error.

File-kind routing for explicit `FILES`:

1. Build a candidate kind from path hints.
2. Confirm final kind from YAML top-level structure.
3. Route to matching parser/linter pipeline.

Normative action path hints (candidate stage):

- basename is `action.yml` or `action.yaml`
- path matches `.github/actions/<name>/action.yml` or `.github/actions/<name>/action.yaml`

Path hints are intentionally fast and non-authoritative. Final routing is structure-confirmed.

---

## 6. Output Format

### 6.1 `text` (default)

Human-readable diagnostic output to stdout. Rich multi-line format (Rust-style) by default; compact single-line format with `--oneline`.

#### 6.1.1 Default rich format

Each diagnostic is rendered as a multi-line block showing the problem header, source location arrow, source snippet with underline caret, and optional help text.

File paths in diagnostic output are **relative to the process working directory** at output time, using forward slashes (for example `.github/workflows/build.yml`). Both absolute and relative path inputs are normalized against the working directory before relativization. This keeps logs and copied output shareable without machine-specific absolute paths. Paths that cannot be expressed relative to the working directory (for example cross-drive paths on Windows) fall back to an absolute path. Null, empty, or whitespace file paths are displayed as `<unknown>`. Other sentinel paths such as `<stdin>` are preserved as-is.

```
error[job-permissions-required]: job "build" omits explicit permissions declaration
  --> .github/workflows/build.yml:12:5
     |
  12 |     runs-on: ubuntu-latest
     |     ^^^^^^^^^^^^^^^^^^^^^^
     |
   = help: add an explicit `permissions:` block to this job

warning[unpinned-uses]: 'actions/checkout@v6' is not pinned to a full-length commit SHA
  --> .github/workflows/build.yml:8:11
     |
   8 |         uses: actions/checkout@v6
     |               ^^^^^^^^^^^^^^^^^^^
     |
```

Multi-line diagnostic spans are rendered with `/ ... |___^` fencing:

```
error[template-injection]: potentially unsafe use of github.event data
  --> .github/workflows/build.yml:15:18
     |
  15 | /     run: echo "${{ github.event.pull_request.title }}"
  16 | |       env:
     | |____________^ untrusted data used in run step
     |
   = help: map the value to an env variable and use the shell variable instead
```

Snippet rendering behavior:

- Source bytes are extracted from the original UTF-8 input per file path key.
- Line numbers in the gutter are right-aligned to the width of the last shown line number.
- Diagnostic columns are 1-based byte offsets within the source line (same basis as the parser).
- Caret padding and length use terminal display width: tabs advance to the next 4-column stop; East Asian wide characters count as width 2; ASCII counts as width 1.
- Caret length (`^`) spans the display width of bytes from `StartColumn` through `EndColumn` (inclusive byte range); minimum 1 caret.
- When source is unavailable (for example stdin without a path, or JSON/SARIF formats), the gutter line `|` is emitted without snippet.

Structure-aware context rendering (`text` / `github-actions` rich format only):

- When a YAML structure path can be resolved, the source excerpt is replaced by a single gutter block showing the minimal ancestor chain from `jobs:` / `runs:` down to the diagnostic line (unrelated siblings omitted with `...`), with carets on the target line. There is no separate `= structure:` section and no duplicate source line.
- When structure context cannot be built, the formatter falls back to the plain single-line (or multi-line) source snippet.
- Shown only when the diagnostic is attributable to workflow/action structure (message path prefix such as `jobs.'…'` / `steps[n]`, optional `structure-path` diagnostic metadata, or ancestor chain includes `jobs:` / `steps:` / `runs:`).
- When a structure path is present (parsed from the message or `structure-path` metadata), the snippet resolves the target YAML node from that path so the correct step/job context is shown even if the diagnostic line/column points elsewhere.
- In `--fix` mode, structure snippets use the post-fix file bytes in `sourceMap` when fixes were applied, so remaining diagnostics align with the modified YAML.
- Not emitted for `--oneline`, `json`, or `sarif` output.

Structure:

```
<severity>[<rule-id>]: <message>
  --> <file>:<line>:<col>
<line> | <ancestor yaml>       (structure context when available)
     | ...
<line> | <target yaml>
     | <leading spaces><carets>
   = help: <help text>     (only when a help annotation is present)
```

Color coding (when color is enabled):

- Severity header (`error[...]`, `warning[...]`) → severity color + bold
- Message → bold
- `-->` arrow and gutter `|` → blue
- Line number → blue
- Caret `^` characters → severity color
- `help:` annotation → dim label + normal text

#### 6.1.2 `--oneline` compact format

With `--oneline`, each diagnostic is collapsed to a single line. Suitable for machine parsing, editor integrations, and environments where multi-line output is inconvenient.

```
.github/workflows/build.yml:12:5: error [job-permissions-required] job "build" omits explicit permissions declaration
```

Structure:

```
<file>:<line>:<col>: <severity> [<rule-id>] <message>
```

Color coding (when color is enabled):

- `error` → red
- `warning` → yellow
- `info` → cyan
- Rule ID → dimmed/gray
- File path and position → bold

### 6.2 `json`

JSON array to stdout. Each element is one diagnostic. Summary lines and `--verbose` output go to stderr — pipe stdout only for valid JSON (for example `seiton --format json 2>/dev/null` in Bash, or `2>$null` in PowerShell before `ConvertFrom-Json`).

The `file` field uses the same relative-path rules as §6.1.1 (working-directory-relative, forward slashes, absolute fallback when relativeization is impossible).

Schema (non-normative):

```json
[
  {
    "file": ".github/workflows/build.yml",
    "line": 12,
    "col": 5,
    "severity": "error",
    "ruleId": "job-permissions-required",
    "message": "job \"build\" omits explicit permissions declaration",
    "fixable": false
  }
]
```

### 6.3 `sarif`

SARIF 2.1.0 JSON output to stdout. Suitable for GitHub Code Scanning upload.
Output is pretty-printed JSON (indented) for readability in code review and issue sharing.

`$schema` uses the official OASIS SARIF 2.1.0 schema URL:

- `https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json`

Outputs should be pass official validation.

- `https://sarifweb.azurewebsites.net/Validation`

Each diagnostic maps to a SARIF `result` under a `run` with tool identity `seiton`.

`runs[].results[].locations[].physicalLocation.artifactLocation.uri` is emitted as a URI reference (slash-separated, percent-encoded when needed):

- When the file path can be expressed relative to the process working directory, `uri` is a URI-safe relative reference (slash-separated, percent-encoded when needed) and `uriBaseId` is `%WORKING_DIR%`.
- When at least one relative artifact is emitted, `runs[].originalUriBaseIds["%WORKING_DIR%"].uri` carries the absolute `file:///...` URI of the working directory (with trailing slash), allowing SARIF consumers to resolve relative artifact URIs.
- Cross-drive or otherwise non-relativeizable filesystem paths fall back to absolute `file:///...` URIs without `uriBaseId`.
- Unknown paths (null, empty, whitespace, or the literal `<unknown>` sentinel) are emitted as `file:///unknown`.
- The stdin sentinel (`<stdin>`) is emitted as a URI-safe percent-encoded reference (for example `%3Cstdin%3E`) without `uriBaseId` and does not trigger `originalUriBaseIds`. The `-` sentinel is emitted literally.
- URI-like strings that are not valid absolute URIs are emitted literally without `uriBaseId`.
- Invalid filesystem paths that cannot be normalized fall back to `file:///unknown` (SARIF) rather than aborting output generation.

Rule metadata (`id`, `helpUri`) is emitted per-rule in `tool.driver.rules`.

`runs[].tool.driver.rules[].helpUri` points to rule-specific documentation anchors in `docs/rules.md` using the rule id (`.../docs/rules.md#<rule-id>`). For parser-origin diagnostics (internal `RuleId: null`, displayed as `syntax-check`), `helpUri` falls back to the general usage guide (`.../docs/usage.md`).

`runs[].tool.driver.version` is always emitted, sourced from assembly informational version (with build metadata suffix trimmed when present).

### 6.4 Summary Output (stderr or job summary)

After diagnostics are emitted to stdout, a summary block (this section) is always produced.

| Format | `GITHUB_STEP_SUMMARY` | Summary destination |
|---|---|---|
| `github-actions` | set and writable | Append full summary block (§6.5.2) |
| `github-actions` | unset or not writable | stderr |
| `text`, `json`, `sarif` | (ignored) | stderr |

`hint:` lines defined in this section are never written to the job summary; they go to stderr in all formats.

When written to stderr, a summary line is always emitted:

```
<N> errors, <N> warnings, <N> infos in <N> file(s)
```

When no diagnostics exist: `0 issues in <N> file(s)`.

When files were fully excluded (file-level exclusion with parse/lint skipped) or diagnostics were suppressed via config/inline directives, optional suffixes are appended (zero categories omitted):

```
0 issues in 123 files (2 excluded, 15 suppressed)
```

- `excluded` — files matched by a file-level exclusion (`rules` omitted, no `jobs` scope) where parse/lint was skipped.
- `suppressed` — diagnostics suppressed during lint via exclusions or inline directives (`SuppressionSummary.TotalSuppressed`).

Zero-count categories are omitted (e.g. `0 issues in 123 files` when both counts are zero).

When at least one diagnostic has a file path, a per-file breakdown is emitted as a markdown-style table, separated from the summary line by a blank line:

```
| File          | Errors | Warnings |
|---------------|-------:|---------:|
| ci.yml        |      3 |        2 |
| release.yml   |      1 |        0 |
```

- Column widths are dynamically computed from the longest file name.
- Numeric columns are right-aligned.
- Zero values are displayed explicitly (not omitted).
- When at least one info diagnostic exists in the per-file breakdown, an `Infos` column is also emitted.
- Files are sorted by total issue count descending, then by file name lexicographically.

When at least one diagnostic is present, a per-rule breakdown is emitted as a markdown-style table (top 10 rules by count), separated from the preceding output by a blank line. Parser diagnostics (`RuleId` is null internally) are grouped under the display rule ID `syntax-check`, matching the `error[syntax-check]:` label in diagnostic output and actionlint's `[syntax-check]` tag.

```
| Rule          | Count |
|---------------|------:|
| syntax-check  |     1 |
| unpinned-uses |     3 |
| template-injection |  2 |
```

- In normal check mode, the count column is labeled "Count".
- In fix/dry-run mode (when `isRemainMode` is true), the count column is labeled "Remaining" to reflect these are post-fix residual diagnostics.
- Column widths are dynamically computed to align values.
- Rules are sorted by count descending, then by rule ID lexicographically.
- At most **10** rules are shown in default (non-verbose) mode. When more than 10 distinct rule IDs are present, stderr also emits (not written to the job summary):

```
hint: re-run with --verbose for the full per-rule breakdown
```

In `-v` / `--verbose` mode, the same per-rule table is emitted with **no row limit** (all distinct rule IDs).

In `-v` / `--verbose` mode, rule activation metadata is emitted once per document kind seen in the run:

```
verbose: rules: <N> enabled, <M> disabled (workflow)
verbose: rules: <N> enabled, <M> disabled (action)
verbose: rules: disabled: <id1>, <id2>, ...   (only when M > 0)
```

`DisabledRuleCount` and `DisabledRuleIds` reflect config/opt-in disabled rules only. The `(workflow)` / `(action)` suffix is included because `ActiveRuleCount` varies by document kind.

Per-file timing summary (only at `-vv`) consolidates document kind, elapsed time, diagnostic count, and suppressed count:

```
verbose: <filepath>: workflow, 1.2 ms, 5 diagnostics, 2 suppressed
verbose: <filepath>: action, 0.8 ms, 3 diagnostics, 0 suppressed
```

Total timing is emitted at the end:

```
verbose: total: 3 file(s) checked in 4.5 ms
verbose: total: 3 file(s) processed, 2 modified in 450.0 ms
verbose: total: 3 file(s) processed, 2 would be modified in 450.0 ms   # --dry-run
```

In fix mode, the total line reports **processed** (input files handled) and **modified** (files whose YAML bytes changed) separately. These counts can differ when fixable issues remain but no content change was produced. In `--dry-run` mode, the modified count uses **would be modified** instead of **modified**.

When fix mode runs on at least one file with fixable issues but produces no content changes, a hint is emitted:

```
hint: no files modified (1 file processed; 1 fixable issue remains)
hint: no files modified (2 files processed; 3 fixable issues remain)
hint: no files would be modified (1 file processed; 1 fixable issue remains)   # --dry-run
hint: no files would be modified (2 files processed; 3 fixable issues remain)   # --dry-run
```

When no fixable issues were attempted, this hint is not emitted.

In fix mode, network timing is emitted per file when pins are resolved:

```
verbose: network: resolved 3 pin(s) for <filepath> in 320.0 ms
```

In parallel `-vv` mode, `verbose: checking <filepath>...` is best-effort progress output and may appear interleaved rather than in input order. Diagnostic output and summary output remain deterministic.

At `-v` (summary level), per-file `checking`/`fixing` lines are **not** emitted.

When no `--min-severity` is explicitly set, errors are zero, and warnings are non-zero, a hint line is emitted:

```
hint: use --min-severity error to treat warnings as non-blocking in CI
```

In `--fix` mode, when network-assisted flags are not enabled but relevant diagnostics exist (`unpinned-uses` or `unpinned-image`), a hint is emitted suggesting the appropriate `--enable-pin-network` / `--enable-image-network` flags.

In `--fix` mode (not `--dry-run`, not `--check`), when at least one fix is applied, a fix summary is emitted to stderr before the remaining diagnostic summary:

```
Fixed <fixed> of <found> issue(s) in <file-count> file(s) (<remaining> remaining)

| File        | Fixed | Remaining |
|-------------|------:|----------:|
| ci.yml      |     4 |         0 |
| release.yml |     2 |         1 |
<errors> error(s), <warnings> warning(s) remain in <affected-files> file(s)
```

In `--dry-run` mode, the table header uses "Would Fix" instead of "Fixed". In `--check` mode, the header uses "Fixable".

When the fix summary is emitted and at least one issue was fixed/fixable/would be fixed, but `--verbose` is not set, stderr also emits:

```
hint: re-run with --verbose for a per-rule fix breakdown
```

This hint is not written to the job summary.

In `-v` / `--verbose` mode, when the fix summary is emitted and at least one issue was fixed/fixable/would be fixed, a per-rule breakdown table is appended after the per-file table:

```
| Rule                         | Would Fix |
|------------------------------|----------:|
| if-expr-wrapper              |         4 |
| job-timeout-minutes-required |         3 |
```

- In `--dry-run` mode, the per-rule count column is labeled "Would Fix".
- In `--check` mode, the per-rule count column is labeled "Fixable" (counts are derived from diagnostics that carry a fix).
- In applied fix mode, the per-rule count column is labeled "Fixed".
- Rules are sorted by count descending, then by rule ID lexicographically.
- Column widths are dynamically computed to align values.

- The total summary line shows the relationship `found = fixed + remaining` explicitly ("Fixed X of Y issues").
- The total summary line appears first, followed by the per-file detail table.
- Column widths are dynamically computed from the longest file name.
- Numeric columns are right-aligned.
- Zero values are displayed explicitly (not omitted).
- Files are sorted by total count (fixed + remaining) descending, then by file name lexicographically.
- Per-file rows include all files that had fixes applied, plus unfixed files with remaining diagnostics (shown with fixed 0).
- `remaining` is the count of diagnostics still present for that file after ignore/severity filters.
- The "remain" summary line shows the severity breakdown of remaining diagnostics and the count of affected files.
- When no fixes are applied (all diagnostics are unfixable), the fix summary is not emitted and the standard diagnostic summary is used instead.
- When fixes are attempted but no file content changes, the fix summary is not emitted; a `hint: no files modified` (or `would be modified` in `--dry-run`) line explains the outcome.
- In `--check` mode, the remaining diagnostic summary uses standard wording ("in N files") rather than "remain" because no fixes were applied.
- When the effective format is `github-actions` and the job summary file is writable, fix summaries and remain summaries are appended there instead of stderr (see §6.5.2).

### 6.5 `github-actions`

Human-readable output optimized for [GitHub Actions](https://docs.github.com/en/actions) job logs and the job summary tab. Intended as the default on GitHub Actions runners (§3.1.1).

#### 6.5.1 Job log (stdout)

Diagnostics are written to **stdout** in file groups using GitHub workflow-command folding markers:

```text
::group::<file>
<diagnostics for file>
::endgroup::
```

Within each group, diagnostics use the same rich text structure as §6.1.1 (severity/rule header, source excerpt, help lines), or one-line form when `--oneline` is set. File paths in diagnostic bodies follow the same relative-path rules as §6.1.1. Group titles (`::group::...`) percent-encode `%`, `\r`, and `\n` per GitHub workflow-command rules so folding markers remain valid; diagnostic bodies also escape `%`, `\r`, and `\n`, and neutralize a leading `::` to prevent workflow-command injection.

- Color is never emitted for this format.
- `--oneline` is supported and changes only the diagnostic body format (group wrapping behavior is unchanged).

When no diagnostics are emitted, stdout carries no diagnostic lines (same as `text`).

#### 6.5.2 Job summary (`GITHUB_STEP_SUMMARY`)

After diagnostics, summary content (§6.4) is written as GitHub Flavored Markdown:

- When `GITHUB_STEP_SUMMARY` is set to a writable file path, the summary is **appended** to that file (with a leading blank line if the file already has content).
- The block starts with a `## Seiton` heading, followed by the same summary lines and markdown tables as §6.4 (counts, per-file breakdown, default top-10 per-rule breakdown, fix-mode tables).
- `hint:` lines from §6.4 are **not** copied to the job summary; they remain on stderr only.

When `GITHUB_STEP_SUMMARY` is unset or not writable, the full §6.4 summary is written to **stderr** only (same as `text` / `json` / `sarif`).

#### 6.5.3 stderr

Progress (`--verbose`), configuration errors, init hints, fix diffs (when format is non-text for diff routing), and `hint:` lines follow the same rules as other formats. Only the §6.4 summary block moves to the job summary file when available.

#### 6.5.4 Unsupported commands

`seiton rules` supports only `text` and `json`; `sarif`, `github-actions`, `flow-json`, and `flow-mermaid` are rejected with exit code `2`.

### 6.6 `flow-json` / `flow-mermaid` (flow formats)

The flow formats emit the **workflow structure** ("flow") instead of diagnostics. They exist so the execution flow of a workflow — the `needs` job DAG combined with per-job step sequences and `parallel` step boundaries — can be visualized without re-interpreting YAML outside the parser.

**WHY**: the Playground flow tab and the CLI must share one machine-readable contract built from the same parsed AST (single source of truth); Mermaid is a thin human-oriented projection of that same contract for pasting into GitHub Markdown.

Behavior common to both formats:

- Supported by `check` (and the root command without `--fix`) only. Fix mode rejects them with exit code `2` and a stderr message.
- Lint rules are not executed; no diagnostics are emitted. Exit code is `0` unless option/config errors occur.
- Each input file is parsed once; non-workflow documents (e.g. `action.yml`) are skipped.
- **Static matrices are expanded** into `combinations` (cross product of the dimension rows, then `exclude` subset removal, then `include` extend/append — approximating GitHub semantics, capped at 256 combinations). Matrices containing any dynamic `${{ }}` dimension or block are reported as declaration only (dimension names / dynamic-expression marker) with empty `combinations`.
- Reusable workflow jobs (`uses:` at job level) are opaque leaves carrying their `uses` ref; the referenced workflow is not loaded.
- `if` conditions are carried as raw expressions.
- `background: true` steps carry a `background` flag so intra-job concurrency (background fork, `wait`/`wait-all` join, `cancel`) can be reconstructed by consumers.
- Jobs and steps carry 1-based source line ranges (`line` / `endLine`, omitted when unknown) so consumers can map lint diagnostics onto flow nodes. Ranges of adjacent blocks may overlap on boundary lines; consumers should resolve overlaps by preferring the node with the greatest start line.
- `reducedNeeds` is `needs` after **transitive reduction** (an edge implied by another dependency's chain drops out, e.g. `a` leaves `needs: [a, b]` when `b` already depends on `a`) — matching how GitHub renders its workflow graph. Renderers should draw `reducedNeeds`; dependency semantics (hover closures, scheduling analysis) should use the full `needs`.
- Runtime settings are carried when declared: job `timeoutMinutes` / `permissions` (`["read-all"]` scalar form or `"scope: level"` entries; empty array = `{}` deny-all; omitted when undeclared) / `environment`, and step `timeoutMinutes` / `continueOnError` / `workingDirectory` (run steps) / `with` (uses steps, key-value object in document order). Dynamic `${{ }}` timeout expressions are omitted.
- Workflow execution context is carried when declared: `schedules` (cron entries of `on: schedule` with the seiton `timezone` extension; omitted when absent) and `concurrency` (`group` raw expression, `cancelInProgress`, seiton `queue` extension).
- Background steps carry `backgroundOutcome` (`"awaited"` when a later `wait` targeting the step or any `wait-all` joins it, `"cancelled"` when a later `cancel` cuts it, `"unawaited"` otherwise) computed over the job's later top-level steps — consumers must not re-derive this.

#### 6.6.1 `flow-json`

Single JSON document to stdout:

```json
{ "version": 1, "workflows": [ { "file", "name"?, "on": [],
  "schedules"?: [ { "cron", "timezone"? } ], "concurrency"?: { "group"?, "cancelInProgress", "queue"? },
  "jobs": [
  { "id", "name"?, "kind": "job|reusable", "line"?, "endLine"?, "if"?, "needs": [], "reducedNeeds": [], "runsOn": [], "uses"?,
    "timeoutMinutes"?, "permissions"?: [], "environment"?,
    "strategy"?: { "hasMatrix", "matrixKeys": [], "matrixIsExpression",
                   "combinations": [ { "<key>": "<value>", ... } ] },
    "steps": [ { "kind": "run|uses|parallel|wait|wait-all|cancel", "line"?, "endLine"?, "id"?, "name"?, "if"?,
                 "background"?, "backgroundOutcome"?, "timeoutMinutes"?, "continueOnError"?,
                 "run"?, "workingDirectory"?, "uses"?, "with"?: {},
                 "targets"?, "target"?, "steps"? } ] } ] } ] }
```

Absent optional fields are omitted. `steps` on a step appears only for `kind: "parallel"` (nested boundary children). This is the same contract served to the Playground flow tab by `PlaygroundFlowRunner`.

#### 6.6.2 `flow-mermaid`

Mermaid `flowchart LR` text to stdout: each job is a subgraph with chained step nodes, transitively reduced `needs` edges (`reducedNeeds`) connect job subgraphs, `parallel` boundaries are nested subgraphs whose children are intentionally unchained (simultaneous), and reusable jobs are subroutine nodes (`[[...]]`). Labels are single-line, quote-sanitized, and truncated.

The output is always **exactly one `flowchart` diagram** so it can be wrapped in a single Mermaid code block. Multiple input files become per-workflow wrapper subgraphs (`w0`, `w1`, … labeled with the file name) with prefixed node ids (`w0j0`, `w0j0n0`, …); a single input keeps the unprefixed shape (`j0`, `j0n0`, …). **WHY**: one Mermaid code block holds one diagram — a second `flowchart` keyword is a parse error, which is exactly what blank-line-separated diagrams caused when piped to a Markdown file.

#### 6.6.3 Design decisions and lessons learned

- **Derived data is computed in Seiton.Core and carried in the contract**, never re-derived per consumer: `reducedNeeds` (transitive reduction), `backgroundOutcome` (wait/wait-all/cancel join analysis), and `strategy.combinations` (static matrix expansion) exist so the CLI, Mermaid exporter, and Playground can never drift in interpretation.
- **The collector walks the Ref facade directly instead of registering an `IPass`** on `WorkflowVisitor`: the visitor flattens `parallel` children into a linear step stream with no boundary enter/exit hooks, but the flow contract needs the nesting. Model new AST consumers that need structure on the collector, not the visitor.
- **The flow JSON serializer is hand-written `Utf8JsonWriter` in Core** so the NativeAOT CLI and the trimmed WASM Playground emit byte-identical output without registering DTOs in a `JsonSerializerContext`.
- **Parser range caveats for consumers**: a job/step `Range` can spill onto the next sibling's first line (resolve overlaps by preferring the node with the greatest start line — see §6.6.1), and a `parallel` step's `Range` covers only its own header line (extend to the deepest descendant `endLine` when highlighting the whole boundary).
- **Matrix expansion approximates GitHub semantics** (cross product → `exclude` subset removal → `include` extend/append, cap 256). Include-only matrices (no dimension rows) must treat every `include` entry as its own combination — the "entry without dimension keys applies to all combinations" rule silently merges entries when there is no base product.
- **The `parallel` extension does not nest** (the parser rejects `parallel` inside `parallel`), so flow boundaries are always single-level even though the DTO models recursion.
- **Flow formats deliberately skip lint execution**: they are structural read-only contracts; exit code stays `0` so pipelines can generate diagrams without failing on lint findings.

---

## 7. Exit Codes

| Code | Meaning |
|---|---|
| `0` | Success — no diagnostics emitted at or above the effective minimum severity. |
| `1` | Lint issues found — at least one diagnostic was emitted. |
| `2` | Invalid CLI options — argument parsing failed or unsupported option combination. |
| `3` | Fatal error — config file error, I/O failure, or internal engine failure. |

By default, warnings alone still produce exit code `1`. In CI, pass `--min-severity error` when warnings should not fail the job (see runtime hint in §6.4).

For `--fix --check`: exits with `1` if any fixable diagnostic remains after post-lint filters such as `--min-severity` are applied.

---

## 8. Example Invocations

```sh
# Lint all workflows in current repository (auto-discovery)
seiton

# Lint workflows and local action metadata files in current repository
seiton --include-actions

# Lint specific files
seiton .github/workflows/build.yml .github/workflows/release.yml

# Lint action metadata file explicitly
seiton .github/actions/release/action.yml

# Lint from stdin
cat .github/workflows/build.yml | seiton - --stdin-filename build.yml

# Output JSON (e.g. for tooling)
seiton --format json

# Output SARIF for GitHub Code Scanning
seiton --format sarif > results.sarif

# On GitHub Actions (GITHUB_ACTIONS=true), default format is github-actions:
# rich stdout + job summary markdown (GITHUB_STEP_SUMMARY). Force flat text if needed:
seiton --format text

# Apply auto-fixes in place
seiton --fix

# Preview fixes without applying
seiton --fix --dry-run

# Check if fixable issues exist (CI gate)
seiton --fix --check

# Pin actions via network (uses GITHUB_TOKEN)
seiton --fix --enable-pin-network --enable-image-network

# Explicit check subcommand (identical to root without --fix)
seiton check

# Use explicit config
seiton --config .github/seiton-strict.yaml

# Generate starter config
seiton init

# Validate config only
seiton validate-config --config .github/seiton.yaml
```

### 8.1 Migration Note (Action Metadata Support)

- Default no-arg discovery behavior is unchanged: `seiton` auto-discovers only under `.github/workflows/` unless `--include-actions` is set.
- `--include-actions` opt-in expands no-arg discovery to include `.github/actions/`.
- Action metadata files are supported when explicitly passed via `FILES`.
- This compatibility policy prevents existing CI behavior regressions while expanding explicit input support.

---

## 9. Cross-Document Consistency

When this document is revised, review and update:

- `Seiton_CLI_csharp_spec.md` — C# implementation spec
- `Seiton_CLI_go_spec.md` — Go implementation spec
- `Seiton_Linter_spec.md` — if config bridge contract or discovery order changes
- `Seiton_Linter_csharp_spec.md` — if `LintConfig` bridge contract changes
