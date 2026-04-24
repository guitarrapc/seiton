using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Linting.RuleConfigHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates <c>runs-on:</c> labels against known GitHub-hosted and self-hosted runner labels.</summary>
public sealed class RunnerLabelRule() : RuleBase(RuleId.RunnerLabel)
{
    private HashSet<string> additionalKnownHostedLabels = [];

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

        // Detect OS family conflicts among labels
        DetectOsFamilyConflicts(job, jobId, runsOn.Labels);

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

    private void DetectOsFamilyConflicts(Job job, string jobId, StringNodeId[] labels)
    {
        // Track which OS families appear and where
        byte seenOsFamilies = 0; // bit 0=linux, 1=windows, 2=macos
        var firstConflictRange = default(TextRange);

        for (var i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            if (Arena.GetStringExpression(label).HasValue)
            {
                continue;
            }

            var labelUtf8 = Arena.GetStringValue(label);
            var family = GetOsFamily(labelUtf8);
            if (family == 0)
            {
                continue;
            }

            if (seenOsFamilies != 0 && (seenOsFamilies & family) == 0)
            {
                // Different OS family from what we already saw
                firstConflictRange = Arena.GetStringRange(label);
                AddJobError(job, $"job '{jobId}' runs-on labels contain conflicting OS families", firstConflictRange);
                return;
            }

            seenOsFamilies |= family;
        }
    }

    /// <summary>Returns a bitmask for the OS family: 1=linux, 2=windows, 4=macos, 0=unknown.</summary>
    private static byte GetOsFamily(ReadOnlySpan<byte> labelUtf8)
    {
        if (StartsWithAsciiIgnoreCase(labelUtf8, "ubuntu-"u8))
        {
            return 1;
        }

        if (StartsWithAsciiIgnoreCase(labelUtf8, "windows-"u8))
        {
            return 2;
        }

        if (StartsWithAsciiIgnoreCase(labelUtf8, "macos-"u8))
        {
            return 4;
        }

        return 0;
    }

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
    {
        if (value.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            var a = value[i];
            var b = prefix[i];
            if (a == b)
            {
                continue;
            }

            if (a is >= (byte)'A' and <= (byte)'Z')
            {
                a = (byte)(a + 32);
            }

            if (b is >= (byte)'A' and <= (byte)'Z')
            {
                b = (byte)(b + 32);
            }

            if (a != b)
            {
                return false;
            }
        }

        return true;
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
