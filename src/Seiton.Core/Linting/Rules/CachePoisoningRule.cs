using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

using static Seiton.Core.Linting.RuleConfigHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>
/// Detects cache-poisoning risks where low-trust triggers use write-capable cache actions
/// on the default-branch cache scope. Aligns with GitHub Actions read-only cache tokens for
/// low-trust workflow triggers (see dependency caching reference).
/// </summary>
public sealed class CachePoisoningRule() : RuleBase(RuleId.CachePoisoning)
{
    private bool hasLowTrustTrigger;
    private HashSet<string> additionalLowTrustTriggers = [];

    public override string Name => "Cache Poisoning Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalLowTrustTriggers = config.GetRuleConfig(Id)?.UntrustedTriggers is { Count: > 0 } triggers
            ? BuildNormalizedSet(triggers)
            : [];
    }

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        hasLowTrustTrigger = Config.Utf8Yaml is not null
            && HasLowTrustTrigger(workflow, additionalLowTrustTriggers);
    }

    public override void VisitStep(StepRef step)
    {
        if (!hasLowTrustTrigger || step.Exec.Kind != StepExecKind.Action || Config.Utf8Yaml is null)
        {
            return;
        }

        var action = step.Exec.AsAction();
        var uses = action.Uses.Value;
        if (!IsWriteCapableCacheAction(uses))
        {
            return;
        }

        var actionRef = action.Uses.Decode();
        AddStepWarning(
            step,
            $"write-capable cache action '{actionRef}' runs in a workflow with low-trust triggers; use actions/cache/restore on low-trust events or save caches from trusted triggers only (push, schedule, workflow_dispatch, etc.)",
            BuildUsesLocation(action));
    }

    private bool HasLowTrustTrigger(WorkflowRef workflow, HashSet<string> additionalLowTrustTriggers)
    {
        for (var i = 0; i < workflow.On.Count; i++)
        {
            if (workflow.On[i].Kind != EventKind.Webhook)
            {
                continue;
            }

            var webhook = workflow.On[i].AsWebhook();
            var hook = webhook.Hook.Value;
            if (WebhookTypes.TryGet(hook, out _, out var spec)
                && spec.Id is WebhookTypes.EventId.PullRequestTarget
                    or WebhookTypes.EventId.WorkflowRun
                    or WebhookTypes.EventId.IssueComment)
            {
                return true;
            }

            if (IsAdditionalLowTrustTrigger(hook, additionalLowTrustTriggers))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAdditionalLowTrustTrigger(ReadOnlySpan<byte> hook, HashSet<string> additionalLowTrustTriggers)
    {
        if (additionalLowTrustTriggers.Count == 0)
        {
            return false;
        }

        return additionalLowTrustTriggers.Contains(NormalizeAsciiLower(hook));
    }

    private static bool IsWriteCapableCacheAction(ReadOnlySpan<byte> uses)
    {
        return IsActionReference(uses, "actions/cache"u8)
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
