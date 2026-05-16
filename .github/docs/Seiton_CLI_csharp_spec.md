# Seiton CLI C# Implementation Specification

> C#-specific implementation details for the Seiton CLI.
> For language-neutral CLI behavior, see `Seiton_CLI_spec.md`.

---

## 0. Preamble

### 0.1 Scope

This document specifies C#/.NET-specific implementation decisions for the Seiton CLI:

- Project structure and file layout
- NativeAOT requirements and `.csproj` configuration
- CLI framework choice (ConsoleAppFramework)
- Internal type structure (commands, formatters, config bridge)
- Parallelization strategy
- AOT-compatible serialization

### 0.2 Project Identity

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

## 1. Project Structure

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

## 2. NativeAOT Requirements

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

## 3. ConsoleAppFramework Wiring

Commands are registered via a single `SeitonCli` class with method-per-command:

```csharp
var app = ConsoleApp.Create();
app.Add<SeitonCli>();
app.Run(args);
```

- Root command: `[Command("")]` attribute on the `Root` method.
- Named commands: method name becomes the command name (e.g., `Check`, `Init`, `Rules`, `Version`).
- Hyphenated commands: `[Command("validate-config")]` attribute overrides the name.
- Parameters are automatically mapped to `--kebab-case` long options with single-char short aliases.

### 3.1 Unknown Option Pre-Check

Before ConsoleAppFramework processes arguments, `CliOptionSuggester.TryWriteSuggestionsForUnknownOptions` intercepts unknown long options to provide friendly suggestions. If any unknown options are detected, the CLI exits with code 2 without invoking ConsoleAppFramework.

---

## 4. Command Implementation Details

### 4.1 CheckCommand

- Uses `ThreadLocal<LintEngine>` for parallel multi-file linting when `ProcessorCount > 1` and more than 1 file is present.
- Sequential path for single files, stdin, or single-core machines (avoids ThreadLocal overhead).
- Aggregates results in input order for deterministic output.

### 4.2 FixCommand

- Always async (`Task<int>`) due to network-assisted pin/image resolution.
- Builds separate `HttpClient` instances for GitHub API and OCI registry (different redirect policies).
- Fix loop runs up to 8 passes per file to converge on a stable state.
- Copies diagnostics immediately after `Check()` to avoid use-after-dispose of lint handles.

### 4.3 VersionCommand

- Reads version from `AssemblyInformationalVersionAttribute` (embedded at build time).
- Uses `RuntimeInformation.FrameworkDescription` and `RuntimeInformation.RuntimeIdentifier` for platform info.

### 4.4 CliConfigBridge

- Resolves config path: explicit flag → `SEITON_CONFIG` env var → parent-directory walk with `LintConfigLibrary.FindRecommendedConfigPath`.
- Resolves output format: flag → `SEITON_FORMAT` env var → default (`Text`).
- Resolves color: `--no-color` flag → `SEITON_NO_COLOR` env → `NO_COLOR` env → `--color` flag → auto (TTY + non-CI detection).
- CI detection: `Environment.GetEnvironmentVariable("CI")` is non-empty.

---

## 5. Output Implementation

### 5.1 JSON Output

Uses source-generated `System.Text.Json` for AOT compatibility:

```csharp
[JsonSerializable(typeof(DiagnosticJsonEntry[]))]
[JsonSerializable(typeof(RuleStatusJsonEntry[]))]
internal partial class SeitonJsonContext : JsonSerializerContext { }
```

### 5.2 SARIF Output

SARIF 2.1.0 is emitted via manual `Utf8JsonWriter` construction (no external SARIF library) to maintain AOT compatibility and minimize dependencies.

### 5.3 Rich Text Output

Source bytes for snippet rendering are retained in a `Dictionary<string, byte[]>` source map (only allocated when text format without `--oneline` is active). Line extraction and caret positioning use byte offsets.

---

## 6. Testing

The CLI project exposes internals to `Seiton.Tests` via:

```xml
<AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
  <_Parameter1>Seiton.Tests</_Parameter1>
</AssemblyAttribute>
```

---

## 7. Cross-Document Consistency

When this document is revised, review and update:

- `Seiton_CLI_spec.md` — if behavioral changes are introduced via implementation
- `Seiton_Linter_csharp_spec.md` — if `LintConfig` bridge contract changes
