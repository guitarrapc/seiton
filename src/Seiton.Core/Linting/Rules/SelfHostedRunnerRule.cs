using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Linting.RuleConfigHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags security concerns with self-hosted runner usage (e.g. dangerous triggers on public repos).</summary>
public sealed class SelfHostedRunnerRule() : RuleBase(RuleId.SelfHostedRunner)
{
    private bool hasUntrustedTrigger;
    private HashSet<string> additionalUntrustedTriggers = [];

    public override string Name => "Self Hosted Runner Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalUntrustedTriggers = config.GetRuleConfig(Id)?.UntrustedTriggers is { Count: > 0 } triggers
            ? BuildNormalizedSet(triggers)
            : [];
    }

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        hasUntrustedTrigger = Config.Utf8Yaml is not null && HasUntrustedTrigger(workflow, Config.Utf8Yaml, additionalUntrustedTriggers);
    }

    public override void VisitJobPre(JobRef job)
    {
        if (!hasUntrustedTrigger || !job.RunsOn.Labels.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var labels = job.RunsOn.Labels;
        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            if (label.Expression.HasValue)
            {
                continue;
            }

            if (!RunnerLabels.IsSelfHostedLabel(label.Value))
            {
                continue;
            }

            var jobId = job.Id.Decode();
            AddJobWarning(
                job,
                $"jobs.'{jobId}'.runs-on uses self-hosted runner under untrusted triggers; add strict job guards and isolate self-hosted execution paths",
                label.Range);
            return;
        }
    }

    private bool HasUntrustedTrigger(WorkflowRef workflow, byte[] utf8Yaml, HashSet<string> additionalUntrustedTriggers)
    {
        for (var i = 0; i < workflow.On.Count; i++)
        {
            var ev = workflow.On[i];
            if (ev.Kind != EventKind.Webhook)
            {
                continue;
            }

            var hook = ev.AsWebhook().Hook.Value;
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
}
