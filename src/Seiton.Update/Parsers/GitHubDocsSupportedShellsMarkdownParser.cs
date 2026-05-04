namespace Seiton.Update.Parsers;

/// <summary>
/// Parses the supported shells table from GitHub Docs
/// <c>data/reusables/actions/supported-shells.md</c> (included from workflow-syntax via Liquid).
/// </summary>
internal sealed class GitHubDocsSupportedShellsMarkdownParser
{
    public IReadOnlyList<ShellTableMergeEntry> Parse(string markdown)
    {
        var normalized = TextNormalization.NormalizeToLf(markdown);
        var lines = normalized.Split('\n');

        var headerIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Contains("| Supported platform |", StringComparison.Ordinal)
                && line.Contains("shell` parameter |", StringComparison.Ordinal))
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0)
        {
            throw new InvalidDataException(
                "supported-shells markdown: could not locate the supported shells table header.");
        }

        if (headerIndex + 1 >= lines.Length
            || !IsMarkdownTableSeparatorRow(lines[headerIndex + 1].Trim()))
        {
            throw new InvalidDataException(
                "supported-shells markdown: missing markdown table separator after header.");
        }

        var byName = new Dictionary<string, ShellMerger>(StringComparer.Ordinal);
        for (var i = headerIndex + 2; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                break;
            }

            if (!trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                break;
            }

            if (IsMarkdownTableSeparatorRow(trimmed))
            {
                continue;
            }

            var columns = trimmed.Split('|');
            // Leading/trailing empty segments: | a | b | -> "", " a ", " b ", ""
            if (columns.Length < 6)
            {
                continue;
            }

            var platformCell = NormalizeCell(columns[1]);
            var shellCell = NormalizeCell(columns[2]);
            var commandCell = NormalizeCell(columns[^2]);

            if (string.IsNullOrWhiteSpace(shellCell)
                || shellCell.Equals("unspecified", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var platforms = ParsePlatforms(platformCell);
            if (platforms.Count == 0)
            {
                continue;
            }

            var command = NormalizeCommand(commandCell);
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new InvalidDataException(
                    $"supported-shells markdown: empty command for shell '{shellCell}'.");
            }

            if (!byName.TryGetValue(shellCell, out var merger))
            {
                byName[shellCell] = new ShellMerger(shellCell, command, new HashSet<string>(platforms));
            }
            else
            {
                if (!string.Equals(merger.Command, command, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"supported-shells markdown: conflicting commands for shell '{shellCell}'.");
                }

                foreach (var p in platforms)
                {
                    merger.Platforms.Add(p);
                }
            }
        }

        if (byName.Count == 0)
        {
            throw new InvalidDataException(
                "supported-shells markdown: table contained no usable shell rows.");
        }

        return byName.Values
            .Select(static m => new ShellTableMergeEntry(
                m.Name,
                m.Platforms.OrderBy(static p => p, StringComparer.Ordinal).ToArray(),
                m.Command))
            .OrderBy(static e => e.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsMarkdownTableSeparatorRow(string trimmedLine)
    {
        if (!trimmedLine.StartsWith("|", StringComparison.Ordinal))
        {
            return false;
        }

        // Require dashed third column like "| -- | --- |" tables use.
        return trimmedLine.Contains("---", StringComparison.Ordinal);
    }

    private static string NormalizeCell(string cell) =>
        cell.Replace("`", string.Empty, StringComparison.Ordinal).Trim();

    private static string NormalizeCommand(string cell)
    {
        var s = cell.Trim().Replace("`", string.Empty, StringComparison.Ordinal).Trim();
        while (s.Length >= 2 && s[^1] == '.' && s[^2] == '"')
        {
            s = s[..^1];
        }

        return s.TrimEnd();
    }

    private static IReadOnlyList<string> ParsePlatforms(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
        {
            return [];
        }

        if (cell.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            return ["linux", "macos", "windows"];
        }

        var hasLinux = cell.Contains("Linux", StringComparison.OrdinalIgnoreCase);
        var hasMac = cell.Contains("macOS", StringComparison.OrdinalIgnoreCase)
            || cell.Contains("macos", StringComparison.OrdinalIgnoreCase);
        var hasWindows = cell.Contains("Windows", StringComparison.OrdinalIgnoreCase);

        if (hasLinux && hasMac)
        {
            return ["linux", "macos"];
        }

        if (hasWindows && !hasLinux && !hasMac)
        {
            return ["windows"];
        }

        return [];
    }

    private sealed class ShellMerger(string name, string command, HashSet<string> platforms)
    {
        internal string Name { get; } = name;
        internal string Command { get; } = command;
        internal HashSet<string> Platforms { get; } = platforms;
    }
}

internal readonly record struct ShellTableMergeEntry(
    string Name,
    IReadOnlyList<string> Platforms,
    string Command);
