using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Linting.RuleConfigHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class SelfHostedRunnerRule : RuleBase
{
    private bool hasUntrustedTrigger;
    private HashSet<string> additionalUntrustedTriggers = [];

    public override string Id => "self-hosted-runner";

    public override string Name => "Self Hosted Runner Rule";

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

    public override void VisitJobPre(Job job)
    {
        if (!hasUntrustedTrigger || job.RunsOn?.Labels is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var labels = job.RunsOn.Labels;
        for (var i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            if (Arena.GetStringExpression(label).HasValue)
            {
                continue;
            }

            if (!RunnerLabels.IsSelfHostedLabel(Arena.GetStringValue(label)))
            {
                continue;
            }

            var jobId = Decode(Arena.GetStringSlice(job.Id));
            AddJobWarning(
                job,
                $"job '{jobId}' uses self-hosted runner under untrusted triggers; add strict job guards and isolate self-hosted execution paths",
                Arena.GetStringRange(label));
            return;
        }
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
}
