using System.Text.RegularExpressions;
using Seiton.Update.Model;

namespace Seiton.Update.Services;

internal sealed class WebhookParityChecker
{
    static readonly Regex SeitonEventRegex = new("eventNameUtf8\\.SequenceEqual\\(\"([^\"]+)\"u8\\)", RegexOptions.Compiled);
    static readonly Regex ActionlintEventRegex = new("^\\s*\"([^\"]+)\":", RegexOptions.Compiled | RegexOptions.Multiline);

    public bool TryCompare(string repoRoot, out WebhookDiffResult diff)
    {
        if (!WebhookSourcePathResolver.TryResolveActionlintReference(repoRoot, out var actionlintPath))
        {
            diff = new WebhookDiffResult([], []);
            return false;
        }

        var seitonPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "WebhookTypes.g.cs");

        var seitonEvents = ParseSeitonEvents(seitonPath);
        var actionlintEvents = ParseActionlintEvents(actionlintPath);

        var missingInSeiton = actionlintEvents.Except(seitonEvents, StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var extraInSeiton = seitonEvents.Except(actionlintEvents, StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();

        diff = new WebhookDiffResult(missingInSeiton, extraInSeiton);
        return true;
    }

    static HashSet<string> ParseSeitonEvents(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Seiton generated webhook table not found.", path);
        }

        var text = File.ReadAllText(path);
        var matches = SeitonEventRegex.Matches(text);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                set.Add(match.Groups[1].Value);
            }
        }

        return set;
    }

    static HashSet<string> ParseActionlintEvents(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("actionlint webhook table not found.", path);
        }

        var text = File.ReadAllText(path);
        var matches = ActionlintEventRegex.Matches(text);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                set.Add(match.Groups[1].Value);
            }
        }

        return set;
    }
}
