using System.Text.RegularExpressions;

namespace Seiton.Update.Parsers;

internal sealed class GitHubDocsRunnerLabelsMarkdownParser
{
    static readonly Regex LabelCodeRegex = new(
        "<code>(?:\\s*<a[^>]*>)?\\s*(?<label>[A-Za-z0-9][A-Za-z0-9.-]*)\\s*(?:</a>)?\\s*</code>\\s*(?<preview>\\((?:public\\s+)?preview\\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    public IReadOnlyList<RunnerLabelEntry> ParseSupportedRunnerLabels(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var section = ExtractSupportedSection(markdown);
        var labels = new Dictionary<string, bool>(StringComparer.Ordinal);

        var matches = LabelCodeRegex.Matches(section);
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var labelRaw = match.Groups["label"].Value.Trim();
            if (!IsHostedRunnerLabel(labelRaw))
            {
                continue;
            }

            var label = labelRaw.ToLowerInvariant();
            var isPreview = match.Groups["preview"].Success;

            if (labels.TryGetValue(label, out var existingPreview))
            {
                labels[label] = existingPreview || isPreview;
            }
            else
            {
                labels[label] = isPreview;
            }
        }

        return labels
            .OrderBy(static x => x.Key, StringComparer.Ordinal)
            .Select(static x => new RunnerLabelEntry(x.Key, x.Value))
            .ToArray();
    }

    static string ExtractSupportedSection(string markdown)
    {
        var start = markdown.IndexOf("## Supported runners", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return markdown;
        }

        var end = markdown.IndexOf("## Administrative privileges", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            end = markdown.Length;
        }

        return markdown[start..end];
    }

    static bool IsHostedRunnerLabel(string value)
    {
        if (value.StartsWith("ubuntu-", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("windows-", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("macos-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public sealed record RunnerLabelEntry(string Label, bool IsPreview);
}
