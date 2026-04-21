using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class ShellNameRule : RuleBase
{
    public override string Id => "shell-name";

    public override string Name => "Shell Name Rule";

    private Workflow? _currentWorkflow;
    private Job? _currentJob;

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
        CheckDefaultsRunShell(job.Defaults);
        _currentJob = null;
    }

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecRun run || run.Shell is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var shellSpan = run.Shell.Value.AsSpan(Config.Utf8Yaml);

        // Skip expression values ? cannot validate at static analysis time
        if (run.Shell.Expression is not null || shellSpan.IndexOf("${{"u8) >= 0)
        {
            return;
        }

        if (IsValidShellName(shellSpan))
        {
            return;
        }

        AddStepError(step, BuildInvalidShellMessage(run.Shell), run.Shell.Range);
    }

    private void CheckDefaultsRunShell(Defaults? defaults)
    {
        if (defaults is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var shellNode = defaults.Run?.Shell;
        if (shellNode is null)
        {
            return;
        }

        var shellSpan = shellNode.Value.AsSpan(Config.Utf8Yaml);

        if (shellNode.Expression is not null || shellSpan.IndexOf("${{"u8) >= 0)
        {
            return;
        }

        if (!IsValidShellName(shellSpan))
        {
            if (_currentWorkflow is not null)
            {
                AddWorkflowError(_currentWorkflow, BuildInvalidShellMessage(shellNode), shellNode.Range);
            }
            else if (_currentJob is not null)
            {
                AddJobError(_currentJob, BuildInvalidShellMessage(shellNode), shellNode.Range);
            }
        }
    }

    private string BuildInvalidShellMessage(StringNode shellNode)
    {
        var shellText = Decode(shellNode.Value);
        return $"shell name '{shellText}' is invalid; valid values are: bash, sh, pwsh, powershell, cmd, python";
    }

    private static bool IsValidShellName(ReadOnlySpan<byte> shell)
    {
        return shell.SequenceEqual("bash"u8)
            || shell.SequenceEqual("sh"u8)
            || shell.SequenceEqual("pwsh"u8)
            || shell.SequenceEqual("powershell"u8)
            || shell.SequenceEqual("cmd"u8)
            || shell.SequenceEqual("python"u8);
    }
}
