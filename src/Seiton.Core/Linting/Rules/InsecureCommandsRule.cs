using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags use of <c>ACTIONS_ALLOW_UNSECURE_COMMANDS</c> which enables deprecated insecure workflow commands.</summary>
public sealed class InsecureCommandsRule() : RuleBase(RuleId.InsecureCommands)
{
    public override string Name => "Insecure Commands Rule";

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        if (!workflow.Env.HasValue)
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

    public override void VisitJobPre(JobRef job)
    {
        if (!job.Env.HasValue)
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

    public override void VisitStep(StepRef step)
    {
        if (!step.Env.HasValue)
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

    private bool TryFindInsecureCommandsEnv(EnvRef env, out string envName, out TextRange location)
    {
        envName = string.Empty;
        location = env.Range;
        if (!env.Vars.HasValue || env.Vars.Count == 0 || Config.Utf8Yaml is null)
        {
            return false;
        }

        foreach (var pair in env.Vars)
        {
            if (!EqualsAsciiIgnoreCase(pair.Key.Bytes, "ACTIONS_ALLOW_UNSECURE_COMMANDS"u8))
            {
                continue;
            }

            var valueUtf8 = TrimAsciiWhiteSpace(pair.Value.Value.Value);
            if (!IsTruthy(valueUtf8))
            {
                continue;
            }

            // Decode only on the diagnostic path, preserving the source casing in the message.
            envName = pair.Key.Decode();
            location = pair.Value.Value.Range;
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
