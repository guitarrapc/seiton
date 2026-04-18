using Seiton.Core.Generated;
using Seiton.Core.Linting;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class SelfHostedRunnerRule : RuleBase
{
    bool hasUntrustedTrigger;
    HashSet<string>? additionalUntrustedTriggers;

    public override string Id => "self-hosted-runner";

    public override string Name => "Self Hosted Runner Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalUntrustedTriggers = BuildNormalizedSet(config.AdditiveCustomization.AdditionalUntrustedTriggers);
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

    static bool HasUntrustedTrigger(Workflow workflow, byte[] utf8Yaml, HashSet<string>? additionalUntrustedTriggers)
    {
        for (var i = 0; i < workflow.On.Count; i++)
        {
            if (workflow.On[i] is not WebhookEvent webhook)
            {
                continue;
            }

            var hook = webhook.Hook.Value.AsSpan(utf8Yaml);
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

    static bool IsAdditionalUntrustedTrigger(ReadOnlySpan<byte> hook, HashSet<string>? additionalUntrustedTriggers)
    {
        if (additionalUntrustedTriggers is null || additionalUntrustedTriggers.Count == 0)
        {
            return false;
        }

        return additionalUntrustedTriggers.Contains(NormalizeAsciiLower(hook));
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
        var chars = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var b = value[i];
            chars[i] = (char)(b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : b);
        }

        return new string(chars);
    }
}
