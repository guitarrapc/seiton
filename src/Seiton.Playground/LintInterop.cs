using System.Buffers;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace Seiton.Playground;

/// <summary>
/// Browser-callable lint API. Invoked by the host script after <c>runMain()</c> completes.
/// <para>
/// CRITICAL: Every <c>[JSExport]</c> method MUST catch all exceptions internally.
/// An unhandled exception propagating through the interop boundary causes the Mono WASM
/// runtime to abort (exit code 1). Once aborted, the runtime cannot be restarted without
/// a full page reload, and all subsequent calls fail with
/// "Assert failed: .NET runtime already exited with 1".
/// </para>
/// </summary>
public static partial class LintInterop
{
    /// <summary>
    /// User-facing build version (same trimming as the seiton CLI). Exposed to the page script after WASM starts.
    /// </summary>
    /// <summary>Test/diagnostic hook: browser runtime flags (Playwright bisect).</summary>
    [JSExport]
    public static string GetRuntimeFlags()
    {
        try
        {
            return $"IsBrowser={OperatingSystem.IsBrowser()}";
        }
        catch
        {
            return "IsBrowser=unknown";
        }
    }

    [JSExport]
    public static string GetProductVersion()
    {
        try
        {
            return PlaygroundBuildInfo.GetDisplayVersion(typeof(LintInterop).Assembly);
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Lints <paramref name="yamlSource"/> and returns UTF-8 JSON bytes (marshaled as <c>Uint8Array</c> to JS).
    /// JS side decodes with <c>new TextDecoder().decode(bytes)</c>.
    /// </summary>
    /// <param name="yamlSource">Full YAML text (may be empty).</param>
    /// <param name="filePath">Virtual path (e.g. <c>.github/workflows/ci.yml</c> or <c>action.yml</c>).</param>
    [JSExport]
    public static byte[] RunLint(string? yamlSource, string? filePath)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(filePath)
                ? ".github/workflows/test.yml"
                : filePath.Trim();
            return PlaygroundLintRunner.RunToJsonUtf8(yamlSource ?? string.Empty, path);
        }
        catch (Exception ex)
        {
            return SerializeInternalError(ex);
        }
    }

    /// <summary>
    /// Applies automatic fixes sequentially. Network-dependent pinning/digest remediation is unavailable in WASM.
    /// The <c>deny-read-all</c> autofix (scalar <c>read-all</c> → empty mapping)
    /// is skipped here so it cannot undo <c>deny-write-all</c>’s <c>read-all</c> suggestion.
    /// </summary>
    [JSExport]
    public static string ApplyAllFixes(string? yamlSource, string? filePath)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(filePath)
                ? ".github/workflows/test.yml"
                : filePath.Trim();
            return PlaygroundLintRunner.ApplyAllFixes(yamlSource ?? string.Empty, path);
        }
        catch (Exception ex)
        {
            // Return original input so editor content is not corrupted on error.
            // Surface the failure to the browser console so it is not a silent no-op.
            ReportApplyAllFixesError(ex);
            return yamlSource ?? string.Empty;
        }
    }

    /// <summary>
    /// Applies automatic fixes including network-based pin remediation (SHA/digest resolution).
    /// Returns a JSON string: <c>{"yaml":"...","resolved":N,"skipped":N,"failed":N}</c>.
    /// Falls back to offline-only fixes on any unhandled error.
    /// </summary>
    [JSExport]
    public static async Task<string> ApplyAllFixesWithNetworkAsync(string? yamlSource, string? filePath)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(filePath)
                ? ".github/workflows/test.yml"
                : filePath.Trim();
            var result = await PlaygroundLintRunner.ApplyAllFixesAsync(yamlSource ?? string.Empty, path);
            return SerializeAsyncFixResult(result);
        }
        catch (Exception ex)
        {
            // On failure, fall back to sync offline-only fixes and report error.
            ReportApplyAllFixesError(ex);
            var fallbackYaml = ApplyAllFixes(yamlSource, filePath);
            return SerializeAsyncFixResult(new AsyncFixResult(fallbackYaml, 0, 0, 0));
        }
    }

    /// <summary>
    /// Sets the lint configuration from YAML text (same format as <c>seiton.yaml</c>).
    /// Parsed config is cached with XxHash64 content hashing to avoid re-parse on cosmetic edits.
    /// </summary>
    /// <param name="configYaml">Config YAML text. Null/empty resets to default.</param>
    /// <returns>UTF-8 JSON byte array: empty array <c>[]</c> on success, diagnostic array on validation errors.</returns>
    [JSExport]
    public static byte[] SetConfig(string? configYaml)
    {
        try
        {
            return PlaygroundLintRunner.SetConfig(configYaml);
        }
        catch (Exception ex)
        {
            return SerializeInternalError(ex);
        }
    }

    /// <summary>
    /// Reports an automatic-fix failure to the browser console without allowing the
    /// exception to cross the WASM interop boundary.
    /// </summary>
    private static void ReportApplyAllFixesError(Exception ex)
    {
        try
        {
            ConsoleError($"[Seiton.Playground] ApplyAllFixes failed: {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // Never allow error-reporting failures to escape a [JSExport] call path.
        }
    }

    [JSImport("globalThis.console.error")]
    private static partial void ConsoleError(string message);

    /// <summary>
    /// Serializes an internal error as a UTF-8 JSON diagnostic array so the UI can display it
    /// without the exception propagating to the WASM runtime boundary.
    /// </summary>
    private static byte[] SerializeInternalError(Exception ex)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("message"u8, $"[internal error] {ex.GetType().Name}: {ex.Message}");
            writer.WriteNumber("line"u8, 1);
            writer.WriteNumber("column"u8, 1);
            writer.WriteString("severity"u8, "Error");
            writer.WriteString("ruleId"u8, "internal-error");
            writer.WriteBoolean("fixable"u8, false);
            writer.WriteNull("fixDescription"u8);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Serializes an <see cref="AsyncFixResult"/> as a JSON string for the JS side to decode.
    /// </summary>
    private static string SerializeAsyncFixResult(AsyncFixResult result)
    {
        var buffer = new ArrayBufferWriter<byte>(result.Yaml.Length + 64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("yaml"u8, result.Yaml);
            writer.WriteNumber("resolved"u8, result.ResolvedCount);
            writer.WriteNumber("skipped"u8, result.SkippedCount);
            writer.WriteNumber("failed"u8, result.FailedCount);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
