# Seiton CLI C# Implementation Specification

> C# implementation specification for the CLI contract defined in `Seiton_CLI_spec.md`. This document captures C# runtime structures and behavior for command dispatch, config bridge, output formatting, and NativeAOT constraints. See `Seiton_CLI_go_spec.md` for the Go target. Both language specs share the same outline; only language-specific content differs. Parser and linter behavior are specified in `Seiton_Parser_csharp_spec.md` and `Seiton_Linter_csharp_spec.md`.

> **Cross-document synchronization rule**: `Seiton_CLI_spec.md` is the source of truth. When this C# spec is updated, also review and update `Seiton_CLI_spec.md` and `Seiton_CLI_go_spec.md` in the same PR/commit scope.

---

## 0. C# Preamble

### 0.1 Contract

This document defines the C# implementation contract for CLI behavior under `Seiton_CLI_spec.md`.

In scope:

- Project structure and NativeAOT `.csproj` configuration
- ConsoleAppFramework command wiring and parameter mapping
- Config bridge resolution logic (`CliConfigBridge`)
- Output formatter implementation (text/json/SARIF)
- Parallelization strategy for multi-file linting
- Unknown option suggestion implementation

Out of scope:

- Core lint/parse logic (see `Seiton_Linter_csharp_spec.md`, `Seiton_Parser_csharp_spec.md`)
- Rule behavior and rule catalog
- Generated data pipeline (`Seiton.Update`)

### 0.2 Overview

The Seiton CLI C# implementation provides:

1. NativeAOT-compatible thin CLI wrapper over `Seiton.Core`
2. ConsoleAppFramework source-generated command dispatch
3. Config bridge translating CLI flags/env vars into `LintConfig`
4. Multi-format diagnostic output (text/json/SARIF) via source-generated JSON serialization
5. Parallel multi-file linting with deterministic aggregated output ordering
6. Pre-framework unknown option detection with edit-distance suggestions

### 0.3 Structure

Representative implementation surface:

| File/Area | Responsibility |
|---|---|
| `src/Seiton/Program.cs` | ConsoleApp bootstrap, `SeitonCli` class with command methods |
| `src/Seiton/Commands/CheckCommand.cs` | Lint orchestration (sequential + parallel paths) |
| `src/Seiton/Commands/FixCommand.cs` | Fix orchestration with network-assisted pin/image resolution |
| `src/Seiton/Commands/InputDiscovery.cs` | File discovery (auto + explicit expansion) |
| `src/Seiton/Commands/ExitCode.cs` | Exit code constants |
| `src/Seiton/Config/CliConfigBridge.cs` | Config resolution, env var reading, flag override application |
| `src/Seiton/Output/DiagnosticFormatter.cs` | Text/JSON/SARIF formatting |
| `src/Seiton/Cli/CliOptionSuggester.cs` | Unknown option detection and suggestion |

### 0.4 Design

1. Keep CLI as thin wrapper — no lint/parse logic in this project.
2. Keep all JSON serialization AOT-compatible (source-generated `System.Text.Json`).
3. Keep aggregated diagnostic and summary output deterministic regardless of parallelization; verbose progress lines may interleave.
4. Keep config resolution aligned with `Seiton_CLI_spec.md` §4 precedence order.

---

## 1. Project Identity

| Property | Value |
|---|---|
| Project name | `Seiton` |
| Assembly name | `seiton` |
| Namespace root | `Seiton` |
| Output type | `Exe` |
| Framework | `net10.0` |
| NativeAOT | enabled |
| CLI framework | ConsoleAppFramework (source generator mode) |

---

## 2. Project Structure

```
src/
  Seiton/
    Seiton.csproj
    Program.cs                # ConsoleApp bootstrap + root command wiring
    Cli/
      CliOptionSuggester.cs   # Unknown option detection and suggestion
    Commands/
      CheckCommand.cs         # seiton check
      DiagnosticsIgnoreFilter.cs  # Ignore-pattern matcher
      ExitCode.cs             # Exit code constants
      FixCommand.cs           # seiton --fix
      InitCommand.cs          # seiton init
      InputDiscovery.cs       # File discovery logic
      RulesCommand.cs         # seiton rules
      ValidateCommand.cs      # seiton validate-config
      VersionCommand.cs       # seiton version
    Output/
      DiagnosticFormatter.cs  # text / json / sarif formatters
      OutputFormat.cs         # OutputFormat enum
      ColorMode.cs            # ColorMode enum
    Config/
      CliConfigBridge.cs      # CLI flags → LintConfig translation
```

---

## 3. NativeAOT Requirements

Because the CLI is published as NativeAOT:

- No `System.Reflection`-based serialization. JSON output must use source-generated `System.Text.Json` serializers with `[JsonSerializable]`.
- No `Assembly.Load` or plugin loading.
- `ConsoleAppFramework` source generator mode must be used (it supports NativeAOT via source generation).
- `Seiton.Core` must not introduce reflection-dependent code paths reachable from the CLI execution path.
- All AOT trim annotations (`[DynamicallyAccessedMembers]`) must be validated via `<PublishAot>true</PublishAot>` warnings during build.

`.csproj` properties:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <AssemblyName>seiton</AssemblyName>
  <RootNamespace>Seiton</RootNamespace>
  <InvariantGlobalization>true</InvariantGlobalization>
  <PublishAot>true</PublishAot>
  <StripSymbols>true</StripSymbols>
  <OptimizationPreference>Size</OptimizationPreference>
</PropertyGroup>
```

`InvariantGlobalization` is safe here because all rule matching uses UTF-8 byte span comparisons without locale-sensitive collation.

---

## 4. Entry Point and Command Wiring

### 4.1 Bootstrap

```csharp
// Unknown option pre-check (before ConsoleAppFramework sees args)
if (CliOptionSuggester.TryWriteSuggestionsForUnknownOptions(args, Console.Error))
{
    Environment.ExitCode = ExitCode.InvalidOptions;
    return;
}

var app = ConsoleApp.Create();
app.Add<SeitonCli>();
app.Run(args);
```

### 4.2 Command Registration

Commands are registered via a single `SeitonCli` class with method-per-command:

- Root command: `[Command("")]` attribute on the `Root` method.
- Named commands: method name becomes the command name (e.g., `Check`, `Init`, `Rules`, `Version`).
- Hyphenated commands: `[Command("validate-config")]` attribute overrides the name.
- Parameters are automatically mapped to `--kebab-case` long options with single-char short aliases derived from the first letter.

### 4.3 Root Command Parameter Mapping

Shared contract reference: `Seiton_CLI_spec.md` §2.

```csharp
[Command("")]
public async Task Root(
    [Argument] string[]? files = null,
    string? config = null,
    string stdinFilename = "<stdin>",
    string[]? ignore = null,
    string? minSeverity = null,
    OutputFormat format = OutputFormat.Text,
    bool oneline = false,
    ColorMode color = ColorMode.Auto,
    bool noColor = false,
    bool verbose = false,
    bool fix = false,
    bool dryRun = false,
    bool check = false,
    bool enablePinNetwork = false,
    bool enableImageNetwork = false,
    bool includeActions = false)
```

Fix-only flags (`dryRun`, `check`, `enablePinNetwork`, `enableImageNetwork`) are validated at runtime — an error is emitted if they are passed without `--fix`.

### 4.4 Subcommand Mapping

| CLI Command | C# Method | Notes |
|---|---|---|
| `seiton` (root) | `Root(...)` | `[Command("")]`; dispatches to `CheckCommand` or `FixCommand` |
| `seiton check` | `Check(...)` | Same parameters as root minus fix flags |
| `seiton init` | `Init(...)` | `output`, `force` parameters |
| `seiton validate-config` | `ValidateConfig(...)` | `[Command("validate-config")]`; `config` parameter |
| `seiton rules` | `Rules(...)` | `config`, `format` parameters |
| `seiton version` | `Version()` | No parameters |

---

## 5. Config Bridge (`CliConfigBridge`)

Shared contract reference: `Seiton_CLI_spec.md` §4.

### 5.1 Resolution Methods

```csharp
public static class CliConfigBridge
{
    // Config path: flag → SEITON_CONFIG env → directory walk discovery
    public static string? ResolveConfigPath(string? explicitConfigPath);

    // Output format: flag → SEITON_FORMAT env → default (Text)
    public static OutputFormat ResolveOutputFormat(OutputFormat flagFormat);

    // Color: --no-color → SEITON_NO_COLOR → NO_COLOR → --color → auto (TTY + CI)
    public static bool ResolveColorEnabled(ColorMode colorFlag, bool noColorFlag);

    // Load config + apply CLI overrides (enablePinNetwork, enableImageNetwork)
    public static (LintConfig? Config, Diagnostic[] Diagnostics) LoadConfig(
        string? configPath, bool enablePinNetwork, bool enableImageNetwork);
}
```

### 5.2 Config Discovery Walk

Discovery calls `LintConfigLibrary.FindRecommendedConfigPath(directory)` at each level:

```csharp
var current = Environment.CurrentDirectory;
while (current is not null)
{
    var discovered = LintConfigLibrary.FindRecommendedConfigPath(current);
    if (discovered is not null) return discovered;
    current = Directory.GetParent(current)?.FullName;
}
return null;
```

Probe order per directory (defined in `LintConfigLibrary.RecommendedRelativePaths`):

1. `.github/seiton.yaml`
2. `.github/seiton.yml`
3. `seiton.yaml`
4. `seiton.yml`

### 5.3 CI Detection

```csharp
private static bool IsCi() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));
```

Used in `ResolveColorEnabled`: auto mode disables color when CI is detected or stdout is redirected.

---

## 6. Command Implementation Details

### 6.1 CheckCommand

Shared contract reference: `Seiton_Linter_spec.md` §2.1.

- Uses `ThreadLocal<LintEngine>` for parallel multi-file linting when `ProcessorCount > 1` and more than 1 file is present.
- Sequential path for single files, stdin, or single-core machines (avoids ThreadLocal overhead).
- Results are written to a pre-allocated `FileCheckResult[]` slot array indexed by file position, guaranteeing deterministic aggregated diagnostic and summary output order.
- Each worker calls `CopyDiagnostics()` to create caller-owned diagnostic copies that survive engine reuse.
- Post-lint filters (`--ignore`, `--min-severity`) are applied after aggregation.
- Summary line is always written to stderr via `WriteSummary` (error/warning/info counts + file count).
- In `--verbose` mode with diagnostics, `WritePerRuleBreakdown` appends per-rule counts sorted by count descending, then rule ID.
- In `--verbose` mode, `WriteRuleSummary` emits rule activation counts (`verbose: rules: <N> enabled, <M> disabled (workflow)` or `(action)`) and lists disabled rule IDs when present (`verbose: rules: disabled: <id1>, <id2>, ...`). Rule summary is logged once per DocumentKind (workflow and action separately), since `ActiveRuleCount` varies by document kind.
- In `--verbose` mode, per-file timing is logged via `WriteFileTimingSummary` (e.g. `verbose: .github/workflows/ci.yml: workflow, 1.2 ms, 5 diagnostics, 2 suppressed`).
- In `--verbose` mode, total timing is logged via `WriteTotalTiming` (e.g. `verbose: total: 3 file(s) checked in 4.5 ms`).
- `VerboseLogger` exposes `GetTimestamp()` and `GetElapsedTime(long start)` delegating to `TimeProvider` for testable timing.
- `FileCheckResult` carries `ActiveRuleCount`, `DisabledRuleCount`, `DisabledRuleIds`, `DocumentKind`, `FileElapsed`, `FileDiagnosticCount` (computed), and `FileSuppressedCount` (computed) for the parallel aggregation path.
- In parallel mode, `checking <file>...` is emitted from inside `Parallel.For` as best-effort progress output; the lines are self-contained and may interleave, while aggregated diagnostics and summaries remain deterministic.
- When no `--min-severity` is set, errors are zero, and warnings are non-zero, a hint line is emitted: `hint: use --min-severity error to treat warnings as non-blocking in CI`.
- In fix mode, `WriteNetworkFixHint` emits a hint when `unpinned-uses` or `unpinned-image` diagnostics exist but the corresponding network flag is not enabled.

### 6.2 FixCommand

- Always async (`Task<int>`) due to network-assisted pin/image resolution.
- Builds separate `HttpClient` instances for GitHub API and OCI registry (different redirect policies: GitHub follows same-origin only; OCI allows cross-origin for auth challenges).
- Fix loop runs up to 8 passes per file to converge on a stable state.
- Copies diagnostics immediately after `Check()` to avoid use-after-dispose of lint handles.
- Stdin (`-`) is explicitly rejected in fix mode (returns `ExitCode.InvalidOptions`).
- Network remediation (`PinRemediationEngine`) is constructed only when effective pin/image network is enabled.
- In `--verbose` mode, network timing wraps `RemediateAsync()` and emits `verbose: network: resolved pins for <file> in <elapsed> ms`.
- In `--verbose` mode, total timing emits `verbose: total: <N> file(s) fixed in <elapsed> ms`.
- When both `--check` and `--dry-run` are passed, `--check` takes precedence: no diffs are printed and no fixes are applied.

### 6.3 InputDiscovery

Shared contract reference: `Seiton_CLI_spec.md` §5.

```csharp
internal static class InputDiscovery
{
    public static string[] ResolveFiles(string[] files, bool includeActions);
}
```

- Auto-discovery: walks parent directories to find `.github/workflows/` (and `.github/actions/` when `includeActions`).
- Explicit args: expands directories recursively, validates file existence.
- Uses `SearchOption.AllDirectories` for YAML file collection.
- Sort: `StringComparer.Ordinal` for deterministic ordering.

### 6.4 VersionCommand

- Reads version from `AssemblyInformationalVersionAttribute` (embedded at build time); strips `+commitHash` suffix.
- Uses `RuntimeInformation.FrameworkDescription` and `RuntimeInformation.RuntimeIdentifier` for platform info.

### 6.5 Unknown Option Suggester (`CliOptionSuggester`)

- Runs before ConsoleAppFramework to catch unknown `--long-options`.
- Maintains static `HashSet<string>` of known long options.
- Normalized comparison: strips `-` characters, case-insensitive.
- Distance threshold: `≤1` for short options (≤4 chars), `≤2` for medium (≤8 chars), `≤3` for long.
- Builds `Try: seiton ...` command hint preserving original argument order.

---

## 7. Output Implementation

### 7.1 JSON Output

Uses source-generated `System.Text.Json` for AOT compatibility:

```csharp
[JsonSerializable(typeof(DiagnosticJsonEntry[]))]
[JsonSerializable(typeof(RuleStatusJsonEntry[]))]
internal partial class SeitonJsonContext : JsonSerializerContext { }
```

### 7.2 SARIF Output

SARIF 2.1.0 is emitted via an object graph serialized with source-generated `System.Text.Json` (`JsonSerializer` + `JsonSerializerContext`). No external SARIF library is used, maintaining AOT compatibility and minimal dependencies.

### 7.3 Rich Text Output

Source bytes for snippet rendering are retained in a `Dictionary<string, byte[]>` source map (only allocated when text format without `--oneline` is active). Line extraction and caret positioning use byte offsets.

### 7.4 Exit Code Constants

```csharp
internal static class ExitCode
{
    public const int Success = 0;
    public const int LintIssuesFound = 1;
    public const int InvalidOptions = 2;
    public const int FatalError = 3;
}
```

---

## 8. Testing

The CLI project exposes internals to `Seiton.Tests` via:

```xml
<AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
  <_Parameter1>Seiton.Tests</_Parameter1>
</AssemblyAttribute>
```

CLI commands accept `TextWriter` parameters for stdout/stderr injection in tests (e.g., `FixCommand.RunAsync(..., output, error)`).

---

## 9. Cross-Document Consistency

When this document is revised, review and update:

- `Seiton_CLI_spec.md` — if behavioral changes are introduced via implementation
- `Seiton_CLI_go_spec.md` — for cross-language consistency
- `Seiton_Linter_csharp_spec.md` — if `LintConfig` bridge contract changes
