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
        additionalKnownHostedLabels = config.GetRuleConfig(Id)?.KnownHostedLabels is { Count: > 0 } labels
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

        // Detect OS family conflicts among static labels (reports ALL conflicts)
        var (staticOsFamily, firstOsLabel) = DetectOsFamilyConflicts(job, jobId, runsOn.Labels);

        // Detect OS conflicts between static labels and matrix-expanded expression labels
        if (staticOsFamily != 0)
        {
            DetectMatrixLabelOsConflicts(job, jobId, runsOn.Labels, staticOsFamily, firstOsLabel);
        }

        var loggedAdditionalKnownHostedLabel = false;

        for (var i = 0; i < runsOn.Labels.Count; i++)
        {
            var label = runsOn.Labels[i];
            if (ExpressionScanHelpers.ContainsExpressionMarker(label, Arena))
            {
                continue;
            }

            var labelUtf8 = Arena.GetStringValue(label);
            if (labelUtf8.IsEmpty)
            {
                // Empty labels are already reported by the parser as syntax-check.
                continue;
            }

            if (RunnerLabels.IsKnownHostedLabel(labelUtf8) || RunnerLabels.IsSelfHostedPresetLabel(labelUtf8))
            {
                continue;
            }

            if (TryHandleAdditionalKnownHostedLabel(job, label, labelUtf8, ref loggedAdditionalKnownHostedLabel, dedupeInfoPerJob: true))
            {
                continue;
            }

            var labelText = Decode(Arena.GetStringSlice(label));
            AddJobWarning(job, BuildUnknownLabelMessage(labelText), Arena.GetStringRange(label));
        }
    }

    /// <summary>
    /// Detects OS family conflicts among static labels.
    /// Reports ALL conflicts (not just the first) and returns the combined OS family bitmask
    /// and the first OS label for use in matrix conflict messages.
    /// </summary>
    private (byte SeenOsFamilies, StringNodeId FirstOsLabel) DetectOsFamilyConflicts(Job job, string jobId, IReadOnlyList<StringNodeId> labels)
    {
        byte seenOsFamilies = 0; // bit 0=linux, 1=windows, 2=macos
        var firstOsLabel = default(StringNodeId);

        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            if (ExpressionScanHelpers.ContainsExpressionMarker(label, Arena))
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
                var labelText = Decode(Arena.GetStringSlice(label));
                var firstText = Decode(Arena.GetStringSlice(firstOsLabel));
                var firstRange = Arena.GetStringRange(firstOsLabel);
                AddJobError(job, $"label \"{labelText}\" conflicts with label \"{firstText}\" defined at line:{firstRange.StartLine},col:{firstRange.StartColumn}. note: to run your job on each workers, use matrix", Arena.GetStringRange(label));
                // Continue checking remaining labels — don't return early
            }
            else if (seenOsFamilies == 0)
            {
                firstOsLabel = label;
            }

            seenOsFamilies |= family;
        }

        return (seenOsFamilies, firstOsLabel);
    }

    /// <summary>
    /// Detects OS family conflicts between static runs-on labels and matrix-expanded expression labels.
    /// When runs-on is a list containing both static labels and <c>${{ matrix.AXIS }}</c> expressions,
    /// resolves the matrix axis values and checks each for OS family conflicts with the static labels.
    /// </summary>
    private void DetectMatrixLabelOsConflicts(Job job, string jobId, IReadOnlyList<StringNodeId> labels, byte staticOsFamily, StringNodeId firstOsLabel)
    {
        var matrix = job.Strategy?.Matrix;
        if (matrix is null || matrix.Expression.HasValue || matrix.Rows is null)
        {
            return;
        }

        var firstOsLabelText = Decode(Arena.GetStringSlice(firstOsLabel));

        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            if (!ExpressionScanHelpers.ContainsExpressionMarker(label, Arena))
            {
                continue;
            }

            var exprUtf8 = Arena.GetStringValue(label);
            if (!ExpressionScanHelpers.TryExtractExpressionBody(exprUtf8, out var body))
            {
                continue;
            }

            // Only handle simple `matrix.AXIS` references
            if (!body.StartsWith("matrix."u8) || body.Length <= 7)
            {
                continue;
            }

            // Check for nested property access (e.g. matrix.runner.os) — skip
            if (body[7..].IndexOf((byte)'.') >= 0)
            {
                continue;
            }

            var axisName = body[7..]; // slice after "matrix."

            if (!matrix.Rows.Value.TryGetValue(Config.Utf8Yaml, axisName, out var row))
            {
                continue;
            }

            if (row.Expression.HasValue || row.Values is null)
            {
                continue;
            }

            for (var j = 0; j < row.Values.Count; j++)
            {
                if (row.Values[j] is not RawYamlString scalar)
                {
                    continue;
                }

                if (ExpressionScanHelpers.ContainsExpressionMarker(scalar.Value, Arena))
                {
                    continue;
                }

                var valueUtf8 = Arena.GetStringValue(scalar.Value);
                var family = GetOsFamily(valueUtf8);
                if (family == 0 || (staticOsFamily & family) != 0)
                {
                    // Same family or unknown — no conflict
                    continue;
                }

                var valueText = Decode(Arena.GetStringSlice(scalar.Value));
                var firstRange = Arena.GetStringRange(firstOsLabel);
                AddJobError(job, $"label \"{valueText}\" conflicts with label \"{firstOsLabelText}\" defined at line:{firstRange.StartLine},col:{firstRange.StartColumn}. note: to run your job on each workers, use matrix", Arena.GetStringRange(scalar.Value));
            }
        }
    }

    /// <summary>Returns a bitmask for the OS family: 1=linux, 2=windows, 4=macos, 0=unknown.
    /// Matches both hosted label prefixes (ubuntu-, windows-, macos-) and bare self-hosted preset labels (linux, windows, macos).</summary>
    private static byte GetOsFamily(ReadOnlySpan<byte> labelUtf8)
    {
        if (StartsWithAsciiIgnoreCase(labelUtf8, "ubuntu-"u8)
            || (labelUtf8.Length == 5 && StartsWithAsciiIgnoreCase(labelUtf8, "linux"u8)))
        {
            return 1;
        }

        if (StartsWithAsciiIgnoreCase(labelUtf8, "windows-"u8)
            || (labelUtf8.Length == 7 && StartsWithAsciiIgnoreCase(labelUtf8, "windows"u8)))
        {
            return 2;
        }

        if (StartsWithAsciiIgnoreCase(labelUtf8, "macos-"u8)
            || (labelUtf8.Length == 5 && StartsWithAsciiIgnoreCase(labelUtf8, "macos"u8)))
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

    private bool ContainsSelfHostedLabel(IReadOnlyList<StringNodeId> labels)
    {
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        for (var i = 0; i < labels.Count; i++)
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

    private string BuildUnknownLabelMessage(string labelText)
    {
        var msg = $"label \"{labelText}\" is unknown. available labels are: hosted runners: {RunnerLabels.HostedLabelList}. larger runners: {RunnerLabels.LargerLabelList}. self-hosted presets: {RunnerLabels.SelfHostedPresetLabelList}";
        if (additionalKnownHostedLabels.Count > 0)
        {
            var customList = string.Join(", ", additionalKnownHostedLabels.OrderBy(static x => x, StringComparer.Ordinal).Select(static l => $"\"{l}\""));
            msg += $". custom labels: {customList}";
        }
        msg += ". if it is a custom label for self-hosted runner, set list of labels in config file";
        return msg;
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
        var loggedAdditionalKnownHostedLabel = false;

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
                            || RunnerLabels.IsSelfHostedPresetLabel(labelUtf8))
                        {
                            continue;
                        }

                        if (TryHandleAdditionalKnownHostedLabel(job, scalar.Value, labelUtf8, ref loggedAdditionalKnownHostedLabel, dedupeInfoPerJob: true))
                        {
                            continue;
                        }

                        var labelText = Decode(Arena.GetStringSlice(scalar.Value));
                        AddJobWarning(job, BuildUnknownLabelMessage(labelText), Arena.GetStringRange(scalar.Value));
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
                                || RunnerLabels.IsSelfHostedPresetLabel(elemUtf8))
                            {
                                continue;
                            }

                            if (TryHandleAdditionalKnownHostedLabel(job, element.Value, elemUtf8, ref loggedAdditionalKnownHostedLabel, dedupeInfoPerJob: true))
                            {
                                continue;
                            }

                            var elemText = Decode(Arena.GetStringSlice(element.Value));
                            AddJobWarning(job, BuildUnknownLabelMessage(elemText), Arena.GetStringRange(element.Value));
                        }

                        break;
                    }
            }
        }
    }

    private bool TryHandleAdditionalKnownHostedLabel(Job job, StringNodeId labelNode, ReadOnlySpan<byte> labelUtf8, ref bool loggedAdditionalKnownHostedLabel, bool dedupeInfoPerJob)
    {
        if (!IsAdditionalKnownHostedLabel(labelUtf8))
        {
            return false;
        }

        if (Config.Verbose && (!dedupeInfoPerJob || !loggedAdditionalKnownHostedLabel))
        {
            var knownLabelText = System.Text.Encoding.UTF8.GetString(labelUtf8);
            AddJobInfo(job, $"label '{knownLabelText}' matched known-hosted-labels config, skipping", Arena.GetStringRange(labelNode));
            loggedAdditionalKnownHostedLabel = true;
        }

        return true;
    }
}
