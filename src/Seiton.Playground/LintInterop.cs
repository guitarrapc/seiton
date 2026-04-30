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
    public static string GetProductVersion() => PlaygroundBuildInfo.GetDisplayVersion(typeof(LintInterop).Assembly);

    /// <summary>
    /// Lints <paramref name="yamlSource"/> as the file at <paramref name="filePath"/> and returns a JSON array of diagnostics.
    /// </summary>
    /// <param name="yamlSource">Full YAML text (may be empty).</param>
    /// <param name="filePath">Virtual path (e.g. <c>.github/workflows/ci.yml</c> or <c>action.yml</c>).</param>
    [JSExport]
    public static string RunLint(string? yamlSource, string? filePath)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(filePath)
                ? ".github/workflows/test.yml"
                : filePath.Trim();
            return PlaygroundLintRunner.RunToJson(yamlSource ?? string.Empty, path);
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
        catch (Exception)
        {
            // Return original input so editor content is not corrupted on error.
            return yamlSource ?? string.Empty;
        }
    }

    /// <summary>
    /// Serializes an internal error as a JSON diagnostic array so the UI can display it
    /// without the exception propagating to the WASM runtime boundary.
    /// </summary>
    private static string SerializeInternalError(Exception ex)
    {
        // Manually construct JSON to avoid depending on the internal PlaygroundJsonSerializerContext.
        // The message is JSON-escaped to prevent injection.
        var escapedMessage = JsonEncodedText.Encode($"[internal error] {ex.GetType().Name}: {ex.Message}");
        return "[{\"message\":\"" + escapedMessage + "\",\"line\":1,\"column\":1,\"severity\":\"Error\",\"ruleId\":\"internal-error\",\"fixable\":false}]";
    }
}
