namespace Seiton.Update.Parsers;

internal sealed class IanaTimeZonesZiParser
{
    /// <summary>
    /// Parses a tzdata.zi file and extracts all Zone names and Link targets.
    /// Both are valid IANA timezone identifiers.
    /// </summary>
    public IanaTimeZonesParseResult Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new IanaTimeZonesParseResult(string.Empty, [], []);
        }

        var version = string.Empty;
        var zones = new List<string>();
        var links = new List<string>();

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            // Extract version from comment like "# version 2024a" or "# tzdb version = 2024b"
            if (line.StartsWith("# version", StringComparison.Ordinal))
            {
                var versionPart = line.AsSpan()["# version".Length..].Trim();
                if (versionPart.Length > 0)
                {
                    version = versionPart.ToString();
                }
            }

            // Zone definition: Z <zone-name> <rest...>
            if (line.StartsWith("Z ", StringComparison.Ordinal))
            {
                var rest = line.AsSpan()[2..];
                var spaceIdx = rest.IndexOf(' ');
                if (spaceIdx > 0)
                {
                    var zoneName = rest[..spaceIdx].ToString();
                    if (zoneName.Contains('/') || zoneName.StartsWith("Etc/", StringComparison.Ordinal) || zoneName == "EST" || zoneName == "MST" || zoneName == "HST" || zoneName.StartsWith("Etc/", StringComparison.Ordinal))
                    {
                        zones.Add(zoneName);
                    }
                    else
                    {
                        zones.Add(zoneName);
                    }
                }
            }

            // Link definition: L <target> <link-name>
            if (line.StartsWith("L ", StringComparison.Ordinal))
            {
                var rest = line.AsSpan()[2..];
                var spaceIdx = rest.IndexOf(' ');
                if (spaceIdx > 0)
                {
                    var remaining = rest[(spaceIdx + 1)..].Trim();
                    var endIdx = remaining.IndexOf(' ');
                    var linkName = endIdx > 0 ? remaining[..endIdx].ToString() : remaining.ToString();
                    if (!string.IsNullOrEmpty(linkName))
                    {
                        links.Add(linkName);
                    }
                }
            }
        }

        return new IanaTimeZonesParseResult(
            version,
            zones.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToList(),
            links.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToList());
    }

    internal sealed record IanaTimeZonesParseResult(
        string Version,
        IReadOnlyList<string> Zones,
        IReadOnlyList<string> Links);
}
