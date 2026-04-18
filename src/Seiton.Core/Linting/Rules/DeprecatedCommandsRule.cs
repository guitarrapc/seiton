using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class DeprecatedCommandsRule : RuleBase
{
    public override string Id => "deprecated-commands";

    public override string Name => "Deprecated Commands Rule";

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecRun run)
        {
            return;
        }

        var script = run.Run.Value.AsSpan(Config.Utf8Yaml);

        if (ContainsAsciiIgnoreCase(script, "::set-output"u8))
        {
            AddStepWarning(step, "run script uses deprecated command '::set-output'; use $GITHUB_OUTPUT instead", run.Run.Range);
            return;
        }

        if (ContainsAsciiIgnoreCase(script, "::save-state"u8))
        {
            AddStepWarning(step, "run script uses deprecated command '::save-state'; use $GITHUB_STATE instead", run.Run.Range);
            return;
        }

        if (ContainsAsciiIgnoreCase(script, "::add-path"u8))
        {
            AddStepWarning(step, "run script uses deprecated command '::add-path'; use $GITHUB_PATH instead", run.Run.Range);
            return;
        }

        if (ContainsAsciiIgnoreCase(script, "::set-env"u8))
        {
            AddStepWarning(step, "run script uses deprecated command '::set-env'; use $GITHUB_ENV instead", run.Run.Range);
        }
    }

    static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> token)
    {
        if (token.Length == 0 || value.Length < token.Length)
        {
            return false;
        }

        for (var start = 0; start <= value.Length - token.Length; start++)
        {
            var matched = true;
            for (var i = 0; i < token.Length; i++)
            {
                var l = value[start + i];
                var r = token[i];
                if (l is >= (byte)'A' and <= (byte)'Z')
                {
                    l = (byte)(l + 32);
                }

                if (r is >= (byte)'A' and <= (byte)'Z')
                {
                    r = (byte)(r + 32);
                }

                if (l != r)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
