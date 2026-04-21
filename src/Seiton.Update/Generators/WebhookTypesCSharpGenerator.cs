using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class WebhookTypesCSharpGenerator
{
    private static readonly IReadOnlyDictionary<string, string[]> OptionMap = new Dictionary<string, string[]>(StringComparer.Ordinal)
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
        AppendGeneratedHeader(sb, "sync-webhooks");
        sb.Append(
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
            if (OptionMap.TryGetValue(e.Name, out var options) && options.Length > 0)
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

        sb.Append(
            """
                            _ => ActivityTypesMode.NotSupported,
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

        sb.Append(
            """
                }
            }
            """);

        return TextNormalization.NormalizeToLf(sb.ToString());
    }

    private static void AppendGeneratedHeader(StringBuilder sb, string command)
    {
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// This file is generated by Seiton.Update. Do not edit manually.");
        sb.AppendLine($"// Regenerate: dotnet run --project src/Seiton.Update -- {command}");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();
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
