# Seiton CLI C# Implementation Specification

> C# implementation specification for the CLI contract defined in `.github/docs/Seiton_CLI_spec.md`. This document captures C# runtime structures and behavior for command dispatch, config bridge, output formatting, and NativeAOT constraints. See `.github/docs/Seiton_CLI_go_spec.md` for the Go target. Both language specs share the same outline; only language-specific content differs. Parser and linter behavior are specified in `.github/docs/Seiton_Parser_csharp_spec.md` and `.github/docs/Seiton_Linter_csharp_spec.md`.

> **Cross-document synchronization rule**: `.github/docs/Seiton_CLI_spec.md` is the source of truth. When this C# spec is updated, also review and update `.github/docs/Seiton_CLI_spec.md` and `.github/docs/Seiton_CLI_go_spec.md` in the same PR/commit scope.

---

## 0. C# Preamble

### 0.1 Contract

This document defines the C# implementation contract for CLI behavior under `.github/docs/Seiton_CLI_spec.md`.

In scope:

- Project structure and NativeAOT `.csproj` configuration
- ConsoleAppFramework command wiring and parameter mapping
- Config bridge resolution logic (`CliConfigBridge`)
- Output formatter implementation (text/json/SARIF)
- Parallelization strategy for multi-file linting
- Unknown option suggestion implementation

Out of scope:

- Core lint/parse logic (see `.github/docs/Seiton_Linter_csharp_spec.md`, `.github/docs/Seiton_Parser_csharp_spec.md`)
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
4. Keep config resolution aligned with `.github/docs/Seiton_CLI_spec.md` §4 precedence order.

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
      DiagnosticFormatter.cs  # text / json / sarif; GitHubActions uses WriteText today
      GitHubStepSummaryWriter.cs  # GITHUB_STEP_SUMMARY append (github-actions only)
      OutputFormat.cs         # OutputFormat enum (includes GitHubActions)
      OutputFormatParser.cs   # Parses --format string (supports github-actions hyphen)
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

Shared contract reference: `.github/docs/Seiton_CLI_spec.md` §2.

```csharp
[Command("")]
public async Task Root(
    [Argument] string[]? files = null,
    string? config = null,
    string stdinFilename = "<stdin>",
    string[]? ignore = null,
    string? minSeverity = null,
    string format = "text",  // parsed via OutputFormatParser (hyphenated github-actions)
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
| `seiton validate-config` | `ValidateConfig(...)` | `[Command("validate-config")]`; `config`, `verbose` parameters |
| `seiton rules` | `Rules(...)` | `config`, `format` parameters |
| `seiton version` | `Version()` | No parameters |
| `seiton install` | `Install(...)` | `skills`, `target`, `output`, `force`, `ci` parameters |

---

## 5. Config Bridge (`CliConfigBridge`)

Shared contract reference: `.github/docs/Seiton_CLI_spec.md` §4.

### 5.1 Resolution Methods

```csharp
public static class CliConfigBridge
{
    // Config path: flag → SEITON_CONFIG env → directory walk discovery
    public static ConfigPathResolution ResolveConfigPath(string? explicitConfigPath);

    // Output format: explicit non-text flag → explicit --format text → SEITON_FORMAT env → GITHUB_ACTIONS auto (GitHubActions) → default (Text)
    // allowGitHubActionsAutoDefault: false for seiton rules (stays text on GHA)
    public static OutputFormat ResolveOutputFormat(
        OutputFormat flagFormat,
        bool formatExplicitlySet = false,
        bool allowGitHubActionsAutoDefault = true);

// `CliFormatArgs.WasFormatSpecified(rawArgv)` — true when `--format` / `-f` appears before `--`
// Program passes this from `CliVerboseParser.GetRawArgs()` into check/fix handlers.

    // Color: --no-color → SEITON_NO_COLOR → NO_COLOR → --color → auto (TTY + CI)
    public static bool ResolveColorEnabled(ColorMode colorFlag, bool noColorFlag);

    // Load config + apply CLI overrides (enablePinNetwork, enableImageNetwork)
    public static (LintConfig? Config, Diagnostic[] Diagnostics) LoadConfig(
        string? configPath, bool enablePinNetwork, bool enableImageNetwork);
}
```

`ConfigPathResolution.FormatVerboseMessage()` produces stderr lines such as `discovered from …, walked up N level(s)` and `(from --config)`.

### 5.2 Config Discovery Walk

Discovery calls `LintConfigLibrary.FindRecommendedConfigPath(directory)` at each level:

```csharp
return DiscoverConfigPath(Environment.CurrentDirectory);

internal static ConfigPathResolution DiscoverConfigPath(
    string discoveryStartDirectory,
    string? discoveryBoundary = null);
```

The walk loop (production passes `discoveryBoundary: null`):

```csharp
var discoveryStart = Path.GetFullPath(discoveryStartDirectory);
var current = discoveryStart;
var levelsWalked = 0;
while (current is not null)
{
    var discovered = LintConfigLibrary.FindRecommendedConfigPath(current);
    if (discovered is not null)
        return new ConfigPathResolution(discovered, Discovery, discoveryStart, levelsWalked);
    if (discoveryBoundary reached) break;
    current = Directory.GetParent(current)?.FullName;
    levelsWalked++;
}
return new ConfigPathResolution(null, None, discoveryStart, levelsWalked);
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

Shared contract reference: `.github/docs/Seiton_Linter_spec.md` §2.1.

Parallelization:

- Uses `ThreadLocal<LintEngine>` for parallel multi-file linting when `ProcessorCount > 1` and more than 1 file is present.
- Sequential path for single files, stdin, or single-core machines.
- Results are written to a pre-allocated slot array indexed by file position, guaranteeing deterministic aggregated output order.

Post-processing and output:

- Post-lint filters (`--ignore`, `--min-severity`) are applied after aggregation.
- Verbose output format follows `.github/docs/Seiton_CLI_spec.md` §6.4.

C#-specific design notes:

- Timing uses `TimeProvider` abstraction for testable elapsed-time measurement.
- Rule activation metadata is captured once per `DocumentKind` (at most 2 snapshots) to avoid redundant allocations across parallel workers.

### 6.2 FixCommand

- Always async (`Task<int>`) due to network-assisted pin/image resolution.
- Fix loop converges iteratively to a stable state.
- Stdin (`-`) is explicitly rejected in fix mode (returns `ExitCode.InvalidOptions`).
- Network remediation (`PinRemediationEngine`) is constructed only when effective pin/image network is enabled.
- When both `--check` and `--dry-run` are passed, `--check` takes precedence: no diffs are printed and no fixes are applied.
- Verbose output format follows `.github/docs/Seiton_CLI_spec.md` §6.4.

### 6.3 InputDiscovery

Shared contract reference: `.github/docs/Seiton_CLI_spec.md` §5.

- Auto-discovery: walks parent directories to find `.github/workflows/` (and `.github/actions/` when `includeActions`).
- Explicit args: expands directories recursively, validates file existence.
- Sort: `StringComparer.Ordinal` for deterministic ordering.
- Accepts optional `startDirectory` parameter for testability; production callers use the current working directory.

### 6.4 VersionCommand

- Reads version from `AssemblyInformationalVersionAttribute` (embedded at build time); strips `+commitHash` suffix.
- Uses `RuntimeInformation.FrameworkDescription` and `RuntimeInformation.RuntimeIdentifier` for platform info.

### 6.5 Unknown Option Suggester (`CliOptionSuggester`)

- Runs before ConsoleAppFramework to catch unknown `--long-options`.
- Maintains static `HashSet<string>` of known long options.
- Normalized comparison: strips `-` characters, case-insensitive.
- Builds `Try: seiton ...` command hint preserving original argument order.

### 6.6 InstallCommand

Shared contract reference: `.github/docs/Seiton_CLI_spec.md` §1.7.

- Synchronous (`int` return); no async I/O needed.
- Supports two install modes: `--skills` (agent skill files) and `--ci` (CI workflow template). Both can be specified together.
- Skill files are embedded as `EmbeddedResource` with logical names prefixed `Skills/`.
- CI workflow template is embedded as `EmbeddedResource` with logical name `CiTemplates/seiton.yml`. Default installed workflow: Docker lint job with `GITHUB_ACTIONS` / `GITHUB_STEP_SUMMARY` (implicit `github-actions` format); optional commented SARIF / `upload-sarif` job for Code Scanning.
- `SkillResources.GetAllSkillFiles()` reads all embedded resources matching the `Skills/` prefix, returns sorted `List<(string RelativePath, string Content)>`.
- `CiWorkflowResources.GetWorkflowTemplate()` reads the single CI template resource.
- `ResolveSkillDestination(target, output, cwd)` maps target name (`claude`, `copilot`, `cursor`) to output path; returns `null` for unknown targets.
- File write loop creates subdirectories as needed (`Directory.CreateDirectory`).
- Accepts optional `baseDirectory`, `stdout`, `stderr` parameters for testability.

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

### 7.4 `github-actions` Output

Shared contract: `.github/docs/Seiton_CLI_spec.md` §6.5.

| Component | Behavior |
|---|---|
| `OutputFormatParser` | Maps CLI strings `text`, `json`, `sarif`, `github-actions` to `OutputFormat`. Invalid values → exit `2` with stderr message. |
| `CliConfigBridge.ResolveOutputFormat` | Precedence: parsed flag (unless built-in default `text`) → `SEITON_FORMAT` → optional `GITHUB_ACTIONS` auto-default → `Text`. |
| `DiagnosticFormatter` | `GitHubActions` uses `WriteText` with `color: false`. |
| `GitHubStepSummaryWriter` | When format is `GitHubActions` and `GITHUB_STEP_SUMMARY` is a writable path, appends §6.4 Markdown (`## Seiton` once per run, LF, UTF-8 no BOM). `IOException` / `UnauthorizedAccessException` → fall back to stderr. `Reset()` at start of `CheckCommand.Run` / `FixCommand.Run`. |
| `CheckCommand.WriteSummary` / `FixCommand.WriteFixSummary` | Build summary via shared content writers; route to step summary or stderr per §6.4 table. `hint:` lines always use the stderr `TextWriter`. |

`Program.cs` binds `--format` as `string` (not enum) so Cocona can accept `github-actions`.

### 7.5 Exit Code Constants

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

- `.github/docs/Seiton_CLI_spec.md` — if behavioral changes are introduced via implementation
- `.github/docs/Seiton_CLI_go_spec.md` — for cross-language consistency
- `.github/docs/Seiton_Linter_csharp_spec.md` — if `LintConfig` bridge contract changes
