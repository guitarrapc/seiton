using System.Text.RegularExpressions;

namespace Seiton.Update.Parsers;

internal sealed partial class GitHubDocsRunnerLabelsMarkdownParser
{
    private static readonly Regex LabelCodeRegex = new(
        "<code>(?:\\s*<a[^>]*>)?\\s*(?<label>[A-Za-z0-9][A-Za-z0-9.-]*)\\s*(?:</a>)?\\s*</code>\\s*(?<preview>\\((?:public\\s+)?preview\\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    // Matches comma-separated labels in table cells: "macos-15-large (latest), macos-26-large"
    private static readonly Regex LargerLabelRegex = new(
        @"(?<label>macos-[A-Za-z0-9][A-Za-z0-9.-]*)\s*(?<preview>\((?:public\s+)?preview\))?",
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

    /// <summary>
    /// Parses the larger-runners reference page for macOS larger runner labels.
    /// Section: "## Available macOS larger runners and labels"
    /// </summary>
    public IReadOnlyList<RunnerLabelEntry> ParseLargerRunnerLabels(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var section = ExtractLargerRunnersSection(markdown);
        var labels = new Dictionary<string, bool>(StringComparer.Ordinal);

        // Try <code>-wrapped labels first (HTML rendered format)
        var codeMatches = LabelCodeRegex.Matches(section);
        for (var i = 0; i < codeMatches.Count; i++)
        {
            var match = codeMatches[i];
            var labelRaw = match.Groups["label"].Value.Trim();
            if (!IsHostedRunnerLabel(labelRaw))
            {
                continue;
            }

            var label = labelRaw.ToLowerInvariant();
            var isPreview = match.Groups["preview"].Success;
            labels.TryAdd(label, isPreview);
        }

        // Also try plain-text macOS labels in table cells (markdown/plain format)
        var plainMatches = LargerLabelRegex.Matches(section);
        for (var i = 0; i < plainMatches.Count; i++)
        {
            var match = plainMatches[i];
            var labelRaw = match.Groups["label"].Value.Trim();
            // Filter: only larger runner labels contain "-large" or "-xlarge"
            if (!labelRaw.Contains("-large", StringComparison.OrdinalIgnoreCase)
                && !labelRaw.Contains("-xlarge", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var label = labelRaw.ToLowerInvariant();
            var isPreview = match.Groups["preview"].Success;
            labels.TryAdd(label, isPreview);
        }

        return labels
            .OrderBy(static x => x.Key, StringComparer.Ordinal)
            .Select(static x => new RunnerLabelEntry(x.Key, x.Value))
            .ToArray();
    }

    private static string ExtractSupportedSection(string markdown)
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

    private static string ExtractLargerRunnersSection(string markdown)
    {
        var start = markdown.IndexOf("## Available macOS larger runners", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            // Fallback: try the entire document
            return markdown;
        }

        var end = markdown.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        if (end < 0)
        {
            end = markdown.Length;
        }

        return markdown[start..end];
    }

    private static bool IsHostedRunnerLabel(string value)
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
