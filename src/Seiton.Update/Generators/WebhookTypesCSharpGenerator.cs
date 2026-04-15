using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class WebhookTypesCSharpGenerator
{
    static readonly IReadOnlyDictionary<string, string[]> OptionMap = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["branch_protection_rule"] = ["types"],
        ["check_run"] = ["types"],
        ["check_suite"] = ["types"],
        ["discussion"] = ["types"],
        ["discussion_comment"] = ["types"],
        ["push"] = ["branches", "branches-ignore", "tags", "tags-ignore", "paths", "paths-ignore"],
        ["label"] = ["types"],
        ["merge_group"] = ["types", "branches", "branches-ignore"],
        ["milestone"] = ["types"],
        ["pull_request"] = ["types", "branches", "branches-ignore", "paths", "paths-ignore"],
        ["pull_request_review"] = ["types"],
        ["pull_request_review_comment"] = ["types"],
        ["pull_request_target"] = ["types", "branches", "branches-ignore", "paths", "paths-ignore"],
        ["workflow_dispatch"] = ["inputs"],
        ["workflow_call"] = ["inputs", "secrets", "outputs"],
        ["workflow_run"] = ["workflows", "types", "branches", "branches-ignore"],
        ["release"] = ["types"],
        ["registry_package"] = ["types"],
        ["issues"] = ["types"],
        ["issue_comment"] = ["types"],
        ["repository_dispatch"] = ["types"],
        ["watch"] = ["types"],
    };

    public string Generate(IReadOnlyList<WebhookEventModel> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("namespace Seiton.Core.Generated;");
        sb.AppendLine();
        sb.AppendLine("internal static class WebhookTypes");
        sb.AppendLine("{");
        sb.AppendLine("    internal enum ActivityTypesMode");
        sb.AppendLine("    {");
        sb.AppendLine("        NotSupported,");
        sb.AppendLine("        Any,");
        sb.AppendLine("        Restricted,");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal enum EventId");
        sb.AppendLine("    {");
        foreach (var e in events)
        {
            sb.AppendLine($"        {ToEventIdName(e.Name)},");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static bool TryGet(ReadOnlySpan<byte> eventNameUtf8, out string eventName, out EventSpec spec)");
        sb.AppendLine("    {");
        foreach (var e in events)
        {
            sb.AppendLine($"        if (eventNameUtf8.SequenceEqual(\"{e.Name}\"u8)) {{ eventName = \"{e.Name}\"; spec = new(EventId.{ToEventIdName(e.Name)}); return true; }}");
        }

        sb.AppendLine();
        sb.AppendLine("        eventName = string.Empty;");
        sb.AppendLine("        spec = default;");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal readonly struct EventSpec");
        sb.AppendLine("    {");
        sb.AppendLine("        internal EventId Id { get; }");
        sb.AppendLine();
        sb.AppendLine("        public EventSpec(EventId id)");
        sb.AppendLine("        {");
        sb.AppendLine("            Id = id;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public bool IsTypeOptionSupported() => GetTypesMode() is not ActivityTypesMode.NotSupported;");
        sb.AppendLine();
        sb.AppendLine("        public bool IsOptionAllowed(ReadOnlySpan<byte> optionUtf8)");
        sb.AppendLine("        {");
        sb.AppendLine("            return Id switch");
        sb.AppendLine("            {");
        foreach (var e in events)
        {
            if (OptionMap.TryGetValue(e.Name, out var options) && options.Length > 0)
            {
                sb.AppendLine($"                EventId.{ToEventIdName(e.Name)} => {BuildAnyOptionCondition(options, "optionUtf8")},");
            }
        }

        sb.AppendLine("                _ => false,");
        sb.AppendLine("            };");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public bool IsTypeAllowed(ReadOnlySpan<byte> valueUtf8)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (GetTypesMode() is ActivityTypesMode.Any)");
        sb.AppendLine("            {");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (GetTypesMode() is ActivityTypesMode.NotSupported)");
        sb.AppendLine("            {");
        sb.AppendLine("                return false;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            return Id switch");
        sb.AppendLine("            {");

        var pullRequestTypes = events.FirstOrDefault(static x => x.Name == "pull_request")?.ActivityTypes;
        var pullRequestTargetTypes = events.FirstOrDefault(static x => x.Name == "pull_request_target")?.ActivityTypes;
        var usePullRequestHelper = pullRequestTypes is not null
            && pullRequestTargetTypes is not null
            && pullRequestTypes.SequenceEqual(pullRequestTargetTypes, StringComparer.Ordinal);

        foreach (var e in events.Where(static x => x.ActivityTypes is { Count: > 0 }))
        {
            if (usePullRequestHelper && (e.Name == "pull_request" || e.Name == "pull_request_target"))
            {
                sb.AppendLine($"                EventId.{ToEventIdName(e.Name)} => IsPullRequestType(valueUtf8),");
                continue;
            }

            sb.AppendLine($"                EventId.{ToEventIdName(e.Name)} => {BuildAnyValueCondition(e.ActivityTypes!, "valueUtf8")},");
        }

        sb.AppendLine("                _ => false,");
        sb.AppendLine("            };");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        private ActivityTypesMode GetTypesMode()");
        sb.AppendLine("        {");
        sb.AppendLine("            return Id switch");
        sb.AppendLine("            {");

        foreach (var e in events.Where(static x => x.ActivityTypes is null))
        {
            sb.AppendLine($"                EventId.{ToEventIdName(e.Name)} => ActivityTypesMode.Any,");
        }

        var restricted = events.Where(static x => x.ActivityTypes is { Count: > 0 }).ToArray();
        if (restricted.Length > 0)
        {
            sb.Append("                ");
            sb.Append(string.Join(" or ", restricted.Select(x => $"EventId.{ToEventIdName(x.Name)}")));
            sb.AppendLine(" => ActivityTypesMode.Restricted,");
        }

        sb.AppendLine("                _ => ActivityTypesMode.NotSupported,");
        sb.AppendLine("            };");
        sb.AppendLine("        }");

        if (usePullRequestHelper && pullRequestTypes is not null)
        {
            sb.AppendLine();
            sb.AppendLine("        private static bool IsPullRequestType(ReadOnlySpan<byte> value)");
            sb.AppendLine("        {");
            sb.AppendLine($"            return {BuildAnyValueCondition(pullRequestTypes, "value")};");
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString().Replace("\r\n", "\n");
    }

    static string BuildAnyOptionCondition(IReadOnlyList<string> options, string variableName)
    {
        return string.Join(" || ", options.Select(o => $"{variableName}.SequenceEqual(\"{o}\"u8)"));
    }

    static string BuildAnyValueCondition(IReadOnlyList<string> values, string variableName)
    {
        return string.Join(" || ", values.Select(v => $"{variableName}.SequenceEqual(\"{v}\"u8)"));
    }

    static string ToEventIdName(string eventName)
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
