using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class WebhookTypesCSharpGenerator
{
    /// <summary>
    /// Builds the effective option map for each event by combining event-specific filter keys
    /// (from expected-keys data) with "types" for events that support activity types.
    /// </summary>
    internal static Dictionary<string, string[]> BuildEffectiveOptionMap(
        IReadOnlyList<WebhookEventModel> events,
        IReadOnlyDictionary<string, string[]> eventFilterKeys)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            var supportsTypes = e.ActivityTypes is null || e.ActivityTypes.Count > 0;
            var hasFilterKeys = eventFilterKeys.TryGetValue(e.Name, out var filterKeys) && filterKeys.Length > 0;

            if (!supportsTypes && !hasFilterKeys)
                continue;

            var options = new List<string>();
            if (hasFilterKeys)
                options.AddRange(filterKeys!);
            if (supportsTypes && !options.Contains("types"))
                options.Insert(0, "types");

            result[e.Name] = options.ToArray();
        }

        return result;
    }

    public string Generate(IReadOnlyList<WebhookEventModel> events, IReadOnlyDictionary<string, string[]> eventFilterKeys)
    {
        var optionMap = BuildEffectiveOptionMap(events, eventFilterKeys);

        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-webhooks");
        sb.AppendLine(
            """
            namespace Seiton.Core.Generated;

            internal static class WebhookTypes
            {
                internal enum ActivityTypesMode
                {
                    NotSupported,
                    Any,
                    Restricted,
                }

                internal enum EventId
                {
            """);
        foreach (var e in events)
        {
            sb.Append($"        {ToEventIdName(e.Name)},\n");
        }

        sb.Append(
            """
                }

                public static bool TryGet(ReadOnlySpan<byte> eventNameUtf8, out string eventName, out EventSpec spec)
                {
            """);
        foreach (var e in events)
        {
            sb.Append($"        if (eventNameUtf8.SequenceEqual(\"{e.Name}\"u8)) {{ eventName = \"{e.Name}\"; spec = new(EventId.{ToEventIdName(e.Name)}); return true; }}\n");
        }

        sb.Append(
            """

                    eventName = string.Empty;
                    spec = default;
                    return false;
                }

                internal readonly struct EventSpec
                {
                    internal EventId Id { get; }

                    public EventSpec(EventId id)
                    {
                        Id = id;
                    }

                    public bool IsTypeOptionSupported() => GetTypesMode() is not ActivityTypesMode.NotSupported;

                    public bool IsOptionAllowed(ReadOnlySpan<byte> optionUtf8)
                    {
                        return Id switch
                        {
            """);
        foreach (var e in events)
        {
            if (optionMap.TryGetValue(e.Name, out var options) && options.Length > 0)
            {
                sb.Append($"                EventId.{ToEventIdName(e.Name)} => {BuildAnyOptionCondition(options, "optionUtf8")},\n");
            }
        }

        sb.Append(
            """
                            _ => false,
                        };
                    }

                    public bool IsTypeAllowed(ReadOnlySpan<byte> valueUtf8)
                    {
                        if (GetTypesMode() is ActivityTypesMode.Any)
                        {
                            return true;
                        }

                        if (GetTypesMode() is ActivityTypesMode.NotSupported)
                        {
                            return false;
                        }

                        return Id switch
                        {
            """);

        var pullRequestTypes = events.FirstOrDefault(static x => x.Name == "pull_request")?.ActivityTypes;
        var pullRequestTargetTypes = events.FirstOrDefault(static x => x.Name == "pull_request_target")?.ActivityTypes;
        var usePullRequestHelper = pullRequestTypes is not null
            && pullRequestTargetTypes is not null
            && pullRequestTypes.SequenceEqual(pullRequestTargetTypes, StringComparer.Ordinal);

        foreach (var e in events.Where(static x => x.ActivityTypes is { Count: > 0 }))
        {
            if (usePullRequestHelper && (e.Name == "pull_request" || e.Name == "pull_request_target"))
            {
                sb.Append($"                EventId.{ToEventIdName(e.Name)} => IsPullRequestType(valueUtf8),\n");
                continue;
            }

            sb.Append($"                EventId.{ToEventIdName(e.Name)} => {BuildAnyValueCondition(e.ActivityTypes!, "valueUtf8")},\n");
        }

        sb.Append(
            """
                            _ => false,
                        };
                    }

                    private ActivityTypesMode GetTypesMode()
                    {
                        return Id switch
                        {
            """);

        foreach (var e in events.Where(static x => x.ActivityTypes is null))
        {
            sb.Append($"                EventId.{ToEventIdName(e.Name)} => ActivityTypesMode.Any,\n");
        }

        var restricted = events.Where(static x => x.ActivityTypes is { Count: > 0 }).ToArray();
        if (restricted.Length > 0)
        {
            sb.Append("                ");
            sb.Append(string.Join(" or ", restricted.Select(x => $"EventId.{ToEventIdName(x.Name)}")));
            sb.Append(" => ActivityTypesMode.Restricted,\n");
        }

        sb.AppendLine(
            """
                            _ => ActivityTypesMode.NotSupported,
                        };
                    }
            """);

        // Generate GetAllowedOptionNames: returns string[] of valid option names for suggestion
        sb.Append(
            """

                    public string[] GetAllowedOptionNames()
                    {
                        return Id switch
                        {
            """);
        foreach (var e in events)
        {
            if (optionMap.TryGetValue(e.Name, out var options) && options.Length > 0)
            {
                var arrayLiteral = string.Join(", ", options.Select(static o => $"\"{o}\""));
                sb.Append($"                EventId.{ToEventIdName(e.Name)} => [{arrayLiteral}],\n");
            }
        }

        sb.AppendLine(
            """
                            _ => [],
                        };
                    }
            """);

        if (usePullRequestHelper && pullRequestTypes is not null)
        {
            sb.Append(
                $$"""

                        private static bool IsPullRequestType(ReadOnlySpan<byte> value)
                        {
                            return {{BuildAnyValueCondition(pullRequestTypes, "value")}};
                        }
                """);
        }

        sb.AppendLine(
            """
                }
            """);

        // Generate GetEventsForFilter: reverse lookup from filter key → comma-separated event list
        AppendGetEventsForFilter(sb, optionMap);

        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
    }

    private static void AppendGetEventsForFilter(StringBuilder sb, Dictionary<string, string[]> optionMap)
    {
        // Build reverse lookup: filter key → sorted event names
        // Exclude non-filter keys (types, inputs, outputs, secrets)
        var excludedKeys = new HashSet<string>(StringComparer.Ordinal) { "types", "inputs", "outputs", "secrets" };
        var reverseMap = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var (eventName, options) in optionMap)
        {
            foreach (var option in options)
            {
                if (excludedKeys.Contains(option))
                    continue;

                if (!reverseMap.TryGetValue(option, out var eventSet))
                {
                    eventSet = new SortedSet<string>(StringComparer.Ordinal);
                    reverseMap[option] = eventSet;
                }

                eventSet.Add(eventName);
            }
        }

        // Group filters that share the same event set (e.g., branches and branches-ignore)
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (filter, eventSet) in reverseMap.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            var eventsString = string.Join(", ", eventSet);
            if (!groups.TryGetValue(eventsString, out var filters))
            {
                filters = [];
                groups[eventsString] = filters;
            }

            filters.Add(filter);
        }

        sb.AppendLine();
        sb.AppendLine("    /// <summary>Returns a comma-separated list of events that support the given filter option.</summary>");
        sb.AppendLine("    internal static string GetEventsForFilter(string filterName)");
        sb.AppendLine("    {");
        sb.AppendLine("        return filterName switch");
        sb.AppendLine("        {");

        foreach (var (eventsString, filters) in groups.OrderBy(static x => x.Value[0], StringComparer.Ordinal))
        {
            var pattern = string.Join(" or ", filters.Select(static f => $"\"{f}\""));
            sb.AppendLine($"            {pattern} => \"{eventsString}\",");
        }

        sb.AppendLine("            _ => string.Empty,");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
    }

    private static string BuildAnyOptionCondition(IReadOnlyList<string> options, string variableName)
    {
        return string.Join(" || ", options.Select(o => $"{variableName}.SequenceEqual(\"{o}\"u8)"));
    }

    private static string BuildAnyValueCondition(IReadOnlyList<string> values, string variableName)
    {
        return string.Join(" || ", values.Select(v => $"{variableName}.SequenceEqual(\"{v}\"u8)"));
    }

    private static string ToEventIdName(string eventName)
    {
        var parts = eventName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            sb.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
            {
                sb.Append(part.AsSpan(1));
            }
        }

        return sb.ToString();
    }
}
