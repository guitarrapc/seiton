using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public sealed class DangerousTriggersRule : RuleBase
{
    static readonly WebhookTypes.EventId[] DangerousEventIds =
    [
        WebhookTypes.EventId.PullRequestTarget,
        WebhookTypes.EventId.WorkflowRun,
    ];

    public override string Id => "dangerous-triggers";

    public override string Name => "Dangerous Triggers Rule";

    public override void VisitEvent(Event ev)
    {
        if (ev is not WebhookEvent webhookEv || Config.Utf8Yaml is null)
        {
            return;
        }

        var eventNameSpan = webhookEv.EventName.Value.AsSpan(Config.Utf8Yaml);
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
}
