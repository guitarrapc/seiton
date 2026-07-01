using System.Runtime.CompilerServices;
using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags <c>runs-on: *-latest</c> labels which may cause unexpected runner image changes.</summary>
public sealed class RunnerNoLatestRule() : RuleBase(RuleId.RunnerNoLatest)
{
    public override string Name => "Runner No Latest Rule";

    private IReadOnlyDictionary<string, string>? _fixMapping;
    private (byte[] KeyUtf8, string Value)[]? _fixMappingEntries;

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        _fixMapping = config.GetRuleConfig(RuleId.RunnerNoLatest)?.FixMapping;

        if (_fixMapping is null || _fixMapping.Count == 0)
        {
            _fixMappingEntries = null;
            return;
        }

        var entries = new (byte[] KeyUtf8, string Value)[_fixMapping.Count];
        var index = 0;
        foreach (var pair in _fixMapping)
        {
            entries[index++] = (Encoding.UTF8.GetBytes(pair.Key), pair.Value);
        }

        _fixMappingEntries = entries;
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
        for (var i = 0; i < runsOn.Labels.Count; i++)
        {
            var label = runsOn.Labels[i];
            if (Arena.GetStringExpression(label).HasValue)
            {
                continue;
            }

            var labelUtf8 = Arena.GetStringValue(label);

            ReportLatestLabel(job, jobId, label, labelUtf8);
        }
    }

    private void ReportLatestLabel(Job job, string jobId, StringNodeId label, ReadOnlySpan<byte> labelUtf8)
    {
        // Single lookup: check built-in first, then fix-mapping (avoids double scan)
        var isBuiltIn = IsBuiltInLatestLabel(labelUtf8);
        var hasMappingValue = TryGetFixValue(labelUtf8, out var pinned);
        if (!isBuiltIn && !hasMappingValue)
        {
            return;
        }

        var location = Arena.GetStringRange(label);

        DiagnosticFix? fix = null;
        if (Config.Fix.Enabled && hasMappingValue)
        {
            var slice = Arena.GetStringSlice(label);
            fix = new DiagnosticFix(
                $"pin runner label to '{pinned}'",
                [new TextEdit(slice.Offset, slice.Length, pinned)]);
        }

        var labelText = Decode(Arena.GetStringSlice(label));

        if (fix.HasValue)
        {
            AddJobWarning(job, $"jobs.'{jobId}'.runs-on label '{labelText}' is a moving latest label; prefer explicit version-pinned runner labels", location, fix.Value);
        }
        else
        {
            AddJobWarning(job, $"jobs.'{jobId}'.runs-on label '{labelText}' is a moving latest label; prefer explicit version-pinned runner labels", location);
        }
    }

    private void CheckMatrixExpandedLabels(Job job, Runner runsOn)
    {
        var exprUtf8 = Arena.GetStringValue(runsOn.LabelsExpr);
        if (!ExpressionScanHelpers.TryExtractExpressionBody(exprUtf8, out var body))
        {
            return;
        }

        if (!body.StartsWith("matrix."u8) || body.Length <= 7)
        {
            return;
        }

        if (body[7..].IndexOf((byte)'.') >= 0)
        {
            return;
        }

        var matrix = job.Strategy?.Matrix;
        if (matrix is null || matrix.Expression.HasValue || matrix.Rows is null)
        {
            return;
        }

        if (!matrix.Rows.Value.TryGetValue(Config.Utf8Yaml, body[7..], out var row))
        {
            return;
        }

        if (row.Expression.HasValue || row.Values is null)
        {
            return;
        }

        var jobId = Decode(Arena.GetStringSlice(job.Id));

        for (var i = 0; i < row.Values.Count; i++)
        {
            switch (row.Values[i])
            {
                case RawYamlString scalar:
                    if (ExpressionScanHelpers.ContainsExpressionMarker(scalar.Value, Arena))
                    {
                        continue;
                    }

                    ReportLatestLabel(job, jobId, scalar.Value, Arena.GetStringValue(scalar.Value));
                    break;

                case RawYamlArray array:
                    ReportLatestLabelsInMatrixArray(job, jobId, array);
                    break;
            }
        }
    }

    private void ReportLatestLabelsInMatrixArray(Job job, string jobId, RawYamlArray array)
    {
        for (var i = 0; i < array.Items.Count; i++)
        {
            if (array.Items[i] is RawYamlString item && RunnerLabels.IsSelfHostedLabel(Arena.GetStringValue(item.Value)))
            {
                return;
            }
        }

        for (var i = 0; i < array.Items.Count; i++)
        {
            if (array.Items[i] is not RawYamlString item)
            {
                continue;
            }

            if (ExpressionScanHelpers.ContainsExpressionMarker(item.Value, Arena))
            {
                continue;
            }

            ReportLatestLabel(job, jobId, item.Value, Arena.GetStringValue(item.Value));
        }
    }

    /// <summary>
    /// Tries to get the fix replacement value for the given label bytes (ASCII case-insensitive).
    /// </summary>
    private bool TryGetFixValue(ReadOnlySpan<byte> labelUtf8, out string pinned)
    {
        if (_fixMappingEntries is not null)
        {
            for (var i = 0; i < _fixMappingEntries.Length; i++)
            {
                var entry = _fixMappingEntries[i];
                if (!AsciiEqualsIgnoreCase(labelUtf8, entry.KeyUtf8))
                {
                    continue;
                }

                pinned = entry.Value;
                return true;
            }
        }

        pinned = string.Empty;
        return false;
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

    /// <summary>Checks built-in latest hosted runner labels (case-insensitive).</summary>
    private static bool IsBuiltInLatestLabel(ReadOnlySpan<byte> labelUtf8)
    {
        // Fast path: exact lowercase match (most common case)
        if (labelUtf8.SequenceEqual("ubuntu-latest"u8)
            || labelUtf8.SequenceEqual("windows-latest"u8)
            || labelUtf8.SequenceEqual("macos-latest"u8))
        {
            return true;
        }

        // Slow path: case-insensitive comparison via ASCII lowering
        if (labelUtf8.Length == 13 && AsciiEqualsIgnoreCase(labelUtf8, "ubuntu-latest"u8))
        {
            return true;
        }

        if (labelUtf8.Length == 14 && AsciiEqualsIgnoreCase(labelUtf8, "windows-latest"u8))
        {
            return true;
        }

        if (labelUtf8.Length == 12 && AsciiEqualsIgnoreCase(labelUtf8, "macos-latest"u8))
        {
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i];
            var r = right[i];
            if (l == r)
            {
                continue;
            }

            // ASCII lower both sides
            if (l >= (byte)'A' && l <= (byte)'Z')
            {
                l = (byte)(l + 32);
            }

            if (r >= (byte)'A' && r <= (byte)'Z')
            {
                r = (byte)(r + 32);
            }

            if (l != r)
            {
                return false;
            }
        }

        return true;
    }
}
