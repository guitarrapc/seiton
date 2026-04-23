using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags <c>runs-on: *-latest</c> labels which may cause unexpected runner image changes.</summary>
public sealed class RunnerNoLatestRule() : RuleBase(RuleId.RunnerNoLatest)
{
    public override string Name => "Runner No Latest Rule";

    public override void VisitJobPre(Job job)
    {
        var runsOn = job.RunsOn;
        if (runsOn is null || runsOn.LabelsExpr.HasValue || runsOn.Labels is null || Config.Utf8Yaml is null)
        {
            return;
        }

        if (ContainsSelfHostedLabel(runsOn.Labels))
        {
            return;
        }

        var jobId = Decode(Arena.GetStringSlice(job.Id));
        for (var i = 0; i < runsOn.Labels.Length; i++)
        {
            var label = runsOn.Labels[i];
            if (Arena.GetStringExpression(label).HasValue)
            {
                continue;
            }

            var labelUtf8 = Arena.GetStringValue(label);
            if (!IsLatestHostedRunnerLabel(labelUtf8))
            {
                continue;
            }

            var labelText = Decode(Arena.GetStringSlice(label));
            AddJobWarning(job, $"job '{jobId}' runs-on label '{labelText}' is a moving latest label; prefer explicit version-pinned runner labels", Arena.GetStringRange(label));
        }
    }

    private bool ContainsSelfHostedLabel(StringNodeId[] labels)
    {
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        for (var i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            if (Arena.GetStringExpression(label).HasValue)
            {
                continue;
            }

            var labelUtf8 = Arena.GetStringValue(label);
            if (RunnerLabels.IsSelfHostedLabel(labelUtf8))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLatestHostedRunnerLabel(ReadOnlySpan<byte> labelUtf8)
    {
        return labelUtf8.SequenceEqual("ubuntu-latest"u8)
            || labelUtf8.SequenceEqual("windows-latest"u8)
            || labelUtf8.SequenceEqual("macos-latest"u8);
    }
}
