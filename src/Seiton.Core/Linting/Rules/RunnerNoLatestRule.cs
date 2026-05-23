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

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        _fixMapping = config.GetRuleConfig(RuleId.RunnerNoLatest)?.FixMapping;
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
            if (!IsTargetLabel(labelUtf8))
            {
                continue;
            }

            var labelText = Decode(Arena.GetStringSlice(label));
            var location = Arena.GetStringRange(label);

            DiagnosticFix? fix = null;
            if (Config.Fix.Enabled && _fixMapping is not null && TryGetFixValue(labelText, out var pinned))
            {
                var slice = Arena.GetStringSlice(label);
                fix = new DiagnosticFix(
                    $"pin runner label to '{pinned}'",
                    [new TextEdit(slice.Offset, slice.Length, pinned)]);
            }

            if (fix.HasValue)
            {
                AddJobWarning(job, $"jobs.'{jobId}'.runs-on label '{labelText}' is a moving latest label; prefer explicit version-pinned runner labels", location, fix.Value);
            }
            else
            {
                AddJobWarning(job, $"jobs.'{jobId}'.runs-on label '{labelText}' is a moving latest label; prefer explicit version-pinned runner labels", location);
            }
        }
    }

    /// <summary>
    /// Determines whether the label is a detection target: built-in latest labels OR labels in fix-mapping.
    /// Uses ASCII case-insensitive comparison.
    /// </summary>
    private bool IsTargetLabel(ReadOnlySpan<byte> labelUtf8)
    {
        if (IsBuiltInLatestLabel(labelUtf8))
        {
            return true;
        }

        // Check fix-mapping keys (case-insensitive)
        if (_fixMapping is not null)
        {
            var labelStr = Encoding.UTF8.GetString(labelUtf8);
            return _fixMapping.ContainsKey(labelStr);
        }

        return false;
    }

    /// <summary>
    /// Tries to get the fix replacement value for the given label text (case-insensitive).
    /// </summary>
    private bool TryGetFixValue(string labelText, out string pinned)
    {
        if (_fixMapping is not null && _fixMapping.TryGetValue(labelText, out var value))
        {
            pinned = value;
            return true;
        }

        pinned = string.Empty;
        return false;
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

            // ASCII lower
            if (l >= (byte)'A' && l <= (byte)'Z')
            {
                l = (byte)(l + 32);
            }

            if (l != r)
            {
                return false;
            }
        }

        return true;
    }
}
