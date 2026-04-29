using System.Reflection;
using System.Runtime.InteropServices.JavaScript;

namespace Seiton.Playground;

/// <summary>
/// Browser-callable lint API. Invoked by the host script after <c>runMain()</c> completes.
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
        var path = string.IsNullOrWhiteSpace(filePath)
            ? ".github/workflows/test.yml"
            : filePath.Trim();
        return PlaygroundLintRunner.RunToJson(yamlSource ?? string.Empty, path);
    }

    /// <summary>
    /// Applies automatic fixes sequentially. Network-dependent pinning/digest remediation is unavailable in WASM.
    /// The catalog marks <c>deny-read-all</c> non-disableable; its autofix (scalar <c>read-all</c> → empty mapping)
    /// is skipped here so it cannot undo <c>deny-write-all</c>’s <c>read-all</c> suggestion.
    /// </summary>
    [JSExport]
    public static string ApplyAllFixes(string? yamlSource, string? filePath)
    {
        var path = string.IsNullOrWhiteSpace(filePath)
            ? ".github/workflows/test.yml"
            : filePath.Trim();
        return PlaygroundLintRunner.ApplyAllFixes(yamlSource ?? string.Empty, path);
    }
}
