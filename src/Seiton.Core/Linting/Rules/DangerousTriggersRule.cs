using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

using static Seiton.Core.Linting.RuleConfigHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags workflows triggered by dangerous events (e.g. <c>pull_request_target</c>, <c>workflow_run</c>) that may execute untrusted code.</summary>
public sealed class DangerousTriggersRule() : RuleBase(RuleId.DangerousTriggers)
{
    private static readonly WebhookTypes.EventId[] DangerousEventIds =
    [
        WebhookTypes.EventId.PullRequestTarget,
        WebhookTypes.EventId.WorkflowRun,
    ];

    private HashSet<string> additionalDangerousEvents = [];

    public override string Name => "Dangerous Triggers Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalDangerousEvents = config.GetRuleConfig(Id)?.Events is { Count: > 0 } events
            ? BuildNormalizedSet(events)
            : [];
    }

    public override void VisitEvent(EventRef ev)
    {
        if (ev.Kind != EventKind.Webhook || Config.Utf8Yaml is null)
        {
            return;
        }

        var eventNameSpan = ev.EventName.Value;
        if (IsAdditionalDangerousEvent(eventNameSpan))
        {
            var configuredEventName = ev.EventName.Decode();
            AddEventWarning(ev, $"event '{configuredEventName}' is potentially dangerous and may allow privilege escalation from a pull request");
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

            var eventName = ev.EventName.Decode();
            AddEventWarning(ev, $"event '{eventName}' is potentially dangerous and may allow privilege escalation from a pull request");
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
