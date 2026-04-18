using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class SelfHostedRunnerRule : RuleBase
{
    bool hasUntrustedTrigger;

    public override string Id => "self-hosted-runner";

    public override string Name => "Self Hosted Runner Rule";

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        hasUntrustedTrigger = Config.Utf8Yaml is not null && HasUntrustedTrigger(workflow, Config.Utf8Yaml);
    }

    public override void VisitJobPre(Job job)
    {
        if (!hasUntrustedTrigger || job.RunsOn?.Labels is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var labels = job.RunsOn.Labels;
        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            if (label.Expression is not null)
            {
                continue;
            }

            if (!RunnerLabels.IsSelfHostedLabel(label.Value.AsSpan(Config.Utf8Yaml)))
            {
                continue;
            }

            var jobId = Decode(job.Id.Value);
            AddJobWarning(
                job,
                $"job '{jobId}' uses self-hosted runner under untrusted triggers; add strict job guards and isolate self-hosted execution paths",
                label.Range);
            return;
        }
    }

    static bool HasUntrustedTrigger(Workflow workflow, byte[] utf8Yaml)
    {
        for (var i = 0; i < workflow.On.Count; i++)
        {
            if (workflow.On[i] is not WebhookEvent webhook)
            {
                continue;
            }

            var hook = webhook.Hook.Value.AsSpan(utf8Yaml);
            if (!WebhookTypes.TryGet(hook, out _, out var spec))
            {
                continue;
            }

            if (spec.Id is WebhookTypes.EventId.PullRequest
                or WebhookTypes.EventId.PullRequestTarget
                or WebhookTypes.EventId.WorkflowRun)
            {
                return true;
            }
        }

        return false;
    }
}
