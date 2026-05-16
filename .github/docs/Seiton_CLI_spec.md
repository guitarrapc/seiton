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
5. Deterministic output: given identical inputs, CLI output bytes must be identical.

---

## 1. Commands

### 1.1 Default (root) — `seiton [FILES...] [--fix]`

Lint one or more GitHub Actions YAML files (workflow files and action metadata files). This is the primary user-facing operation.

When `--fix` is specified, the root command switches to fix mode: runs lint, then applies all available fix payloads to the source files in place.

- If `--dry-run` is given, prints unified diffs to stdout without modifying files.
- If `--check` is given, exits with a non-zero code when any fixable diagnostic exists (does not apply fixes).
- Network-assisted pin remediation is activated when `fix.pinning.enable-network: true` or `fix.images.enable-network: true` is set in config (or via `--enable-pin-network` / `--enable-image-network` flags).

When no `FILES` are given, discovers all `*.yml` / `*.yaml` files under `.github/workflows/` relative to the current working directory.

When `--include-actions` is specified, no-arg discovery also includes `*.yml` / `*.yaml` files under `.github/actions/`.

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

Useful in CI jobs that maintain `.github/seiton.yaml` to catch configuration drift before lint runs.

### 1.5 `seiton rules`

List all available lint rules and their effective enabled/disabled status.

```
seiton rules [--config PATH] [--format text|json]
```

- `--config`: Explicit config file path. Auto-discovered if omitted.
- `--format`: Output format (`text` or `json`). Defaults to `text`. Also resolved from `SEITON_FORMAT` env var. SARIF is not supported and returns exit code 2.

Resolves configuration (if available) and reports each rule's status:
- Whether it is enabled or disabled
- Whether it is local or online
- The rule's default severity (`error`, `warning`, or `mixed`)
- Whether the rule supports auto-fix
- Which document kinds it supports (workflow, action, or both)
- The reason for its current state (default, config, opt-in)

Exit codes:
- `0`: Success (rule list printed).
- `2`: Invalid options (e.g. `--format sarif`).
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

---

## 2. Flags

All flags apply to the default root command unless otherwise noted.

### 2.1 Input Flags

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--config` | `-c` | `string` | (auto-discovery) | Explicit config file path. If specified, that file is used exclusively. If omitted, Seiton auto-discovers `.github/seiton.yaml`, `.github/seiton.yml`, `seiton.yaml`, `seiton.yml` (nearest directory first, then parent directories). |
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
| `--check` | | `bool` | `false` | Exit non-zero if fixable diagnostics remain after filtering; do not apply fixes. |
| `--enable-pin-network` | | `bool` | `false` | Force-enable network access for action SHA resolution. When omitted, the effective value comes from `fix.pinning.enable-network` in config. |
| `--enable-image-network` | | `bool` | `false` | Force-enable network access for container image digest resolution. When omitted, the effective value comes from `fix.images.enable-network` in config. |

Operational rule:

- `--dry-run`, `--check`, `--enable-pin-network`, and `--enable-image-network` are valid only when `--fix` is active.
- Network-assisted remediation is active when **either** the CLI flag is passed **or** the corresponding config key is `true`. The CLI flag is a force-enable override — it cannot disable a config-enabled setting.

### 2.4 Init Flags (init subcommand)

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--output` | `-o` | `string` | `.github/seiton.yaml` | Output path for the generated config file. |
| `--force` | `-f` | `bool` | `false` | Overwrite existing config file. |

### 2.5 Output Flags

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--format` | | `text\|json\|sarif` | `text` | Output format for diagnostics. |
| `--oneline` | | `bool` | `false` | Emit one diagnostic per line (text format only). |
| `--color` | | `auto\|always\|never` | `auto` | Color output control. `auto` enables color when stdout is not a TTY or CI is detected. |
| `--no-color` | | `bool` | `false` | Alias for `--color=never`. |
| `--verbose` | | `bool` | `false` | Enable verbose progress output to stderr. |

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

All CLI flags can alternatively be set via environment variables. Flag takes precedence over environment variable.

| Environment Variable | Equivalent Flag | Notes |
|---|---|---|
| `SEITON_CONFIG` | `--config` | Config file path. |
| `SEITON_FORMAT` | `--format` | Output format (`text`, `json`, `sarif`). |
| `SEITON_NO_COLOR` | `--no-color` | Any non-empty value disables color. |
| `NO_COLOR` | `--no-color` | Standard `NO_COLOR` convention honored as fallback after `SEITON_NO_COLOR`. |
| `SEITON_GITHUB_TOKEN` | (internal) | GitHub API token for network-assisted operations. Checked before `GITHUB_TOKEN`. Hardcoded resolution order; not configurable via config file. |
| `GITHUB_TOKEN` | (internal) | GitHub API token fallback. |

Token resolution order (`SEITON_GITHUB_TOKEN` → `GITHUB_TOKEN`) is a hardcoded constant in the CLI layer. This order is identical to the order defined in `Seiton_Linter_csharp_spec.md` §4.5.3 and is not configurable via the config file.

### 3.1 CI Environment Auto-Detection

When `CI=true` (standard GitHub Actions environment variable):

- `--color` defaults to `never` unless explicitly overridden.
- Verbose progress output is suppressed unless `--verbose` is explicitly set.

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
2. Otherwise, run discovery (starting at current working directory, walking parent directories upward):
   - `.github/seiton.yaml`
   - `.github/seiton.yml`
   - `seiton.yaml`
   - `seiton.yml`
3. If no config file is found, use built-in defaults.

An empty config file is valid and equivalent to built-in defaults.

Operational clarification:

- `check` / `fix` both follow the same resolution order above.
- If a config file is discovered and valid, it is applied even when `--config` is omitted.

### 4.3 Flag-to-Config Mapping

| CLI flag / env var | Config field | Merge semantics |
|---|---|---|
| `--ignore` | `ignore-patterns` | Additive merge with config-file patterns |
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

1. Locate the nearest `.github/workflows/` directory by walking up from the current working directory.
2. Collect all files matching `*.yml` and `*.yaml` under that directory recursively.
3. Sort collected paths deterministically (lexicographic, ordinal comparison) before passing to the lint engine.

Default auto-discovery scope remains workflow-first (`.github/workflows/`).

When `--include-actions` is enabled, discovery additionally includes `.github/actions/` from the same nearest repository root search.

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
- Caret length (`^`) is derived from `TextRange.EndColumn - TextRange.StartColumn`; minimum 1.
- When source is unavailable (for example stdin without a path, or JSON/SARIF formats), the gutter line `|` is emitted without snippet.

Structure:

```
<severity>[<rule-id>]: <message>
  --> <file>:<line>:<col>
     |
<line> | <source text>
     | <leading spaces><carets>
     |
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

JSON array to stdout. Each element is one diagnostic.

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

Each diagnostic maps to a SARIF `result` under a `run` with tool identity `seiton`.

Rule metadata (id, name, help URI) is emitted per-rule in `tool.driver.rules`.

---

## 7. Exit Codes

| Code | Meaning |
|---|---|
| `0` | Success — no diagnostics emitted at or above the effective minimum severity. |
| `1` | Lint issues found — at least one diagnostic was emitted. |
| `2` | Invalid CLI options — argument parsing failed or unsupported option combination. |
| `3` | Fatal error — config file error, I/O failure, or internal engine failure. |

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
