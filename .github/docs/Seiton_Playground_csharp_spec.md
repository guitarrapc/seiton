# Seiton Playground C# Implementation Specification

> C# implementation specification for the playground contract defined in `Seiton_Playground_spec.md`. This document captures C# runtime structures, WASM interop design, project layout, and build/deploy configuration specific to the .NET target.

> **Cross-document synchronization rule**: `Seiton_Playground_spec.md` is the source of truth for playground behavior. When this C# spec is updated, also review and update `Seiton_Playground_spec.md` in the same PR/commit scope.

---

## 0. C# Preamble

### 0.1 Overview

The Seiton Playground C# implementation provides:

1. **WASM host** (`Seiton.Playground`) — `Microsoft.NET.Sdk.WebAssembly` project with `[JSExport]`/`[JSImport]` interop
2. **Core logic** (`Seiton.Playground.Core`) — testable lint runner, JSON serialization
3. **Tests** (`Seiton.Playground.Tests`) — unit tests for core logic + Playwright browser UI tests

### 0.2 Technology Choice

**`wasm-experimental` (wasmbrowser)** — no Blazor framework. Direct `[JSImport]`/`[JSExport]` interop.

| Approach | Binary Size | Maturity | Decision |
|---|---|---|---|
| A. Blazor WebAssembly | 5–15 MB (trimmed) | Stable | **Rejected** — first load too slow |
| **B. `wasm-experimental`** | 1–5 MB (trimmed+AOT) | API stable since .NET 7 | **Adopted** |
| C. NativeAOT→WASM | Minimal | Immature | **Rejected** |

References:
- [JavaScript [JSImport]/[JSExport] interop — WebAssembly Browser App](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/wasm-browser-app?view=aspnetcore-10.0)
- [Configuring .NET WebAssembly applications](https://github.com/dotnet/runtime/blob/main/src/mono/wasm/features.md)

### 0.3 Structure

| Path | Responsibility |
|---|---|
| `src/Seiton.Playground/Seiton.Playground.csproj` | WebAssembly Browser App (WASM host) |
| `src/Seiton.Playground/LintInterop.cs` | `[JSExport]` interop surface |
| `src/Seiton.Playground/Program.cs` | WASM entry point |
| `src/Seiton.Playground/wwwroot/` | Static assets (index.html, main.js, style.css) |
| `src/Seiton.Playground.Core/PlaygroundLintRunner.cs` | Lint engine invocation + JSON serialization |
| `src/Seiton.Playground.Core/PlaygroundDiagnosticDto.cs` | Diagnostic DTO |
| `src/Seiton.Playground.Core/PlaygroundJsonSerializerContext.cs` | Source Generator JSON context |
| `src/Seiton.Playground.Core/PlaygroundBuildInfo.cs` | Version string logic |
| `tests/Seiton.Playground.Tests/` | Unit + Playwright tests |

### 0.4 Design Decisions

1. Separate `Seiton.Playground` (WASM host) from `Seiton.Playground.Core` (logic) so core logic can be unit-tested without a WASM environment.
2. Reuse a single static `LintEngine` instance to amortize rule setup cost and avoid GC pressure on the constrained WASM heap.
3. Every lint call is a full parse + full lint. Incremental parsing was removed (see §6.5) — it had no production consumer and its lifetime hazards outweighed its benefit.
4. All `[JSExport]` methods catch exceptions internally — unhandled exceptions crossing the interop boundary abort the Mono WASM runtime irreversibly.
5. Use `Utf8JsonWriter` (not `JsonSerializer` with reflection) for zero-allocation diagnostic serialization in the hot path.

### 0.5 Seiton.Core WASM Compatibility

| Feature | Compatible | Notes |
|---|---|---|
| `stackalloc` | ✅ | Supported by .NET WASM runtime |
| `System.Runtime.CompilerServices.Unsafe` | ✅ | Part of BCL |
| `MemoryMarshal` | ✅ | Part of BCL |
| `XxHash64` (scalar impl) | ✅ | No SIMD, pure arithmetic |
| `ReadOnlySpan<byte>` / UTF-8 comparisons | ✅ | Runtime supported |
| VYaml 1.4.0 | ✅ | Pure C#, verified working |
| `HttpClient` (OnlineAudit) | ⚠️ | Browser fetch with CORS constraints; disabled in playground |
| SSE/AVX/Vector intrinsics | ✅ N/A | Not used in Seiton.Core |

---

## 1. WASM Interop Layer

### 1.1 LintInterop.cs

```csharp
public static partial class LintInterop
{
    [JSExport]
    public static string GetProductVersion();

    [JSExport]
    public static byte[] RunLint(string? yamlSource, string? filePath);

    [JSExport]
    public static string ApplyAllFixes(string? yamlSource, string? filePath);

    [JSExport]
    public static Task<string> ApplyAllFixesWithNetworkAsync(string? yamlSource, string? filePath);

    [JSExport]
    public static byte[] SetConfig(string? configYaml);

    [JSImport("globalThis.console.error")]
    private static partial void ConsoleError(string message);
}
```

| Method | Return Type | Rationale |
|---|---|---|
| `RunLint` | `byte[]` (UTF-8 JSON) | JS receives `Uint8Array`, decodes with `TextDecoder`. Avoids string marshalling copy. |
| `ApplyAllFixes` | `string` | Fixed YAML text. Returns original source on error (prevents editor corruption). |
| `ApplyAllFixesWithNetworkAsync` | `Task<string>` | JSON string: `{"yaml":"...","resolved":N,"skipped":N,"failed":N}`. Async for network I/O. Falls back to offline-only fixes on error. |
| `SetConfig` | `byte[]` (UTF-8 JSON) | Config diagnostic array. Empty array `[]` on success; previous valid config retained on error. |
| `GetProductVersion` | `string` | For version badge display. |

**Critical invariant**: Every `[JSExport]` method body is wrapped in `try/catch(Exception)`. The Mono WASM runtime aborts (`exit 1`) on any unhandled exception crossing the interop boundary, and cannot be restarted without a full page reload.

### 1.2 Input Normalization

- `yamlSource`: null/whitespace treated as empty string
- `filePath`: trimmed; defaults to `.github/workflows/test.yml` when null or whitespace

### 1.3 Error Logging

On exception, `RunLint` and `ApplyAllFixes` call `ConsoleError(message)` (`[JSImport("globalThis.console.error")]`) to log the exception message to the browser console before returning the safe fallback value.

### 1.4 Internal-Error Diagnostic Payload

When `RunLint` catches an exception, it returns:

```json
[{
  "message": "[internal error] {ExceptionType}: {Message}",
  "line": 1,
  "column": 1,
  "severity": "Error",
  "ruleId": "internal-error",
  "fixable": false,
  "fixDescription": null
}]
```

### 1.5 Program.cs

```csharp
Console.WriteLine("Seiton WASM runtime initialized.");
```

Executed once at `runMain()`. Lint functionality is invoked via `[JSExport]` from JavaScript.

---

## 2. Core Logic (Seiton.Playground.Core)

### 2.1 PlaygroundLintRunner

```csharp
public static class PlaygroundLintRunner
{
    private static readonly LintEngine Engine = new();
    private static readonly object EngineGate = new();

    public static byte[] RunToJsonUtf8(string yamlSource, string filePath);
    public static string ApplyAllFixes(string yamlSource, string filePath);
    public static byte[] SetConfig(string configYaml);
}
```

Key implementation details:

- **Engine reuse**: Static `LintEngine` avoids allocating 50+ rule objects per keystroke. `LintEngine.Check()` clears internal state at the start of each call; reuse is safe.
- **Lock**: WASM is single-threaded, but the lock ensures correctness for parallel test runners on desktop .NET.
- **JSON serialization**: `ArrayBufferWriter<byte>` + `Utf8JsonWriter` (zero-allocation hot path). `JsonWriterOptions` with `UnsafeRelaxedJsonEscaping`.
- **Reusable encode buffer**: `_utf8Buf` is reused for UTF-8 encoding; it only reallocates when the byte length changes (the parser requires an exact-size array).
- **Identity-based short circuit**: If `yamlSource` (by reference) and `filePath` are identical to the last call, returns cached JSON output immediately.
- **Byte-level cache reuse**: When serialized JSON bytes are content-equal to the previous result, the prior `byte[]` instance is reused (avoids allocation for unchanged output).
- **Diagnostic lifetime invariant**: `DiagnosticList`/`ReadOnlySpan<Diagnostic>` values are consumed while the owning `LintResult`/`AstArena` is still alive. `RunToJsonUtf8` serializes diagnostics before those owners are disposed. Workflow path uses `using var lintResult = Engine.Check(...)`; action metadata uses `LintActionMetadataToJsonUtf8` (`try`/`finally` around arena disposal).
- **Flow lifetime invariant**: Flow JSON and Mermaid output are serialized from the live UTF-8 workflow AST before its `ParseResult` is disposed. The Playground does not materialize the owned `WorkflowFlow` DTO graph, keeping flow generation allocation-free after reusable buffers and pools are warm.

### 2.1.1 Config Content-Hash Caching

`LintConfigYamlParser.Parse()` allocates internally (VYaml reader state, dictionaries, lists). To avoid unnecessary GC pressure on the WASM heap, `PlaygroundLintRunner` caches the parsed `LintConfig` and only re-parses when config content has meaningfully changed.

**Cached state:**

```csharp
private static ulong _configHash;          // XxHash64 of normalized config
private static LintConfig? _cachedConfig;   // last successfully parsed config
private static byte[] _cachedConfigDiag;    // last SetConfig diagnostic result (JSON)
```

**SetConfig algorithm:**

1. If input is null/empty/whitespace-only: reset to default config, clear hash, return `[]`
2. Normalize the input (strip trailing whitespace per line, remove blank lines)
3. Compute XxHash64 of the normalized UTF-8 bytes
4. If hash matches `_configHash`: return `_cachedConfigDiag` immediately (zero allocation)
5. Call `LintConfigLibrary.Validate(configYaml, "seiton.yaml")`
6. On success: update `_cachedConfig`, `_configHash`, `_cachedConfigDiag = []`
7. On validation errors: keep previous `_cachedConfig`, serialize diagnostics to `_cachedConfigDiag`, update `_configHash = hash` (repeated invalid input is a cache hit, avoids re-parsing)

**Normalization procedure** (for hash stability across cosmetic edits):

```
Input: "rules:\n  runner-no-latest: warn\n\n  # comment\n"
  → Split on '\n'
  → TrimEnd() each line
  → Remove empty lines
  → Join with '\n'
Result: "rules:\n  runner-no-latest: warn\n  # comment"
```

**Integration with RunToJsonUtf8 / ApplyAllFixes:**

- Both methods use `_cachedConfig ?? DefaultConfig` when invoking `LintEngine.Check()`
- The `DefaultConfig` is the same hardcoded config currently used (Fix enabled, Network default, Output default, SkipSuppressionSummary=true)

### 2.2 Parse Strategy: Full Parse Per Call

Every `RunToJsonUtf8` call performs a full parse + full lint. Workflow files use `Engine.Check`; action metadata files (`action.yml`) use classified parsing via `LintActionMetadataToJsonUtf8`. The only caches are the identity-based short circuit and the byte-level output reuse (§2.1). Incremental parsing existed previously (D-5b/5c/5d) but was removed — see §6.5 for the decision record.

### 2.3 ApplyAllFixes Strategy

- **Priority order**: `deny-write-all` → `checkout-persist-credentials` → remaining rules
- **Excluded**: `deny-read-all` (would undo `deny-write-all`'s `read-all` suggestion)
- **Iteration cap**: 64 passes maximum
- **Network fixes**: Skipped (pinning/digest remediation requires network unavailable in WASM)
- **One fix per pass**: Picks the highest-priority fixable diagnostic and applies it, then re-lints
- **Early termination**: If diagnostics contain fixable items but none pass the local applicability filter (e.g., all are network-dependent), returns immediately without iteration
- **Browser parse boundary**: Explicit fix passes use structural-hint classification, while real-time lint keeps the single-traversal browser path. Native AOT requires this separation: globally enabling the hint pass can hang on incomplete editor mappings, but omitting it from fix passes can expose fixable diagnostics whose edits are not produced.

### 2.4 PlaygroundDiagnosticDto

```csharp
public sealed class PlaygroundDiagnosticDto
{
    public required string Message { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required string Severity { get; init; }
    public string? RuleId { get; init; }
    public bool Fixable { get; init; }
    public string? FixDescription { get; init; }
}
```

### 2.5 JSON Serialization

`PlaygroundJsonSerializerContext` provides Source Generator context:

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<PlaygroundDiagnosticDto>))]
internal partial class PlaygroundJsonSerializerContext : JsonSerializerContext;
```

`JsonSerializerIsReflectionEnabledByDefault=false` in the project file ensures trimming safety.

### 2.6 PlaygroundBuildInfo — Version Precedence

```
InformationalVersion (preferred, e.g. "1.2.3+abc")
  → strip commit metadata at '+'
  → fallback: AssemblyVersion
  → fallback: literal "0.0.0"
```

---

## 3. Project Configuration

### 3.1 Seiton.Playground.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk.WebAssembly">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <InvariantGlobalization>true</InvariantGlobalization>
    <InvariantTimezone>true</InvariantTimezone>
  </PropertyGroup>
  <PropertyGroup>
    <EmccInitialHeapSize>67108864</EmccInitialHeapSize>
    <EmccMaximumHeapSize>1073741824</EmccMaximumHeapSize>
  </PropertyGroup>
  <PropertyGroup Condition="'$(Configuration)'=='Release'">
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>full</TrimMode>
    <RunAOTCompilation>true</RunAOTCompilation>
    <WasmEnableWebcil>true</WasmEnableWebcil>
  </PropertyGroup>
</Project>
```

| Property | Rationale |
|---|---|
| `Sdk=Microsoft.NET.Sdk.WebAssembly` | WASM SDK without Blazor framework |
| `AllowUnsafeBlocks` | Required by `[JSImport]`/`[JSExport]` Roslyn code generator |
| `InvariantGlobalization` | Excludes ICU data (saves several MB) |
| `InvariantTimezone` | Excludes timezone DB |
| `EmccInitialHeapSize=64MB` | Reduces early `memory.grow` fragmentation |
| `EmccMaximumHeapSize=1GB` | Debug builds (no trim, no AOT) can exceed 512MB |
| `PublishTrimmed` + `TrimMode=full` | Maximum dead-code elimination (Release only) |
| `RunAOTCompilation` | IL→WASM AOT compilation (Release only) |
| `WasmEnableWebcil` | Serve as `.wasm` instead of `.dll` (Release only) |
| `PlaygroundSoftFingerprint` | Emits both fingerprinted and non-fingerprinted files for GitHub Pages |

### 3.2 Seiton.Playground.Core.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Seiton.Playground</RootNamespace>
    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Seiton.Core\Seiton.Core.csproj" />
  </ItemGroup>
</Project>
```

### 3.3 Prerequisites

```shell
dotnet workload install wasm-tools
```

Required for AOT compilation, native rebuild, and trimming.

---

## 4. Build and Publish

### 4.1 Development (Local)

```shell
dotnet build src/Seiton.Playground
dotnet run --project src/Seiton.Playground
```

`dotnet run` starts a development WASM server. No AOT compilation — fast iteration.

### 4.2 Release Publish

```shell
dotnet publish src/Seiton.Playground/Seiton.Playground.csproj \
  -c Release -r browser-wasm \
  -p:PlaygroundSoftFingerprint=true \
  -o publish/
```

Output: `publish/wwwroot/`

```
wwwroot/
  index.html, main.js, style.css
  favicon.png, ogp.png
  _framework/
    dotnet.js, dotnet.native.js, dotnet.runtime.js
    dotnet.native.wasm          (compiled .NET runtime / Mono)
    System.Private.CoreLib.wasm
    Seiton.Core.wasm            (trimmed)
    Seiton.Playground.Core.wasm
    Seiton.Playground.wasm
    ...
```

### 4.3 Size Optimization

| # | Technique | Effect |
|---|---|---|
| 1 | `InvariantGlobalization=true` | Excludes ICU data (several MB) |
| 2 | `InvariantTimezone=true` | Excludes timezone DB |
| 3 | `PublishTrimmed=true` + `TrimMode=full` | Dead code elimination |
| 4 | `RunAOTCompilation=true` | IL→WASM, no runtime JIT |
| 5 | Brotli compression | 60–70% transfer size reduction (auto-generated `.br` files) |

**Trimming compatibility**: Seiton.Core uses no reflection, no DI container, and no JSON serialization (YAML parsed via VYaml). Playground.Core uses Source Generator JSON (`PlaygroundJsonSerializerContext`).

### 4.4 Expected Size

| Configuration | Expected Size (Brotli compressed) |
|---|---|
| Trimmed + AOT + InvariantGlobalization + InvariantTimezone | **2–5 MB** |
| Reference: actionlint main.wasm (after wasm-opt) | ~3 MB |

---

## 5. Comparison with actionlint Architecture

| Aspect | actionlint | seiton |
|---|---|---|
| Language | Go | C# (.NET 10) |
| WASM build | `GOOS=js GOARCH=wasm go build` | `dotnet publish` (Microsoft.NET.Sdk.WebAssembly) |
| JS glue | `wasm_exec.js` (Go standard) | `dotnet.js` (auto-generated) |
| WASM artifacts | Single `main.wasm` | `_framework/` directory (dotnet.native.wasm + assembly set) |
| JS↔WASM interop | `js.FuncOf` + `window.Set` | `[JSExport]` / `[JSImport]` attributes |
| WASM optimization | `wasm-opt` (Binaryen) | AOT + IL trimming + Brotli |

---

## 6. Lessons Learned (C#-Specific)

### 6.1 `[JSExport]` Exception Propagation Kills the WASM Runtime

**Problem**: Unhandled exceptions crossing the `[JSExport]` interop boundary cause the Mono WASM runtime to abort with exit code 1. All subsequent `[JSExport]` calls fail with `"Assert failed: .NET runtime already exited with 1"`.

**Mitigation**:
- All `[JSExport]` methods wrap their body in `try/catch(Exception)`.
- `RunLint` returns an error-diagnostic JSON array on exception.
- `ApplyAllFixes` returns the original input text on exception.
- `ConsoleError` logs to the browser console without re-throwing.
- Real-time browser parsing omits the structural-hint pre-pass and uses the document selector's path hint (action metadata) or workflow default. Explicit fix operations retain the structural-hint path. This keeps each keystroke to one VYaml traversal without degrading fix generation: VYaml's AOT reader can stop making progress when a skip-only pre-pass traverses an incomplete mapping, while omitting the pass from fix execution can produce a silent no-op.
- JS sends every debounced editor state to WASM; there are no keyword- or syntax-specific defer rules.

### 6.2 LintEngine Reuse Is Mandatory

The WASM heap is constrained. Creating a new `LintEngine` per keystroke allocates 50+ rule objects, causing excessive GC pressure. The static engine instance is reused; `Check()` clears internal state at the start of each invocation.

### 6.4 Config Re-Parse Avoidance via Content Hash

**Problem**: `LintConfigYamlParser.Parse()` allocates dictionaries, lists, and VYaml reader state. Calling it on every lint (which fires on every keystroke in the YAML editor) would add ~1–2 KB allocation per invocation unnecessarily, since config rarely changes.

**Mitigation**: Cache the parsed `LintConfig` alongside an XxHash64 hash of the normalized config YAML. `SetConfig` is called only when the config editor content changes (JS-side debounce), and the WASM side additionally skips re-parse if the normalized hash matches. This provides two layers of protection:
1. JS debounce prevents rapid `SetConfig` calls during typing
2. WASM hash check prevents re-parse when edits are purely cosmetic (whitespace/blank lines)

### 6.5 Incremental Parse Was Removed (2026-07)

**History**: Incremental parsing (D-5b root-section skip / D-5c job reuse / D-5d per-job diagnostic cache) was built to reduce WASM allocations per keystroke and showed large benchmark wins at the time (PartialChange Large: -99.4% allocation). It was later disabled in the browser after a "memory access out of bounds" crash during typing (#125) — incremental reuse could retain stale spans across edits and trap under WASM AOT. From that point the only runtime that ever executed the incremental path was the desktop test suite.

**Decision**: Remove the entire mechanism (`IncrementalParseContext`, `WorkflowParser.ParseIncremental`, `AstArena.BulkImportFrom`/`RebindSource`, `LintEngine`/`WorkflowVisitor` job-skip support). Every playground lint call is now a full parse + full lint.

**Why**:
1. **No production consumer** — the CLI never used it, and the browser (the playground's only runtime) had it disabled.
2. **Memory-safety tax** — it imposed lifetime invariants (pooled buffer reuse, arena rebinding, previous-source retention) that every AST/arena change had to preserve, and violations surfaced as native WASM traps rather than managed exceptions.
3. **It had become a net loss** — after the data-oriented AST redesign (#183) made full parse cheap, the committed `PlaygroundLintBenchmark` baseline showed the incremental path (`PartialChange`) was *slower* than full parse (`FullChange`) and allocated *more* (Small: 1.16 ms / 123 KB vs 0.20 ms / 52 KB), because section scanning/hashing plus wholesale table copies (`BulkImportFrom`) outweighed the skipped work.

**Lesson**: An optimization that requires cross-call buffer lifetime invariants is a liability in WASM AOT, where violations are unrecoverable runtime aborts. Re-check an optimization's benefit after each major architecture change — the AST redesign silently inverted this one's cost/benefit. If large-document typing latency ever becomes a requirement, design incrementality fresh (position-shift-tolerant, e.g. LSP-style) instead of resurrecting the byte-identical-offset design.

---

## 7. References

- [JavaScript [JSImport]/[JSExport] interop — WebAssembly Browser App](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/wasm-browser-app?view=aspnetcore-10.0)
- [Configuring .NET WebAssembly applications](https://github.com/dotnet/runtime/blob/main/src/mono/wasm/features.md)
- [JavaScript [JSImport]/[JSExport] interop in .NET WebAssembly](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/?view=aspnetcore-10.0)
- [dotnet.d.ts (.NET WASM runtime configuration)](https://github.com/dotnet/runtime/blob/main/src/mono/browser/runtime/dotnet.d.ts)
