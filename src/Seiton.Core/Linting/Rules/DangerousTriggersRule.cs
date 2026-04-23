using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

using static Seiton.Core.Linting.RuleConfigHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class DangerousTriggersRule : RuleBase
{
    private static readonly WebhookTypes.EventId[] DangerousEventIds =
    [
        WebhookTypes.EventId.PullRequestTarget,
        WebhookTypes.EventId.WorkflowRun,
    ];

    private HashSet<string> additionalDangerousEvents = [];

    public override string Id => "dangerous-triggers";

    public override string Name => "Dangerous Triggers Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalDangerousEvents = config.GetRuleConfig(Id)?.Events?.Extend is { Count: > 0 } events
            ? BuildNormalizedSet(events)
            : [];
    }

    public override void VisitEvent(Event ev)
    {
        if (ev is not WebhookEvent webhookEv || Config.Utf8Yaml is null)
        {
            return;
        }

        var eventNameSpan = Arena.GetStringValue(webhookEv.EventName);
        if (IsAdditionalDangerousEvent(eventNameSpan))
        {
            var configuredEventName = Decode(Arena.GetStringSlice(webhookEv.EventName));
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

            var eventName = Decode(Arena.GetStringSlice(webhookEv.EventName));
            AddEventWarning(webhookEv, $"event '{eventName}' is potentially dangerous and may allow privilege escalation from a pull request");
            return;
        }
    }

    private bool IsAdditionalDangerousEvent(ReadOnlySpan<byte> eventNameSpan)
    {
        if (additionalDangerousEvents.Count == 0)
        {
            return false;
        }

        return additionalDangerousEvents.Contains(NormalizeAsciiLower(eventNameSpan));
    }
}
