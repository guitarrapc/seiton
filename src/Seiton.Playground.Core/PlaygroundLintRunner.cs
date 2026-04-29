using System.Text;
using System.Text.Json;
using Seiton.Core.Linting;

namespace Seiton.Playground;

/// <summary>
/// Runs <see cref="LintEngine"/> and serializes diagnostics to a JSON array for the playground UI.
/// </summary>
public static class PlaygroundLintRunner
{
    private static readonly LintEngine Engine = new();

    /// <summary>
    /// Parses and lints <paramref name="yamlSource"/> as UTF-8 and returns a JSON array of diagnostics.
    /// </summary>
    /// <param name="yamlSource">Full YAML document text.</param>
    /// <param name="filePath">Virtual path used for document classification (e.g. workflow vs action).</param>
    /// <returns>UTF-8 JSON array of camelCase diagnostic objects.</returns>
    public static string RunToJson(string yamlSource, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var utf8Yaml = Encoding.UTF8.GetBytes(yamlSource);
        var result = Engine.Check(utf8Yaml, filePath);

        var list = new List<PlaygroundDiagnosticDto>(result.Diagnostics.Length);
        for (var i = 0; i < result.Diagnostics.Length; i++)
        {
            var d = result.Diagnostics[i];
            var loc = d.Location;
            list.Add(new PlaygroundDiagnosticDto
            {
                Message = d.Message,
                Line = loc.StartLine,
                Column = loc.StartColumn,
                Severity = d.Severity.ToString(),
                RuleId = d.RuleId,
            });
        }

        return JsonSerializer.Serialize(list, PlaygroundJsonSerializerContext.Default.ListPlaygroundDiagnosticDto);
    }
}
