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
        if (runsOn is null || Config.Utf8Yaml is null)
        {
            return;
        }

        if (runsOn.LabelsExpr.HasValue)
        {
            CheckMatrixExpandedLabels(job, runsOn);
            return;
        }

        if (runsOn.Labels is null)
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

    /// <summary>
    /// When <c>runs-on</c> is a single <c>${{ matrix.AXIS }}</c> expression,
    /// resolves the matrix dimension and validates each scalar value as a runner label.
    /// </summary>
    private void CheckMatrixExpandedLabels(Job job, Runner runsOn)
    {
        var exprUtf8 = Arena.GetStringValue(runsOn.LabelsExpr);
        if (!ExpressionScanHelpers.TryExtractExpressionBody(exprUtf8, out var body))
        {
            return;
        }

        // Only handle simple `matrix.AXIS` references
        if (!body.StartsWith("matrix."u8) || body.Length <= 7)
        {
            return;
        }

        // Check for nested property access (e.g. matrix.runner.os) — skip
        if (body[7..].IndexOf((byte)'.') >= 0)
        {
            return;
        }

        var axisName = body[7..]; // slice after "matrix."

        var matrix = job.Strategy?.Matrix;
        if (matrix is null || matrix.Expression.HasValue || matrix.Rows is null)
        {
            return;
        }

        if (!matrix.Rows.Value.TryGetValue(Config.Utf8Yaml, axisName, out var row))
        {
            return;
        }

        // Row is an expression — cannot validate
        if (row.Expression.HasValue || row.Values is null)
        {
            return;
        }

        var jobId = Decode(Arena.GetStringSlice(job.Id));

        for (var i = 0; i < row.Values.Count; i++)
        {
            var value = row.Values[i];
            switch (value)
            {
                case RawYamlString scalar:
                {
                    if (ExpressionScanHelpers.ContainsExpressionMarker(scalar.Value, Arena))
                    {
                        continue;
                    }

                    var labelUtf8 = Arena.GetStringValue(scalar.Value);
                    if (labelUtf8.IsEmpty
                        || RunnerLabels.IsKnownHostedLabel(labelUtf8)
                        || RunnerLabels.IsSelfHostedPresetLabel(labelUtf8)
                        || IsAdditionalKnownHostedLabel(labelUtf8))
                    {
                        continue;
                    }

                    var labelText = Decode(Arena.GetStringSlice(scalar.Value));
                    AddJobWarning(job, $"job '{jobId}' runs-on label '{labelText}' is not a known GitHub-hosted runner label", Arena.GetStringRange(scalar.Value));
                    break;
                }

                case RawYamlArray array:
                {
                    // If any element is "self-hosted", the whole entry is self-hosted runner labels
                    var hasSelfHosted = false;
                    for (var j = 0; j < array.Items.Count; j++)
                    {
                        if (array.Items[j] is RawYamlString item && RunnerLabels.IsSelfHostedLabel(Arena.GetStringValue(item.Value)))
                        {
                            hasSelfHosted = true;
                            break;
                        }
                    }

                    if (hasSelfHosted)
                    {
                        continue;
                    }

                    // Validate each element
                    for (var j = 0; j < array.Items.Count; j++)
                    {
                        if (array.Items[j] is not RawYamlString element)
                        {
                            continue;
                        }

                        if (ExpressionScanHelpers.ContainsExpressionMarker(element.Value, Arena))
                        {
                            continue;
                        }

                        var elemUtf8 = Arena.GetStringValue(element.Value);
                        if (elemUtf8.IsEmpty
                            || RunnerLabels.IsKnownHostedLabel(elemUtf8)
                            || RunnerLabels.IsSelfHostedPresetLabel(elemUtf8)
                            || IsAdditionalKnownHostedLabel(elemUtf8))
                        {
                            continue;
                        }

                        var elemText = Decode(Arena.GetStringSlice(element.Value));
                        AddJobWarning(job, $"job '{jobId}' runs-on label '{elemText}' is not a known GitHub-hosted runner label", Arena.GetStringRange(element.Value));
                    }

                    break;
                }
            }
        }
    }
}
