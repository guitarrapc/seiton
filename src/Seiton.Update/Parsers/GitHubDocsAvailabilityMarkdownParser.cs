namespace Seiton.Update.Parsers;

internal sealed class GitHubDocsAvailabilityMarkdownParser
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ParseWorkflowKeyContexts(string markdown)
    {
        var normalized = TextNormalization.NormalizeToLf(markdown);
        var sectionStart = normalized.IndexOf("### Context availability", StringComparison.Ordinal);
        if (sectionStart < 0)
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }

        var section = normalized[sectionStart..];
        var lines = section.Split('\n');

        var headerIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Contains("| Workflow key |", StringComparison.Ordinal)
                && line.Contains("| Context |", StringComparison.Ordinal))
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0)
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        for (var i = headerIndex + 2; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                break;
            }

            if (!line.StartsWith("|", StringComparison.Ordinal))
            {
                continue;
            }

            var columns = line.Split('|');
            if (columns.Length < 5)
            {
                continue;
            }

            var workflowKey = NormalizeCell(columns[1]);
            if (string.IsNullOrWhiteSpace(workflowKey))
            {
                continue;
            }

            var contextCell = NormalizeCell(columns[2]);
            var contexts = ParseContexts(contextCell);
            result[workflowKey] = contexts;
        }

        return result;
    }

    static string NormalizeCell(string cell)
    {
        return cell
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    static IReadOnlyList<string> ParseContexts(string contextCell)
    {
        if (string.IsNullOrWhiteSpace(contextCell)
            || string.Equals(contextCell, "None", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return contextCell
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
