using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public sealed class ShellNameRule : RuleBase
{
    public override string Id => "shell-name";

    public override string Name => "Shell Name Rule";

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecRun run || run.Shell is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var shellSpan = run.Shell.Value.AsSpan(Config.Utf8Yaml);

        // Skip expression values — cannot validate at static analysis time
        if (run.Shell.Expression is not null || shellSpan.IndexOf("${{"u8) >= 0)
        {
            return;
        }

        if (IsValidShellName(shellSpan))
        {
            return;
        }

        var shellText = Decode(run.Shell.Value);
        AddStepError(step, $"shell name '{shellText}' is invalid; valid values are: bash, sh, pwsh, powershell, cmd, python", run.Shell.Range);
    }

    static bool IsValidShellName(ReadOnlySpan<byte> shell)
    {
        return shell.SequenceEqual("bash"u8)
            || shell.SequenceEqual("sh"u8)
            || shell.SequenceEqual("pwsh"u8)
            || shell.SequenceEqual("powershell"u8)
            || shell.SequenceEqual("cmd"u8)
            || shell.SequenceEqual("python"u8);
    }
}
