using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates that <c>shell:</c> values are recognized shell names.</summary>
public sealed class ShellNameRule() : RuleBase(RuleId.ShellName)
{
    public override string Name => "Shell Name Rule";

    private Workflow? _currentWorkflow;
    private Job? _currentJob;
    private OsFamily _currentOsFamily;

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        _currentWorkflow = workflow;
        CheckDefaultsRunShell(workflow.Defaults);
        _currentWorkflow = null;
    }

    public override void VisitJobPre(Job job)
    {
        _currentJob = job;
        _currentOsFamily = ResolveOsFamily(job);
        CheckDefaultsRunShell(job.Defaults);
        _currentJob = null;
    }

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecRun run || !run.Shell.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var shellSpan = Arena.GetStringValue(run.Shell);

        // Skip expression values ? cannot validate at static analysis time
        if (Arena.GetStringExpression(run.Shell).HasValue || shellSpan.IndexOf("${{"u8) >= 0)
        {
            return;
        }

        if (!IsValidShellName(shellSpan))
        {
            AddStepError(step, BuildInvalidShellMessage(run.Shell), Arena.GetStringRange(run.Shell));
            return;
        }

        // OS-specific shell validation
        if (_currentOsFamily != OsFamily.Unknown)
        {
            CheckOsSpecificShell(step, run.Shell, shellSpan);
        }
    }

    private void CheckDefaultsRunShell(Defaults? defaults)
    {
        if (defaults is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var shellNodeNullable = defaults.Run?.Shell;
        if (shellNodeNullable is null || !shellNodeNullable.Value.HasValue)
        {
            return;
        }

        var shellNode = shellNodeNullable.Value;
        var shellSpan = Arena.GetStringValue(shellNode);

        if (Arena.GetStringExpression(shellNode).HasValue || shellSpan.IndexOf("${{"u8) >= 0)
        {
            return;
        }

        if (!IsValidShellName(shellSpan))
        {
            if (_currentWorkflow is not null)
            {
                AddWorkflowError(_currentWorkflow, BuildInvalidShellMessage(shellNode), Arena.GetStringRange(shellNode));
            }
            else if (_currentJob is not null)
            {
                AddJobError(_currentJob, BuildInvalidShellMessage(shellNode), Arena.GetStringRange(shellNode));
            }
        }
    }

    private string BuildInvalidShellMessage(StringNodeId shellNode)
    {
        var shellText = Decode(Arena.GetStringSlice(shellNode));
        return $"shell name '{shellText}' is invalid; valid values are: bash, sh, pwsh, powershell, cmd, python, or a custom shell command containing '{{0}}'";
    }

    private static bool IsValidShellName(ReadOnlySpan<byte> shell)
    {
        return shell.SequenceEqual("bash"u8)
            || shell.SequenceEqual("sh"u8)
            || shell.SequenceEqual("pwsh"u8)
            || shell.SequenceEqual("powershell"u8)
            || shell.SequenceEqual("cmd"u8)
            || shell.SequenceEqual("python"u8)
            || shell.IndexOf("{0}"u8) >= 0;
    }

    private void CheckOsSpecificShell(Step step, StringNodeId shellNode, ReadOnlySpan<byte> shellSpan)
    {
        // cmd and powershell are Windows-only
        if (shellSpan.SequenceEqual("cmd"u8) && _currentOsFamily != OsFamily.Windows)
        {
            var osName = _currentOsFamily.ToString().ToLowerInvariant();
            AddStepWarning(step, $"shell 'cmd' is not available on {osName} runners", Arena.GetStringRange(shellNode));
        }
        else if (shellSpan.SequenceEqual("powershell"u8) && _currentOsFamily != OsFamily.Windows)
        {
            var osName = _currentOsFamily.ToString().ToLowerInvariant();
            AddStepWarning(step, $"shell 'powershell' is not available on {osName} runners; use 'pwsh' instead", Arena.GetStringRange(shellNode));
        }
    }

    private OsFamily ResolveOsFamily(Job job)
    {
        var runsOn = job.RunsOn;
        if (runsOn is null || runsOn.LabelsExpr.HasValue || runsOn.Labels is null || Config.Utf8Yaml is null)
        {
            return OsFamily.Unknown;
        }

        OsFamily resolved = OsFamily.Unknown;
        for (var i = 0; i < runsOn.Labels.Length; i++)
        {
            var label = runsOn.Labels[i];
            if (Arena.GetStringExpression(label).HasValue)
            {
                return OsFamily.Unknown;
            }

            var labelUtf8 = Arena.GetStringValue(label);
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
