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
    /// The catalog marks <c>deny-read-all</c> non-disableable; its autofix (scalar <c>read-all</c> → empty mapping)
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
}
