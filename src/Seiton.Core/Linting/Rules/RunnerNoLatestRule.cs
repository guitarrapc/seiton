using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class RunnerNoLatestRule : RuleBase
{
    public override string Id => "runner-no-latest";

    public override string Name => "Runner No Latest Rule";

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
            if (!IsLatestHostedRunnerLabel(labelUtf8))
            {
                continue;
            }

            var labelText = Decode(label.Value);
            AddJobWarning(job, $"job '{jobId}' runs-on label '{labelText}' is a moving latest label; prefer explicit version-pinned runner labels", label.Range);
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
            if (label.Expression is not null)
            {
                continue;
            }

            var labelUtf8 = label.Value.AsSpan(Config.Utf8Yaml);
            if (RunnerLabels.IsSelfHostedLabel(labelUtf8))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsLatestHostedRunnerLabel(ReadOnlySpan<byte> labelUtf8)
    {
        return labelUtf8.SequenceEqual("ubuntu-latest"u8)
            || labelUtf8.SequenceEqual("windows-latest"u8)
            || labelUtf8.SequenceEqual("macos-latest"u8);
    }
}
