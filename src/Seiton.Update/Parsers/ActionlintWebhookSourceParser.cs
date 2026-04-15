using System.Text.RegularExpressions;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class ActionlintWebhookSourceParser
{
    static readonly Regex EntryRegex = new(
        "^\\s*\"(?<name>[^\"]+)\"\\s*:\\s*(?<value>nil|\\{.*\\})\\s*,?\\s*$",
        RegexOptions.Compiled);

    static readonly Regex QuotedValueRegex = new("\"([^\"]+)\"", RegexOptions.Compiled);

    public IReadOnlyList<WebhookEventModel> Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("actionlint webhook source not found.", path);
        }

        var list = new List<WebhookEventModel>();
        foreach (var line in File.ReadLines(path))
        {
            var m = EntryRegex.Match(line);
            if (!m.Success)
            {
                continue;
            }

            var name = m.Groups["name"].Value;
            var rawValue = m.Groups["value"].Value;

            IReadOnlyList<string>? activityTypes;
            if (rawValue == "nil")
            {
                activityTypes = null;
            }
            else
            {
                var matches = QuotedValueRegex.Matches(rawValue);
                var values = new List<string>(matches.Count);
                foreach (Match vm in matches)
                {
                    if (vm.Groups.Count > 1)
                    {
                        values.Add(vm.Groups[1].Value);
                    }
                }

                activityTypes = values;
            }

            list.Add(new WebhookEventModel(name, activityTypes));
        }

        return list.OrderBy(static x => x.Name, StringComparer.Ordinal).ToArray();
    }
}
