using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Linting.RuleConfigHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class RunnerLabelRule : RuleBase
{
    private HashSet<string> additionalKnownHostedLabels = [];

    public override string Id => "runner-label";

    public override string Name => "Runner Label Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalKnownHostedLabels = config.GetRuleConfig(Id)?.KnownHostedLabels?.Extend is { Count: > 0 } labels
            ? BuildNormalizedSet(labels)
            : [];
    }

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
            if (labelUtf8.IsEmpty || RunnerLabels.IsKnownHostedLabel(labelUtf8) || IsAdditionalKnownHostedLabel(labelUtf8))
            {
                continue;
            }

            var labelText = Decode(Arena.GetStringSlice(label));
            AddJobWarning(job, $"job '{jobId}' runs-on label '{labelText}' is not a known GitHub-hosted runner label", Arena.GetStringRange(label));
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
            var labelUtf8 = Arena.GetStringValue(label);
            if (RunnerLabels.IsSelfHostedLabel(labelUtf8))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsAdditionalKnownHostedLabel(ReadOnlySpan<byte> labelUtf8)
    {
        if (additionalKnownHostedLabels.Count == 0)
        {
            return false;
        }

        return additionalKnownHostedLabels.Contains(NormalizeAsciiLower(labelUtf8));
    }
}
