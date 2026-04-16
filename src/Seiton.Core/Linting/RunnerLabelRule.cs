using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public sealed class RunnerLabelRule : RuleBase
{
    HashSet<string>? additionalKnownHostedLabels;

    public override string Id => "runner-label";

    public override string Name => "Runner Label Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalKnownHostedLabels = BuildNormalizedSet(config.AdditiveCustomization.AdditionalKnownHostedLabels);
    }

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
            if (labelUtf8.IsEmpty || RunnerLabels.IsKnownHostedLabel(labelUtf8) || IsAdditionalKnownHostedLabel(labelUtf8))
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

    bool IsAdditionalKnownHostedLabel(ReadOnlySpan<byte> labelUtf8)
    {
        if (additionalKnownHostedLabels is null || additionalKnownHostedLabels.Count == 0)
        {
            return false;
        }

        return additionalKnownHostedLabels.Contains(NormalizeAsciiLower(labelUtf8));
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
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = (char)value[i];
            if (ch is >= 'A' and <= 'Z')
            {
                ch = (char)(ch + 32);
            }

            buffer[i] = ch;
        }

        return new string(buffer);
    }
}
