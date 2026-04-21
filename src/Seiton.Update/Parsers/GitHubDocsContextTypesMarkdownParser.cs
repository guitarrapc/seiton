namespace Seiton.Update.Parsers;

internal sealed class GitHubDocsContextTypesMarkdownParser
{
    /// <summary>
    /// Parse context property paths from GitHub Docs contexts.md.
    /// Returns a list of parsed contexts, each with a flat list of (dotPath, type) property entries.
    /// dotPath is the property path relative to the context root (without the "contextName." prefix).
    /// </summary>
    public IReadOnlyList<ParsedContextInfo> ParseContextProperties(string markdown)
    {
        var normalized = TextNormalization.NormalizeToLf(markdown);
        var lines = normalized.Split('\n');
        var result = new List<ParsedContextInfo>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();

            var contextName = TryExtractContextHeading(line);
            if (contextName is null)
                continue;

            var props = ParseSectionProperties(lines, i + 1, contextName);
            if (props.Count > 0)
            {
                result.Add(new ParsedContextInfo { Name = contextName, Properties = props });
            }
        }

        return result;
    }

    private static string? TryExtractContextHeading(string line)
    {
        // Match: ## `{name}` context
        if (!line.StartsWith("## `", StringComparison.Ordinal))
            return null;

        var afterPrefix = line[4..]; // strip "## `"
        var backtickEnd = afterPrefix.IndexOf('`');
        if (backtickEnd < 0)
            return null;

        var name = afterPrefix[..backtickEnd];
        var rest = afterPrefix[(backtickEnd + 1)..].Trim();

        return rest.Equals("context", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;
    }

    private static List<ParsedPropertyEntry> ParseSectionProperties(string[] lines, int startIndex, string contextName)
    {
        var props = new List<ParsedPropertyEntry>();
        var tableHeaderIndex = -1;

        for (var i = startIndex; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd();

            // Stop at next H2 (next context section)
            if (l.StartsWith("## ", StringComparison.Ordinal))
                break;

            if (l.Contains("| Property name |", StringComparison.OrdinalIgnoreCase) &&
                l.Contains("| Type |", StringComparison.OrdinalIgnoreCase))
            {
                tableHeaderIndex = i;
                break;
            }
        }

        if (tableHeaderIndex < 0)
            return props;

        // skip header row + separator row
        for (var i = tableHeaderIndex + 2; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd();

            if (l.Length == 0)
                continue;

            // Stop at next heading
            if (l.StartsWith("#", StringComparison.Ordinal))
                break;

            // Stop if line is not a table row
            if (!l.StartsWith("|", StringComparison.Ordinal))
                continue;

            var entry = TryParsePropertyRow(l, contextName);
            if (entry is not null)
                props.Add(entry);
        }

        return props;
    }

    private static ParsedPropertyEntry? TryParsePropertyRow(string line, string contextName)
    {
        // Expected format: | `context.property` | `type` | description |
        var parts = line.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
            return null;

        var nameCell = parts[1];
        var typeCell = parts[2];

        if (!nameCell.StartsWith("`", StringComparison.Ordinal))
            return null;

        var fullName = nameCell.Trim('`');
        var typeName = typeCell.Trim('`').ToLowerInvariant();

        // Strip the leading context prefix (e.g., "github." from "github.action")
        var ctxPrefix = contextName + ".";
        string dotPath;

        if (fullName.StartsWith(ctxPrefix, StringComparison.Ordinal))
        {
            dotPath = fullName[ctxPrefix.Length..];
        }
        else if (fullName.Equals(contextName, StringComparison.Ordinal))
        {
            // Root row (e.g., "| `github` | `object` | ... |") — skip
            return null;
        }
        else
        {
            // Unrecognized pattern — skip
            return null;
        }

        if (string.IsNullOrWhiteSpace(dotPath) || string.IsNullOrWhiteSpace(typeName))
            return null;

        return new ParsedPropertyEntry(dotPath, typeName);
    }

    internal sealed record ParsedPropertyEntry(string DotPath, string Type);

    internal sealed class ParsedContextInfo
    {
        public string Name { get; set; } = string.Empty;
        public List<ParsedPropertyEntry> Properties { get; set; } = [];
    }
}
