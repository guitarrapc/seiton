using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class GitHubActionMetadataYamlParser
{
    public IReadOnlyList<string> ParseInputNames(string yaml)
    {
        return ParseInputs(yaml).Select(static x => x.Name).ToArray();
    }

    public IReadOnlyList<PopularActionInputModel> ParseInputs(string yaml)
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

        var result = new List<(string Name, bool Required, bool HasDefault, string? DeprecationMessage)>();
        string? currentKey = null;
        var currentRequired = false;
        var currentHasDefault = false;
        string? currentDeprecationMessage = null;

        for (var i = inputsLineIndex + 1; i <= lines.Length; i++)
        {
            var line = i < lines.Length ? lines[i] : null;
            var isEnd = line is null || string.IsNullOrWhiteSpace(line) is false && GetIndent(line) <= inputsIndent && !line.TrimStart().StartsWith("#", StringComparison.Ordinal);
            var isInputKey = line is not null && !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#", StringComparison.Ordinal) && GetIndent(line) == inputsIndent + 2;

            // When we hit a new input key or the end of inputs section, flush previous input
            if ((isInputKey || isEnd) && currentKey is not null)
            {
                // required: true with no default means truly required
                result.Add((currentKey, currentRequired && !currentHasDefault, currentHasDefault, currentDeprecationMessage));
                currentKey = null;
                currentRequired = false;
                currentHasDefault = false;
                currentDeprecationMessage = null;
            }

            if (isEnd)
            {
                break;
            }

            if (line is null || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var indent = GetIndent(line);

            if (indent == inputsIndent + 2)
            {
                // This is an input key line
                var trimmed2 = line.Trim();
                var colon = trimmed2.IndexOf(':');
                if (colon <= 0) continue;

                var key = trimmed2[..colon].Trim();
                key = TrimQuotes(key);
                if (key.Length == 0 || key == "<<") continue;

                currentKey = key;
                currentRequired = false;
                currentHasDefault = false;
                currentDeprecationMessage = null;
            }
            else if (indent == inputsIndent + 4 && currentKey is not null)
            {
                // Sub-property of the current input
                var trimmed2 = line.Trim();
                if (trimmed2.StartsWith("required:", StringComparison.OrdinalIgnoreCase))
                {
                    var val = trimmed2["required:".Length..].Trim().Trim('\'', '"');
                    currentRequired = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                }
                else if (trimmed2.StartsWith("default:", StringComparison.OrdinalIgnoreCase))
                {
                    currentHasDefault = true;
                }
                else if (trimmed2.StartsWith("deprecationMessage:", StringComparison.OrdinalIgnoreCase))
                {
                    var val = trimmed2["deprecationMessage:".Length..].Trim().Trim('\'', '"');
                    if (val is "|" or ">" or "|+" or ">+" or "|-" or ">-" or "|2" or ">2")
                    {
                        // Block scalar: read continuation lines
                        var blockLines = new List<string>();
                        for (var j = i + 1; j < lines.Length; j++)
                        {
                            var blockLine = lines[j];
                            if (string.IsNullOrWhiteSpace(blockLine))
                            {
                                break;
                            }

                            var blockIndent = GetIndent(blockLine);
                            if (blockIndent <= inputsIndent + 4)
                            {
                                break;
                            }

                            blockLines.Add(blockLine.Trim());
                        }

                        currentDeprecationMessage = blockLines.Count > 0 ? string.Join(" ", blockLines).TrimEnd('.') : null;
                    }
                    else if (!string.IsNullOrWhiteSpace(val))
                    {
                        currentDeprecationMessage = val.TrimEnd('.');
                    }
                }
            }
        }

        return result
            .DistinctBy(static x => x.Name, StringComparer.Ordinal)
            .OrderBy(static x => x.Name, StringComparer.Ordinal)
            .Select(static x => new PopularActionInputModel(x.Name, x.Required, x.DeprecationMessage))
            .ToArray();
    }

    public IReadOnlyList<PopularActionOutputModel> ParseOutputs(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');

        var outputsLineIndex = -1;
        var outputsIndent = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed == "outputs:")
            {
                outputsLineIndex = i;
                outputsIndent = GetIndent(line);
                break;
            }
        }

        if (outputsLineIndex < 0)
        {
            return [];
        }

        var result = new List<string>();

        for (var i = outputsLineIndex + 1; i < lines.Length; i++)
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
            if (indent <= outputsIndent)
            {
                break;
            }

            if (indent == outputsIndent + 2)
            {
                var trimmed2 = line.Trim();
                var colon = trimmed2.IndexOf(':');
                if (colon <= 0) continue;

                var key = trimmed2[..colon].Trim();
                key = TrimQuotes(key);
                if (key.Length == 0 || key == "<<") continue;

                result.Add(key);
            }
        }

        return result
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .Select(static x => new PopularActionOutputModel(x))
            .ToArray();
    }

    public string ParseRunsUsing(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');

        var runsLineIndex = -1;
        var runsIndent = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed == "runs:")
            {
                runsLineIndex = i;
                runsIndent = GetIndent(line);
                break;
            }
        }

        if (runsLineIndex < 0)
        {
            return string.Empty;
        }

        for (var i = runsLineIndex + 1; i < lines.Length; i++)
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
            if (indent <= runsIndent)
            {
                break;
            }

            if (indent == runsIndent + 2)
            {
                var trimmed2 = line.Trim();
                if (trimmed2.StartsWith("using:", StringComparison.OrdinalIgnoreCase))
                {
                    var val = trimmed2["using:".Length..].Trim();
                    return TrimQuotes(val);
                }
            }
        }

        return string.Empty;
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
