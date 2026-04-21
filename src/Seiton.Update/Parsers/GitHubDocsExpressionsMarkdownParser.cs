namespace Seiton.Update.Parsers;

internal sealed class GitHubDocsExpressionsMarkdownParser
{
    /// <summary>
    /// Parse function names from GitHub Docs expressions.md.
    /// Extracts H3 headings under the "## Functions" and "## Status check functions" sections.
    /// </summary>
    public IReadOnlyList<string> ParseFunctionNames(string markdown)
    {
        var normalized = TextNormalization.NormalizeToLf(markdown);
        var lines = normalized.Split('\n');

        var names = new List<string>();
        var inFunctionSection = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();

            // Check for H2 section boundaries
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var sectionName = line[3..].Trim();
                inFunctionSection = sectionName.Equals("Functions", StringComparison.OrdinalIgnoreCase)
                    || sectionName.Equals("Status check functions", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            // Extract H3 heading as function name while inside a function section
            if (inFunctionSection && line.StartsWith("### ", StringComparison.Ordinal))
            {
                var heading = line[4..].Trim();

                // Skip "Example" subheadings (e.g. "### Example of `startsWith`", "### Example with a single predicate")
                if (heading.StartsWith("Example", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Normalize to lowercase for comparison
                var name = heading.ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToList();
    }
}
