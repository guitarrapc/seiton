using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class DangerousTriggersRule : RuleBase
{
    static readonly WebhookTypes.EventId[] DangerousEventIds =
    [
        WebhookTypes.EventId.PullRequestTarget,
        WebhookTypes.EventId.WorkflowRun,
    ];

    HashSet<string>? additionalDangerousEvents;

    public override string Id => "dangerous-triggers";

    public override string Name => "Dangerous Triggers Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalDangerousEvents = config.GetRuleConfig(Id)?.Specific is DangerousTriggersSpecificConfig specific
            ? BuildNormalizedSet(specific.Events)
            : BuildNormalizedSet(config.GetRuleConfig(Id)?.Events?.Extend);
    }

    public override void VisitEvent(Event ev)
    {
        if (ev is not WebhookEvent webhookEv || Config.Utf8Yaml is null)
        {
            return;
        }

        var eventNameSpan = webhookEv.EventName.Value.AsSpan(Config.Utf8Yaml);
        if (IsAdditionalDangerousEvent(eventNameSpan))
        {
            var configuredEventName = Decode(webhookEv.EventName.Value);
            AddEventWarning(webhookEv, $"event '{configuredEventName}' is potentially dangerous and may allow privilege escalation from a pull request");
            return;
        }

        if (!WebhookTypes.TryGet(eventNameSpan, out _, out var spec))
        {
            return;
        }

        for (var i = 0; i < DangerousEventIds.Length; i++)
        {
            if (spec.Id != DangerousEventIds[i])
            {
                continue;
            }

            var eventName = Decode(webhookEv.EventName.Value);
            AddEventWarning(webhookEv, $"event '{eventName}' is potentially dangerous and may allow privilege escalation from a pull request");
            return;
        }
    }

    bool IsAdditionalDangerousEvent(ReadOnlySpan<byte> eventNameSpan)
    {
        if (additionalDangerousEvents is null || additionalDangerousEvents.Count == 0)
        {
            return false;
        }

        return additionalDangerousEvents.Contains(NormalizeAsciiLower(eventNameSpan));
    }

    static HashSet<string>? BuildNormalizedSet(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    static string NormalizeAsciiLower(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = (char)value[i];
            if (ch is >= 'A' and <= 'Z')
            {
                ch = (char)(ch + 32);
            }

            buffer[i] = ch;
        }

        return new string(buffer);
    }
}
