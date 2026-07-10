using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags use of <c>ACTIONS_ALLOW_UNSECURE_COMMANDS</c> which enables deprecated insecure workflow commands.</summary>
public sealed class InsecureCommandsRule() : RuleBase(RuleId.InsecureCommands)
{
    public override string Name => "Insecure Commands Rule";

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        if (workflow.Env is null)
        {
            return;
        }

        if (TryFindInsecureCommandsEnv(workflow.Env, out var envName, out var location))
        {
            AddWorkflowWarning(
                workflow,
                $"workflow env '{envName}' enables ACTIONS_ALLOW_UNSECURE_COMMANDS; remove this flag and migrate to environment files",
                location);
        }
    }

    public override void VisitJobPre(Job job)
    {
        if (job.Env is null)
        {
            return;
        }

        if (TryFindInsecureCommandsEnv(job.Env, out var envName, out var location))
        {
            AddJobWarning(
                job,
                $"job env '{envName}' enables ACTIONS_ALLOW_UNSECURE_COMMANDS; remove this flag and migrate to environment files",
                location);
        }
    }

    public override void VisitStep(Step step)
    {
        if (step.Env is null)
        {
            return;
        }

        if (TryFindInsecureCommandsEnv(step.Env, out var envName, out var location))
        {
            AddStepWarning(
                step,
                $"step env '{envName}' enables ACTIONS_ALLOW_UNSECURE_COMMANDS; remove this flag and migrate to environment files",
                location);
        }
    }

    private bool TryFindInsecureCommandsEnv(Env env, out string envName, out TextRange location)
    {
        envName = string.Empty;
        location = env.Range;
        if (env.Vars is null || env.Vars.Value.Count == 0 || Config.Utf8Yaml is null)
        {
            return false;
        }

        foreach (var pair in env.Vars)
        {
            if (!EqualsAsciiIgnoreCase(pair.Key.AsSpan(Config.Utf8Yaml), "ACTIONS_ALLOW_UNSECURE_COMMANDS"u8))
            {
                continue;
            }

            var valueUtf8 = TrimAsciiWhiteSpace(Arena.GetStringValue(pair.Value.Value));
            if (!IsTruthy(valueUtf8))
            {
                continue;
            }

            // Decode only on the diagnostic path, preserving the source casing in the message.
            envName = Decode(pair.Key);
            location = Arena.GetStringRange(pair.Value.Value);
            return true;
        }

        return false;
    }

    private static bool IsTruthy(ReadOnlySpan<byte> value)
    {
        return EqualsAsciiIgnoreCase(value, "true"u8)
            || value.SequenceEqual("1"u8)
            || EqualsAsciiIgnoreCase(value, "yes"u8)
            || EqualsAsciiIgnoreCase(value, "on"u8);
    }
}
