using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public sealed class RunnerLabelRule : RuleBase
{
    public override string Id => "runner-label";

    public override string Name => "Runner Label Rule";

    public override void VisitJobPre(Job job)
    {
        var runsOn = job.RunsOn;
        if (runsOn is null || runsOn.LabelsExpr is not null || runsOn.Labels is null || Config.Utf8Yaml is null)
        {
            return;
        }

        if (ContainsSelfHostedLabel(runsOn.Labels))
        {
            return;
        }

        var jobId = Decode(job.Id.Value);
        for (var i = 0; i < runsOn.Labels.Count; i++)
        {
            var label = runsOn.Labels[i];
            if (label.Expression is not null)
            {
                continue;
            }

            var labelUtf8 = label.Value.AsSpan(Config.Utf8Yaml);
            if (labelUtf8.IsEmpty || RunnerLabels.IsKnownHostedLabel(labelUtf8))
            {
                continue;
            }

            var labelText = Decode(label.Value);
            AddJobWarning(job, $"job '{jobId}' runs-on label '{labelText}' is not a known GitHub-hosted runner label", label.Range);
        }
    }

    bool ContainsSelfHostedLabel(IReadOnlyList<StringNode> labels)
    {
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            var labelUtf8 = label.Value.AsSpan(Config.Utf8Yaml);
            if (RunnerLabels.IsSelfHostedLabel(labelUtf8))
            {
                return true;
            }
        }

        return false;
    }
}
