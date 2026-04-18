# Seiton CLI Specification

> Defines the user-facing CLI for `seiton`.
> Implementation wraps `Seiton.Core` and provides no lint/parse logic of its own.
> Parser and linter behavior are specified in `Seiton_Parser_spec.md` and `Seiton_Linter_spec.md`.

---

## 0. Preamble

### 0.1 Scope

This document specifies:

- CLI project structure and NativeAOT requirements
- Command surface (commands, flags, environment variables)
- Input discovery and file routing
- Output format contracts
- Exit code contract
- Internal config bridge (mapping CLI flags → `LintConfig`)

Out of scope:

- Core lint or parser logic
- Rule behavior (see `Seiton_Linter_spec.md`)
- Data update pipeline (`Seiton.Update`)

### 0.2 Project Identity

| Property | Value |
|---|---|
| Project name | `Seiton` |
| Assembly name | `seiton` |
| Namespace root | `Seiton` |
| Output type | `Exe` |
| Framework | `net10.0` |
| NativeAOT | enabled |
| CLI framework | ConsoleAppFramework |

### 0.3 Design Principles

1. The CLI is a thin wrapper. No lint/parse/fix logic lives here.
2. All user-supplied flags and environment variables are translated into `LintConfig` before passing to `Seiton.Core`.
3. A single config path flows through `LintEngine` — CLI options that overlap with config keys override or supplement the loaded config in a defined precedence order.
4. Deterministic output: given identical inputs, CLI output bytes must be identical.
5. NativeAOT: no reflection-based serialization at startup; all JSON/output paths must be AOT-compatible.

---

## 1. Project Structure

```
src/
  Seiton/
    Seiton.csproj
    Program.cs            # ConsoleApp bootstrap + root command wiring
    Commands/
      CheckCommand.cs     # seiton check
      FixCommand.cs       # seiton fix
      InitCommand.cs      # seiton init
      ValidateCommand.cs  # seiton validate-config
      VersionCommand.cs   # seiton version
    Output/
      DiagnosticFormatter.cs  # text / json / sarif formatters
      OutputFormat.cs         # OutputFormat enum
      ColorMode.cs            # ColorMode enum
    Config/
      CliConfigBridge.cs      # CLI flags → LintConfig translation
```

---

## 2. Commands

### 2.1 Default (root) — `seiton [FILES...]`

Lint one or more workflow files. This is the primary user-facing operation.

When no `FILES` are given, discovers all `*.yml` / `*.yaml` files under `.github/workflows/` relative to the current working directory.

`-` (hyphen) as a file argument reads from stdin. Requires `--stdin-filename` to give the input a meaningful path for diagnostics.

**Alias:** `seiton check [FILES...]` (explicit subcommand; identical behavior).

### 2.2 `seiton check [FILES...]`

Identical behavior to the default root command. Provided for explicit subcommand users and scripting clarity.

### 2.3 `seiton fix [FILES...]`

Apply auto-fixes to workflow files. Runs lint, then applies all available fix payloads to the source files in place.

- If `--dry-run` is given, prints unified diffs to stdout without modifying files.
- If `--check` is given, exits with a non-zero code when any fixable diagnostic exists (does not apply fixes).
- Network-assisted pin remediation is activated when `fix.pinning.enable-network: true` or `fix.images.enable-network: true` is set in config (or via `--enable-pin-network` / `--enable-image-network` flags).

### 2.4 `seiton init`

Generate a starter config file at `.github/seiton.yaml` (or at the path given by `--output`).

- Fails if file already exists, unless `--force` is given.
- Emits a minimal commented YAML with common sections pre-populated.

### 2.5 `seiton validate-config`

Parse and validate the resolved config file. Reports config errors and exits with code 1 if any are found.

Useful in CI jobs that maintain `.github/seiton.yaml` to catch configuration drift before lint runs.

### 2.6 `seiton version`

Print version, build metadata, and target platform to stdout.

```
seiton 1.0.0
built with .NET 10.0.0 (NativeAOT), linux/x64
```

---

## 3. Flags

All flags apply to the `check` / default-root command unless otherwise noted. Subcommand-specific flags are called out.

### 3.1 Input Flags

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--config` | `-c` | `string` | (discovery order) | Explicit config file path. If omitted, discovery order from `Seiton_Linter_spec.md` §5.10 applies. |
| `--stdin-filename` | | `string` | `<stdin>` | Filename used for diagnostics when reading from stdin (`-`). |

### 3.2 Lint Flags (check / fix)

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--ignore` | | `string[]` | (none) | Regex patterns matched against diagnostic messages to suppress. Repeatable. Maps to `LintConfig.IgnorePatterns`. |
| `--min-severity` | | `error\|warning\|info` | (none) | Suppress diagnostics below this severity. |

### 3.3 Fix Flags (fix subcommand)

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--dry-run` | | `bool` | `false` | Print unified diffs to stdout; do not modify files. |
| `--check` | | `bool` | `false` | Exit non-zero if fixable diagnostics exist; do not apply fixes. |
| `--enable-pin-network` | | `bool` | `false` | Override `fix.pinning.enable-network: true` for this run. |
| `--enable-image-network` | | `bool` | `false` | Override `fix.images.enable-network: true` for this run. |

### 3.4 Init Flags (init subcommand)

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--output` | `-o` | `string` | `.github/seiton.yaml` | Output path for the generated config file. |
| `--force` | `-f` | `bool` | `false` | Overwrite existing config file. |

### 3.5 Output Flags

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--format` | | `text\|json\|sarif` | `text` | Output format for diagnostics. |
| `--oneline` | | `bool` | `false` | Emit one diagnostic per line (text format only). |
| `--color` | | `auto\|always\|never` | `auto` | Color output control. `auto` enables color when stderr is a TTY. |
| `--no-color` | | `bool` | `false` | Alias for `--color=never`. |
| `--verbose` | | `bool` | `false` | Enable verbose progress output to stderr. |

---

## 4. Environment Variables

All CLI flags can alternatively be set via environment variables. Flag takes precedence over environment variable.

| Environment Variable | Equivalent Flag | Notes |
|---|---|---|
| `SEITON_CONFIG` | `--config` | Config file path. |
| `SEITON_FORMAT` | `--format` | Output format (`text`, `json`, `sarif`). |
| `SEITON_NO_COLOR` | `--no-color` | Any non-empty value disables color. |
| `NO_COLOR` | `--no-color` | Standard `NO_COLOR` convention honored as fallback after `SEITON_NO_COLOR`. |
| `SEITON_GITHUB_TOKEN` | (internal) | GitHub API token for network-assisted operations. Checked before `GITHUB_TOKEN`. Hardcoded resolution order; not configurable via config file. |
| `GITHUB_TOKEN` | (internal) | GitHub API token fallback. |
| `SEITON_LOG_LEVEL` | `--verbose` | `debug`, `info`, `warn`, `error`. `debug` implies `--verbose`. |

Token resolution order (`SEITON_GITHUB_TOKEN` → `GITHUB_TOKEN`) is a hardcoded constant in the CLI layer. This order is identical to the order defined in `Seiton_Linter_csharp_spec.md` §4.5.3 and is not configurable via the config file.

### 4.1 CI Environment Auto-Detection

When `CI=true` (standard GitHub Actions environment variable):

- `--color` defaults to `never` unless explicitly overridden.
- Progress indicators are suppressed unless `--verbose` is explicitly set.

---

## 5. Config Bridge: CLI Flags → LintConfig

The CLI never passes raw flags directly to `LintEngine`. All inputs are normalized into a `LintConfig` first.

### 5.1 Resolution Order

For each configurable setting, the effective value is resolved in this order (highest to lowest precedence):

1. CLI flag (explicit, current invocation)
2. Environment variable
3. Config file value (loaded from resolved config path)
4. Built-in `Seiton.Core` default

This ensures users can always override config-file defaults at the command line without editing the file.

### 5.2 Config Path Resolution

1. If `--config` / `SEITON_CONFIG` is set, use that path exclusively. Error if file does not exist.
2. Otherwise, run discovery as specified in `Seiton_Linter_spec.md` §5.10:
   - `.github/seiton.yaml`
   - `.github/seiton.yml`
   - `seiton.yaml`
   - `seiton.yml`
3. If no config file is found, use `LintConfig` built-in defaults.

An empty config file is valid and equivalent to built-in defaults.

### 5.3 Flag-to-Config Mapping

| CLI flag / env var | `LintConfig` field |
|---|---|
| `--ignore` | `LintConfig.IgnorePatterns` (additive merge with config-file patterns) |
| `--min-severity` | filter applied post-lint before output (not stored in `LintConfig`) |
| `--enable-pin-network` | `FixPinningConfig.EnableNetwork` (override to `true`) |
| `--enable-image-network` | `FixImagesConfig.EnableNetwork` (override to `true`) |
| `SEITON_GITHUB_TOKEN` / `GITHUB_TOKEN` | resolved by `GitHubNetworkConfig` token resolution (not stored in config file) |

All other behavior comes from the resolved `LintConfig` loaded from the config file.

---

## 6. Input Discovery

When no `FILES` arguments are given to `check` / `fix`:

1. Locate the nearest `.github/workflows/` directory by walking up from the current working directory.
2. Collect all files matching `*.yml` and `*.yaml` under that directory (non-recursive by default; recursive under subdirectories when they exist).
3. Sort collected paths deterministically (lexicographic, `/`-normalized) before passing to `LintEngine`.

When `FILES` are given:

- Each argument is treated as a file path.
- `-` reads from stdin; `--stdin-filename` is used as the file path in diagnostics.
- Directories are expanded to all `*.yml` / `*.yaml` files within them (non-recursive).
- Non-existent paths produce a fatal error.

---

## 7. Output Format

### 7.1 `text` (default)

Human-readable diagnostic output to stdout. One diagnostic per entry by default; compact single-line format with `--oneline`.

Default multi-line format:

```
.github/workflows/build.yml:12:5: error [job-permissions-required] job "build" omits explicit permissions declaration
```

```
<file>:<line>:<col>: <severity> [<rule-id>] <message>
```

Color coding (when color is enabled):

- `error` → red
- `warning` → yellow
- `info` → cyan
- Rule ID → dimmed/gray
- File path and position → bold

### 7.2 `json`

AOT-compatible JSON array to stdout. Each element is one diagnostic.

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

### 7.3 `sarif`

SARIF 2.1.0 JSON output to stdout. Suitable for GitHub Code Scanning upload.

Each diagnostic maps to a SARIF `result` under a `run` with tool identity `seiton`.

Rule metadata (id, name, help URI) is emitted per-rule in `tool.driver.rules`.

---

## 8. Exit Codes

| Code | Meaning |
|---|---|
| `0` | Success — no diagnostics emitted at or above the effective minimum severity. |
| `1` | Lint issues found — at least one diagnostic was emitted. |
| `2` | Invalid CLI options — argument parsing failed. |
| `3` | Fatal error — config file error, I/O failure, or internal engine failure. |

For `fix --check`: exits with `1` if any fixable diagnostic was found (even if no non-fixable issues exist).

---

## 9. NativeAOT Requirements

Because the CLI is published as NativeAOT:

- No `System.Reflection`-based serialization. JSON output must use source-generated `System.Text.Json` serializers with `[JsonSerializable]`.
- No `Assembly.Load` or plugin loading.
- `ConsoleAppFramework` source generator mode must be used (it supports NativeAOT via source generation).
- `Seiton.Core` must not introduce reflection-dependent code paths reachable from the CLI execution path.
- All AOT trim annotations (`[DynamicallyAccessedMembers]`) must be validated via `<TrimmerRootFile>` or `<PublishAot>true</PublishAot>` warnings during build.

`.csproj` properties required:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <PublishAot>true</PublishAot>
  <AllowUnsafeBlocks>false</AllowUnsafeBlocks>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

`InvariantGlobalization` is safe here because all rule matching uses UTF-8 byte span comparisons without locale-sensitive collation.

---

## 10. Shell Completion

`seiton completion <shell>` generates shell completion scripts.

Supported shells: `bash`, `zsh`, `fish`, `powershell`.

ConsoleAppFramework generates completion support for registered commands and flags.

---

## 11. Example Invocations

```sh
# Lint all workflows in current repository (auto-discovery)
seiton

# Lint specific files
seiton .github/workflows/build.yml .github/workflows/release.yml

# Lint from stdin
cat .github/workflows/build.yml | seiton - --stdin-filename build.yml

# Output JSON (e.g. for tooling)
seiton --format json

# Output SARIF for GitHub Code Scanning
seiton --format sarif > results.sarif

# Apply auto-fixes in place
seiton fix

# Preview fixes without applying
seiton fix --dry-run

# Check if fixable issues exist (CI gate)
seiton fix --check

# Pin actions via network (uses GITHUB_TOKEN)
seiton fix --enable-pin-network --enable-image-network

# Use explicit config
seiton --config .github/seiton-strict.yaml

# Generate starter config
seiton init

# Validate config only
seiton validate-config --config .github/seiton.yaml
```

---

## 12. Cross-Document Consistency

When this document is revised, review and update:

- `Docs/Seiton_spec.md` — component table entry for `seiton` CLI
- `Docs/Seiton_Linter_csharp_spec.md` — if `LintConfig` bridge contract changes
- `Docs/Seiton_Linter_spec.md` §5.10 — if config discovery order changes
