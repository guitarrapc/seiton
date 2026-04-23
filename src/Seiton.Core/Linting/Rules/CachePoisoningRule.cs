using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

using static Seiton.Core.Linting.RuleConfigHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class CachePoisoningRule : RuleBase
{
    private bool hasUntrustedTrigger;
    private HashSet<string> additionalUntrustedTriggers = [];

    public override string Id => "cache-poisoning";

    public override string Name => "Cache Poisoning Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalUntrustedTriggers = config.GetRuleConfig(Id)?.UntrustedTriggers?.Extend is { Count: > 0 } triggers
            ? BuildNormalizedSet(triggers)
            : [];
    }

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        hasUntrustedTrigger = Config.Utf8Yaml is not null && HasUntrustedTrigger(workflow, Config.Utf8Yaml, additionalUntrustedTriggers);
    }

    public override void VisitStep(Step step)
    {
        if (!hasUntrustedTrigger || step.Exec is not ExecAction action || Config.Utf8Yaml is null)
        {
            return;
        }

        var uses = Arena.GetStringValue(action.Uses);
        if (!IsCacheAction(uses))
        {
            return;
        }

        var actionRef = Decode(Arena.GetStringSlice(action.Uses));
        AddStepWarning(
            step,
            $"cache action '{actionRef}' runs in a workflow with untrusted triggers; isolate cache scope and avoid restore-key fallback across trust boundaries",
            BuildUsesLocation(action));
    }

    private bool HasUntrustedTrigger(Workflow workflow, byte[] utf8Yaml, HashSet<string> additionalUntrustedTriggers)
    {
        for (var i = 0; i < workflow.On.Count; i++)
        {
            if (workflow.On[i] is not WebhookEvent webhook)
            {
                continue;
            }

            var hook = Arena.GetStringValue(webhook.Hook);
            if (WebhookTypes.TryGet(hook, out _, out var spec)
                && spec.Id is WebhookTypes.EventId.PullRequest
                    or WebhookTypes.EventId.PullRequestTarget
                    or WebhookTypes.EventId.WorkflowRun)
            {
                return true;
            }

            if (IsAdditionalUntrustedTrigger(hook, additionalUntrustedTriggers))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAdditionalUntrustedTrigger(ReadOnlySpan<byte> hook, HashSet<string> additionalUntrustedTriggers)
    {
        if (additionalUntrustedTriggers.Count == 0)
        {
            return false;
        }

        return additionalUntrustedTriggers.Contains(NormalizeAsciiLower(hook));
    }
    private static bool IsCacheAction(ReadOnlySpan<byte> uses)
    {
        return IsActionReference(uses, "actions/cache"u8)
            || IsActionReference(uses, "actions/cache/restore"u8)
            || IsActionReference(uses, "actions/cache/save"u8);
    }

    private static bool IsActionReference(ReadOnlySpan<byte> uses, ReadOnlySpan<byte> actionName)
    {
        var at = uses.IndexOf((byte)'@');
        if (at <= 0)
        {
            return false;
        }

        return EqualsAsciiIgnoreCase(uses[..at], actionName);
    }
}
