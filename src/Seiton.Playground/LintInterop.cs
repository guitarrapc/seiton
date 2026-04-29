using System.Runtime.InteropServices.JavaScript;

namespace Seiton.Playground;

/// <summary>
/// Browser-callable lint API. Invoked by the host script after <c>runMain()</c> completes.
/// </summary>
public static partial class LintInterop
{
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
}
