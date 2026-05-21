# Seiton CLI Go Implementation Specification

> Go implementation specification for the CLI contract defined in `.github/docs/Seiton_CLI_spec.md`. This document captures Go runtime structures and behavior for command dispatch, config bridge, output formatting, and build constraints. See `.github/docs/Seiton_CLI_csharp_spec.md` for the C# target. Both language specs share the same outline; only language-specific content differs. Parser and linter behavior are specified in `.github/docs/Seiton_Parser_go_spec.md` and `.github/docs/Seiton_Linter_go_spec.md`.

> **Cross-document synchronization rule**: `.github/docs/Seiton_CLI_spec.md` is the source of truth. When this Go spec is updated, also review and update `.github/docs/Seiton_CLI_spec.md` and `.github/docs/Seiton_CLI_csharp_spec.md` in the same PR/commit scope.

---

## 0. Go Preamble

### 0.1 Contract

This document defines the Go implementation contract for CLI behavior under `.github/docs/Seiton_CLI_spec.md`.

In scope:

- Go project layout and build configuration
- Command dispatch and flag parsing
- Config bridge resolution logic
- Output formatter implementation (text/json/SARIF)
- Parallelization strategy for multi-file linting
- Unknown option suggestion implementation

Out of scope:

- Core lint/parse logic (see `.github/docs/Seiton_Linter_go_spec.md`, `.github/docs/Seiton_Parser_go_spec.md`)
- Rule behavior and rule catalog
- Generated data pipeline (`Seiton.Update`)

### 0.2 Overview

The Seiton CLI Go implementation provides:

1. Static binary CLI wrapper over the `seiton` core package
2. Standard library `flag`-based command dispatch (no framework dependency)
3. Config bridge translating CLI flags/env vars into linter config
4. Multi-format diagnostic output (text/json/SARIF) via `encoding/json`
5. Parallel multi-file linting with deterministic aggregated output ordering
6. Pre-dispatch unknown option detection with edit-distance suggestions

### 0.3 Structure

Representative implementation surface:

| File/Area | Responsibility |
|---|---|
| `cmd/seiton/main.go` | Entry point, pre-dispatch unknown option check |
| `command.go` | Flag definitions, command dispatch, top-level orchestration |
| `check.go` | Lint orchestration (sequential + parallel paths) |
| `fix.go` | Fix orchestration with network-assisted pin/image resolution |
| `input_discovery.go` | File discovery (auto + explicit expansion) |
| `config_bridge.go` | Config resolution, env var reading, flag override application |
| `output.go` | Text/JSON/SARIF formatting |
| `option_suggester.go` | Unknown option detection and suggestion |

### 0.4 Design

1. Keep CLI as thin wrapper — no lint/parse logic in the CLI layer.
2. Use standard library where possible (`flag`, `encoding/json`, `os`); avoid external CLI framework dependencies.
3. Keep aggregated diagnostic and summary output deterministic regardless of parallelization; verbose progress lines may interleave.
4. Keep config resolution aligned with `.github/docs/Seiton_CLI_spec.md` §4 precedence order.

---

## 1. Project Identity

| Property | Value |
|---|---|
| Module path | `github.com/guitarrapc/seiton` |
| Binary name | `seiton` |
| Package root | `seiton` (single package) |
| CLI entry | `cmd/seiton/main.go` |
| Min Go version | `go1.24` |

The single-package layout follows the same pattern as `actionlint`, keeping all core + CLI code in one importable package with the binary entry point in `cmd/seiton/`.

---

## 2. Project Structure

```
cmd/
  seiton/
    main.go              # Entry point (pre-dispatch + Command.Main)
command.go               # Command struct, flag parsing, dispatch
check.go                 # seiton check / root lint orchestration
fix.go                   # seiton --fix orchestration
input_discovery.go       # File discovery logic
config_bridge.go         # CLI flags → LintConfig translation
output.go                # text / json / sarif formatters
option_suggester.go      # Unknown option detection and suggestion
exit_code.go             # Exit code constants
```

---

## 3. Build Configuration

Go builds produce a static binary by default. Build-time version injection uses `-ldflags`:

```sh
go build -ldflags "-s -w -X github.com/guitarrapc/seiton.version=1.2.3" ./cmd/seiton
```

| Build property | Value |
|---|---|
| `CGO_ENABLED` | `0` (pure Go, no C dependencies) |
| `-ldflags -s -w` | Strip debug symbols and DWARF for smaller binary |
| `-X seiton.version` | Inject version at build time |

Cross-compilation for all target platforms (linux/darwin/windows × amd64/arm64) is supported via `GOOS`/`GOARCH` without additional tooling.

---

## 4. Entry Point and Command Dispatch

### 4.1 Entry Point

```go
// cmd/seiton/main.go
package main

import (
    "os"

    "github.com/guitarrapc/seiton"
)

func main() {
    os.Exit((&seiton.Command{}).Main(os.Args[1:]))
}
```

### 4.2 Command Struct and Flag Parsing

The `Command` struct holds parsed options. Flags are defined with the standard library `flag` package. This follows the `actionlint` pattern — no external CLI framework.

```go
type Command struct {
    // Parsed from flags
    Files              []string
    Config             string
    StdinFilename      string
    Ignore             []string
    MinSeverity        string
    Format             string // "text", "json", "sarif"
    Oneline            bool
    Color              string // "auto", "always", "never"
    NoColor            bool
    Verbose            bool
    Fix                bool
    DryRun             bool
    Check              bool
    EnablePinNetwork   bool
    EnableImageNetwork bool
    IncludeActions     bool

    // Internal
    Stdout io.Writer
    Stderr io.Writer
}
```

### 4.3 Dispatch Logic

`Main` parses flags, validates combinations, and dispatches:

```go
func (cmd *Command) Main(args []string) int {
    // 1. Pre-dispatch unknown option suggestion
    if writeSuggestionsForUnknownOptions(args, cmd.Stderr) {
        return ExitInvalidOptions
    }

    // 2. Parse flags
    // 3. Handle --version, --help, --init-config
    // 4. Validate fix-only flag combinations
    // 5. Dispatch to check or fix
}
```

Subcommand detection: If the first non-flag argument matches a known subcommand name (`check`, `init`, `validate-config`, `rules`, `version`), dispatch to that subcommand's handler. Otherwise treat all non-flag arguments as file paths for the root (check/fix) command.

### 4.4 Subcommand Mapping

| CLI Command | Go Function | Notes |
|---|---|---|
| `seiton` (root) | `cmd.runRoot()` | Dispatches to `runCheck` or `runFix` based on `--fix` |
| `seiton check` | `cmd.runCheck()` | Same as root without `--fix` |
| `seiton init` | `cmd.runInit()` | `--output`, `--force` flags |
| `seiton validate-config` | `cmd.runValidateConfig()` | `--config` flag |
| `seiton rules` | `cmd.runRules()` | `--config`, `--format` flags |
| `seiton version` | `cmd.runVersion()` | No flags |

---

## 5. Config Bridge

Shared contract reference: `.github/docs/Seiton_CLI_spec.md` §4.

### 5.1 Resolution Functions

```go
// resolveConfigPath returns the config path from flag, env, or directory walk.
// Returns ("", nil) when no config found. Returns ("", err) when explicit path missing.
func resolveConfigPath(explicit string) (string, error)

// resolveOutputFormat returns the effective format from flag and SEITON_FORMAT env.
func resolveOutputFormat(flagFormat string) string

// resolveColorEnabled returns whether color output is active.
// Precedence: --no-color → SEITON_NO_COLOR → NO_COLOR → --color → auto (TTY + CI).
func resolveColorEnabled(colorFlag string, noColorFlag bool) bool
```

### 5.2 Config Discovery Walk

Discovery probes each directory level starting from the current working directory:

```go
func resolveConfigPath(explicit string) (string, error) {
    if explicit != "" {
        if _, err := os.Stat(explicit); err != nil {
            return "", fmt.Errorf("config file not found: %s", explicit)
        }
        return explicit, nil
    }
    if envConfig := os.Getenv("SEITON_CONFIG"); envConfig != "" {
        if _, err := os.Stat(envConfig); err != nil {
            return "", fmt.Errorf("config file not found: %s", envConfig)
        }
        return envConfig, nil
    }
    // Walk parent directories
    dir, _ := os.Getwd()
    for dir != "" {
        for _, rel := range recommendedRelativePaths {
            p := filepath.Join(dir, rel)
            if _, err := os.Stat(p); err == nil {
                return p, nil
            }
        }
        parent := filepath.Dir(dir)
        if parent == dir {
            break
        }
        dir = parent
    }
    return "", nil
}
```

Probe order per directory:

1. `.github/seiton.yaml`
2. `.github/seiton.yml`
3. `seiton.yaml`
4. `seiton.yml`

### 5.3 CI Detection

```go
func isCi() bool {
    return os.Getenv("CI") != ""
}
```

Used in `resolveColorEnabled`: auto mode disables color when CI is detected or stdout is not a terminal.

Terminal detection uses `golang.org/x/term.IsTerminal(int(os.Stdout.Fd()))`.

---

## 6. Command Implementation Details

### 6.1 Check (Lint Orchestration)

Shared contract reference: `.github/docs/Seiton_Linter_spec.md` §2.1.

Parallelization:

- Uses `errgroup.Group` with `SetLimit(runtime.NumCPU())` for parallel multi-file linting.
- Sequential path for single files, stdin, or `GOMAXPROCS=1`.
- Results are collected into a pre-allocated slice indexed by file position, guaranteeing deterministic aggregated output order.

Post-processing and output:

- Post-lint filters (`--ignore`, `--min-severity`) are applied after aggregation.
- Verbose output format follows `.github/docs/Seiton_CLI_spec.md` §6.4.

Go-specific design notes:

- Rule activation metadata is captured once per `DocumentKind` (at most 2 snapshots) to avoid redundant allocations across parallel workers. The main goroutine emits deterministic rule summaries, timing, and suppression lines in input order.

### 6.2 Fix Orchestration

- Fix path is async via goroutines for network-assisted resolution.
- Fix loop converges iteratively to a stable state.
- Stdin (`-`) is explicitly rejected in fix mode (returns `ExitInvalidOptions`).
- Network remediation (`PinRemediationEngine`) is constructed only when effective pin/image network is enabled.
- When both `--check` and `--dry-run` are passed, `--check` takes precedence: no diffs are printed and no fixes are applied.
- Verbose output format follows `.github/docs/Seiton_CLI_spec.md` §6.4.

### 6.3 Input Discovery

Shared contract reference: `.github/docs/Seiton_CLI_spec.md` §5.

- Auto-discovery: walks parent directories to find `.github/workflows/` (and `.github/actions/` when `includeActions`). Resolved independently — they may come from different ancestor levels.
- Explicit args: expands directories recursively via `filepath.WalkDir`, validates file existence.
- Sort: deterministic ordering via `sort.Strings` for consistent output.
- YAML file matching: `*.yml` and `*.yaml` extensions.

### 6.4 Version Command

- Version string is injected at build time via `-ldflags -X`.
- Runtime description uses `runtime.Version()` (e.g. `go1.24.0`).
- Platform identifier uses `runtime.GOOS + "/" + runtime.GOARCH` (e.g. `linux/amd64`).
- Output format: `seiton <version>\nbuilt with <go-version>, <os/arch>`.

### 6.5 Unknown Option Suggester

- Runs before flag parsing to catch unknown `--long-options`.
- Maintains a slice of known long option names.
- Normalized comparison: strips `-` characters, case-insensitive via `strings.ToLower`.
- Builds `Try: seiton ...` command hint preserving original argument order.
- Drops unknown options without a suggestion from the hint line.

---

## 7. Output Implementation

### 7.1 Text Output

Rich multi-line format (Rust-style) by default; compact single-line with `--oneline`.

Source bytes for snippet rendering are retained in a `map[string][]byte` source map (only allocated when text format without `--oneline` is active). Line extraction and caret positioning use byte offsets.

ANSI color codes are emitted via direct escape sequence writes (no external color library). Color enable/disable is resolved once at startup via `resolveColorEnabled`.

### 7.2 JSON Output

```go
// Standard encoding/json with struct tags
type diagnosticJSONEntry struct {
    File     string `json:"file"`
    Line     int    `json:"line"`
    Col      int    `json:"col"`
    Severity string `json:"severity"`
    RuleID   string `json:"ruleId"`
    Message  string `json:"message"`
    Fixable  bool   `json:"fixable"`
}
```

Output is a JSON array to stdout. Uses `json.NewEncoder(os.Stdout)` with `SetIndent` for readable output.

### 7.3 SARIF Output

SARIF 2.1.0 is emitted via Go struct serialization with `encoding/json`. No external SARIF library is used.

```go
type sarifLog struct {
    Version string     `json:"version"`
    Schema  string     `json:"$schema"`
    Runs    []sarifRun `json:"runs"`
}

type sarifRun struct {
    Tool    sarifTool     `json:"tool"`
    Results []sarifResult `json:"results"`
}

// ... (tool.driver.name, tool.driver.informationUri, tool.driver.rules with id only)
```

Rule metadata in `tool.driver.rules` contains only `id`.

### 7.4 Exit Code Constants

```go
const (
    ExitSuccess         = 0
    ExitLintIssuesFound = 1
    ExitInvalidOptions  = 2
    ExitFatalError      = 3
)
```

---

## 8. Testing

### 8.1 Test Strategy

- Unit tests for config bridge resolution, option suggester, and output formatting.
- Integration tests that invoke `Command.Main` with captured stdout/stderr.
- Golden file tests for text/JSON/SARIF output format stability.
- Testability via `Stdout`/`Stderr` writer injection on the `Command` struct.

### 8.2 Test Execution

```sh
go test ./...
```

For verbose output:

```sh
go test -v ./...
```

For a specific test:

```sh
go test -run TestCheckCommand ./...
```

---

## 9. Cross-Document Consistency

When this document is revised, review and update:

- `.github/docs/Seiton_CLI_spec.md` — if behavioral changes are introduced via implementation
- `.github/docs/Seiton_CLI_csharp_spec.md` — for cross-language consistency
- `.github/docs/Seiton_Linter_go_spec.md` — if config bridge contract changes
