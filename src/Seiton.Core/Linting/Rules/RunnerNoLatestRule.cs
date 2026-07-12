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

    public override void VisitJobPre(JobRef job)
    {
        var runsOn = job.RunsOn;
        if (!runsOn.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        if (runsOn.LabelsExpr.HasValue)
        {
            CheckMatrixExpandedLabels(job, runsOn);
            return;
        }

        if (!runsOn.Labels.HasValue)
        {
            return;
        }

        if (ContainsSelfHostedLabel(runsOn.Labels))
        {
            return;
        }

        for (var i = 0; i < runsOn.Labels.Count; i++)
        {
            var label = runsOn.Labels[i];
            if (label.Expression.HasValue)
            {
                continue;
            }

            var labelUtf8 = label.Value;

            ReportLatestLabel(job, label, labelUtf8);
        }
    }

    private void ReportLatestLabel(JobRef job, StringRef label, ReadOnlySpan<byte> labelUtf8)
    {
        // Only scan fix-mapping when built-in detection is insufficient or fix generation needs the pinned value.
        var isBuiltIn = IsBuiltInLatestLabel(labelUtf8);
        var hasMappingValue = false;
        var pinned = string.Empty;
        if (!isBuiltIn || Config.Fix.Enabled)
        {
            hasMappingValue = TryGetFixValue(labelUtf8, out pinned);
        }

        if (!isBuiltIn && !hasMappingValue)
        {
            return;
        }

        var location = label.Range;

        DiagnosticFix? fix = null;
        if (Config.Fix.Enabled && hasMappingValue)
        {
            var slice = label.Slice;
            fix = new DiagnosticFix(
                $"pin runner label to '{pinned}'",
                [new TextEdit(slice.Offset, slice.Length, pinned)]);
        }

        // Decode the job id and label into stack buffers so the diagnostic costs a single
        // string (the message itself) instead of message + two intermediate strings.
        Span<char> jobIdBuffer = stackalloc char[128];
        var jobId = DecodeChars(job.Id.Slice, jobIdBuffer);
        Span<char> labelBuffer = stackalloc char[128];
        var labelText = DecodeChars(label.Slice, labelBuffer);

        if (fix.HasValue)
        {
            AddJobWarning(job, $"jobs.'{jobId}'.runs-on label '{labelText}' is a moving latest label; prefer explicit version-pinned runner labels", location, fix.Value);
        }
        else
        {
            AddJobWarning(job, $"jobs.'{jobId}'.runs-on label '{labelText}' is a moving latest label; prefer explicit version-pinned runner labels", location);
        }
    }

    private void CheckMatrixExpandedLabels(JobRef job, RunnerRef runsOn)
    {
        var exprUtf8 = runsOn.LabelsExpr.Value;
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

        var matrix = job.Strategy.Matrix;
        if (!matrix.HasValue || matrix.Expression.HasValue || !matrix.Rows.HasValue)
        {
            return;
        }

        if (!matrix.Rows.TryGetValue(body[7..], out var row))
        {
            return;
        }

        if (row.Expression.HasValue || !row.Values.HasValue)
        {
            return;
        }

        for (var i = 0; i < row.Values.Count; i++)
        {
            var value = row.Values[i];
            switch (value.Kind)
            {
                case RawYamlKind.String:
                    var scalar = value.Scalar;
                    if (scalar.Expression.HasValue || ExpressionScanHelpers.ContainsExpressionMarker(scalar.Value))
                    {
                        continue;
                    }

                    ReportLatestLabel(job, scalar, scalar.Value);
                    break;

                case RawYamlKind.Array:
                    ReportLatestLabelsInMatrixArray(job, value);
                    break;
            }
        }
    }

    private void ReportLatestLabelsInMatrixArray(JobRef job, RawYamlRef array)
    {
        for (var i = 0; i < array.Items.Count; i++)
        {
            var item = array.Items[i];
            if (item.Kind == RawYamlKind.String && RunnerLabels.IsSelfHostedLabel(item.Scalar.Value))
            {
                return;
            }
        }

        for (var i = 0; i < array.Items.Count; i++)
        {
            if (array.Items[i].Kind != RawYamlKind.String)
            {
                continue;
            }

            var item = array.Items[i].Scalar;
            if (item.Expression.HasValue || ExpressionScanHelpers.ContainsExpressionMarker(item.Value))
            {
                continue;
            }

            ReportLatestLabel(job, item, item.Value);
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

    private bool ContainsSelfHostedLabel(StringRefList labels)
    {
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            if (label.Expression.HasValue)
            {
                continue;
            }

            var labelUtf8 = label.Value;
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
