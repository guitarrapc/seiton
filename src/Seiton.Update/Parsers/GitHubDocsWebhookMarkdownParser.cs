using System.Text.RegularExpressions;

namespace Seiton.Update.Parsers;

internal sealed class GitHubDocsWebhookMarkdownParser
{
    private static readonly Regex HeadingRegex = new(
        "^##\\s+`?(?<name>[a-z0-9_]+)`?\\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex BacktickTokenRegex = new(
        "`(?<token>[a-z0-9_]+)`",
        RegexOptions.Compiled);

    public IReadOnlyDictionary<string, IReadOnlyList<string>?> ParseActivityTypesByEvent(string markdown)
    {
        var result = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);

        var matches = HeadingRegex.Matches(markdown);
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var name = match.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var sectionStart = match.Index + match.Length;
            var sectionEnd = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            if (sectionEnd <= sectionStart)
            {
                continue;
            }

            var section = markdown[sectionStart..sectionEnd];
            if (!TryExtractActivityTypesFromTable(section, out var activityTypes))
            {
                // No parseable table in this section (e.g. informational headings).
                continue;
            }

            result[name] = activityTypes;
        }

        return result;
    }

    public ISet<string> ParseEventNames(string markdown)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in HeadingRegex.Matches(markdown))
        {
            var name = match.Groups["name"].Value;
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static bool TryExtractActivityTypesFromTable(string section, out IReadOnlyList<string>? activityTypes)
    {
        activityTypes = null;
        var lines = TextNormalization.NormalizeToLf(section).Split('\n');

        var activityHeaderIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("Activity types", StringComparison.Ordinal))
            {
                activityHeaderIndex = i;
                break;
            }
        }

        if (activityHeaderIndex < 0)
        {
            return false;
        }

        var separatorIndex = -1;
        for (var i = activityHeaderIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("|", StringComparison.Ordinal) && line.Contains("---", StringComparison.Ordinal))
            {
                separatorIndex = i;
                break;
            }

            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                break;
            }
        }

        if (separatorIndex < 0)
        {
            return false;
        }

        var rowIndex = -1;
        for (var i = separatorIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("|", StringComparison.Ordinal))
            {
                rowIndex = i;
                break;
            }

            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                break;
            }
        }

        if (rowIndex < 0)
        {
            return false;
        }

        var row = lines[rowIndex];
        var columns = row.Split('|');
        // markdown table row has leading/trailing separators, so columns[0] and last are empty.
        if (columns.Length < 4)
        {
            return false;
        }

        var typesCell = columns[2].Trim();
        if (typesCell.Length == 0)
        {
            return false;
        }

        if (typesCell.Contains("Not applicable", StringComparison.OrdinalIgnoreCase))
        {
            activityTypes = [];
            return true;
        }

        if (typesCell.Contains("Custom", StringComparison.OrdinalIgnoreCase))
        {
            // repository_dispatch: user-defined custom event_type values.
            activityTypes = null;
            return true;
        }

        // Cells that still contain docs-template placeholders are not directly parseable.
        // Keep the schema-derived value in such cases.
        if (typesCell.Contains("{%", StringComparison.Ordinal) || typesCell.Contains("%}", StringComparison.Ordinal))
        {
            return false;
        }

        var values = new List<string>();
        foreach (Match token in BacktickTokenRegex.Matches(typesCell))
        {
            var value = token.Groups["token"].Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        values = values
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToList();

        if (values.Count == 0)
        {
            return false;
        }

        activityTypes = values;
        return true;
    }
}
