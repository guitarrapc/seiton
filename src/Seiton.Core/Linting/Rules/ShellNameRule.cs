using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates that <c>shell:</c> values are recognized shell names.</summary>
public sealed class ShellNameRule() : RuleBase(RuleId.ShellName)
{
    public override string Name => "Shell Name Rule";

    private WorkflowRef _currentWorkflow;
    private JobRef _currentJob;
    private OsFamily _currentOsFamily;

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        _currentWorkflow = workflow;
        CheckDefaultsRunShell(workflow.Defaults);
        _currentWorkflow = default;
    }

    public override void VisitJobPre(JobRef job)
    {
        _currentJob = job;
        _currentOsFamily = ResolveOsFamily(job);
        CheckDefaultsRunShell(job.Defaults);
        _currentJob = default;
    }

    public override void VisitStep(StepRef step)
    {
        if (step.Exec.Kind != StepExecKind.Run || Config.Utf8Yaml is null)
        {
            return;
        }

        var run = step.Exec.AsRun();
        if (!run.Shell.HasValue)
        {
            return;
        }

        var shellSpan = run.Shell.Value;

        // Skip expression values ? cannot validate at static analysis time
        if (run.Shell.Expression.HasValue || ExpressionScanHelpers.ContainsExpressionMarker(shellSpan))
        {
            return;
        }

        if (!IsValidShellName(shellSpan))
        {
            AddStepError(step, BuildInvalidShellMessage(run.Shell), run.Shell.Range);
            return;
        }

        // OS-specific shell validation (skip custom shell templates — they are user-defined)
        if (_currentOsFamily != OsFamily.Unknown && Shells.IsValidShell(shellSpan))
        {
            CheckOsSpecificShell(step, run.Shell, shellSpan);
        }
    }

    private void CheckDefaultsRunShell(DefaultsRef defaults)
    {
        if (!defaults.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var shellNode = defaults.Run.Shell;
        if (!shellNode.HasValue)
        {
            return;
        }

        var shellSpan = shellNode.Value;

        if (shellNode.Expression.HasValue || ExpressionScanHelpers.ContainsExpressionMarker(shellSpan))
        {
            return;
        }

        if (!IsValidShellName(shellSpan))
        {
            if (_currentWorkflow.HasValue)
            {
                AddWorkflowError(_currentWorkflow, BuildInvalidShellMessage(shellNode), shellNode.Range);
            }
            else if (_currentJob.HasValue)
            {
                AddJobError(_currentJob, BuildInvalidShellMessage(shellNode), shellNode.Range);
            }
        }
    }

    private string BuildInvalidShellMessage(StringRef shellNode)
    {
        var shellText = Decode(shellNode.Slice);
        return $"shell name '{shellText}' is invalid; valid values are: {Shells.AllValidShellNames}, or a custom shell command containing '{{0}}'";
    }

    private static bool IsValidShellName(ReadOnlySpan<byte> shell)
    {
        return Shells.IsValidShell(shell)
            || shell.IndexOf("{0}"u8) >= 0;
    }

    private void CheckOsSpecificShell(StepRef step, StringRef shellNode, ReadOnlySpan<byte> shellSpan)
    {
        // Check shell availability for the detected OS
        var available = _currentOsFamily switch
        {
            OsFamily.Linux => Shells.IsAvailableOnLinux(shellSpan),
            OsFamily.MacOS => Shells.IsAvailableOnMacOS(shellSpan),
            OsFamily.Windows => Shells.IsAvailableOnWindows(shellSpan),
            _ => true,
        };

        if (!available)
        {
            var osName = _currentOsFamily.ToString().ToLowerInvariant();
            AddStepWarning(step, $"shell '{Decode(shellNode.Slice)}' is not available on {osName} runners", shellNode.Range);
        }
    }

    private OsFamily ResolveOsFamily(JobRef job)
    {
        var runsOn = job.RunsOn;
        if (!runsOn.HasValue || runsOn.LabelsExpr.HasValue || !runsOn.Labels.HasValue || Config.Utf8Yaml is null)
        {
            return OsFamily.Unknown;
        }

        OsFamily resolved = OsFamily.Unknown;
        for (var i = 0; i < runsOn.Labels.Count; i++)
        {
            var label = runsOn.Labels[i];
            if (label.Expression.HasValue)
            {
                return OsFamily.Unknown;
            }

            var labelUtf8 = label.Value;
            var family = GetOsFamilyFromLabel(labelUtf8);
            if (family == OsFamily.Unknown)
            {
                continue;
            }

            if (resolved != OsFamily.Unknown && resolved != family)
            {
                return OsFamily.Unknown; // conflicting labels, can't determine OS
            }

            resolved = family;
        }

        return resolved;
    }

    private static OsFamily GetOsFamilyFromLabel(ReadOnlySpan<byte> labelUtf8)
    {
        if (StartsWithAsciiIgnoreCase(labelUtf8, "ubuntu-"u8))
        {
            return OsFamily.Linux;
        }

        if (StartsWithAsciiIgnoreCase(labelUtf8, "windows-"u8))
        {
            return OsFamily.Windows;
        }

        if (StartsWithAsciiIgnoreCase(labelUtf8, "macos-"u8))
        {
            return OsFamily.MacOS;
        }

        return OsFamily.Unknown;
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

    private enum OsFamily
    {
        Unknown,
        Linux,
        Windows,
        MacOS,
    }
}
