using System.Text.RegularExpressions;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

/// <summary>
/// Parses the GitHub Docs reusable file <c>github-token-available-permissions.md</c>
/// to extract permission scope names and their allowed values from the embedded YAML block.
/// </summary>
internal sealed partial class GitHubDocsPermissionsMarkdownParser
{
    /// <summary>
    /// Parse the markdown content and extract permission scopes.
    /// The file contains a YAML code block like:
    /// <code>
    /// permissions:
    ///   actions: read|write|none
    ///   id-token: write|none
    ///   ...
    /// </code>
    /// Lines may be wrapped in Liquid <c>{% ifversion ... %}</c> conditionals which are stripped.
    /// </summary>
    public PermissionsModel Parse(string markdownContent)
    {
        var normalized = TextNormalization.NormalizeToLf(markdownContent);
        var lines = normalized.Split('\n');

        var scopes = new List<PermissionScopeModel>();
        var inYamlBlock = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();

            // Detect YAML code block boundaries
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (inYamlBlock)
                {
                    break; // end of the first YAML block
                }

                if (line.StartsWith("```yaml", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("```yml", StringComparison.OrdinalIgnoreCase))
                {
                    inYamlBlock = true;
                }

                continue;
            }

            if (!inYamlBlock)
            {
                continue;
            }

            // Strip all Liquid template tags ({% ifversion ... %}, {% endif %}, etc.) from the line
            var stripped = LiquidTagRegex().Replace(line, "").Trim();

            // Skip empty lines after stripping
            if (string.IsNullOrWhiteSpace(stripped))
            {
                continue;
            }

            // Skip the "permissions:" header line
            if (stripped.StartsWith("permissions:", StringComparison.Ordinal))
            {
                continue;
            }

            // Parse scope lines like "actions: read|write|none"
            var match = ScopeLineRegex().Match(stripped);
            if (match.Success)
            {
                var scopeName = match.Groups["name"].Value;
                var valuesStr = match.Groups["values"].Value;
                var allowed = valuesStr.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                scopes.Add(new PermissionScopeModel(scopeName, allowed));
            }
        }

        return new PermissionsModel(scopes);
    }

    [GeneratedRegex(@"^(?<name>[a-z][a-z0-9\-]*):\s*(?<values>[a-z|]+)$")]
    private static partial Regex ScopeLineRegex();

    [GeneratedRegex(@"\{%.*?%\}")]
    private static partial Regex LiquidTagRegex();
}
