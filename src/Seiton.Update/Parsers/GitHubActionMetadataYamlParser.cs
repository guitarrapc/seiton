namespace Seiton.Update.Parsers;

internal sealed class GitHubActionMetadataYamlParser
{
    public IReadOnlyList<string> ParseInputNames(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');

        var inputsLineIndex = -1;
        var inputsIndent = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed == "inputs:")
            {
                inputsLineIndex = i;
                inputsIndent = GetIndent(line);
                break;
            }
        }

        if (inputsLineIndex < 0)
        {
            return [];
        }

        var result = new List<string>();
        for (var i = inputsLineIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= inputsIndent)
            {
                break;
            }

            if (indent != inputsIndent + 2)
            {
                continue;
            }

            var trimmed = line.Trim();
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = trimmed[..colon].Trim();
            key = TrimQuotes(key);

            if (key.Length == 0 || key == "<<")
            {
                continue;
            }

            result.Add(key);
        }

        return result
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();
    }

    private static int GetIndent(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static string TrimQuotes(string text)
    {
        if (text.Length >= 2)
        {
            if ((text[0] == '\'' && text[^1] == '\'') || (text[0] == '"' && text[^1] == '"'))
            {
                return text[1..^1];
            }
        }

        return text;
    }
}
