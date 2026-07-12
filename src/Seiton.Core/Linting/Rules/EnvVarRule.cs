using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates environment variable name conventions in <c>env:</c> blocks.</summary>
public sealed class EnvVarRule() : RuleBase(RuleId.EnvVar)
{
    private const string NonPortableNameHelp =
        "rename to UPPER_SNAKE_CASE (e.g. upstream -> UPSTREAM) and update all references; or pass ${{ inputs.* }} directly in with: when the value is only forwarded once";

    public override string Name => "Env Var Rule";

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        ValidateEnv(workflow.Env, static (rule, message, location, target) =>
            rule.AddWorkflowWarning(target, message, location, NonPortableNameHelp), workflow, "workflow.env");
    }

    public override void VisitJobPre(JobRef job)
    {
        ValidateEnv(job.Env, static (rule, message, location, target) =>
            rule.AddJobWarning(target, message, location, NonPortableNameHelp), job, "job.env");
    }

    public override void VisitStep(StepRef step)
    {
        ValidateEnv(step.Env, static (rule, message, location, target) =>
            rule.AddStepWarning(target, message, location, NonPortableNameHelp), step, "step.env");
    }

    private void ValidateEnv<TTarget>(
        EnvRef env,
        Action<EnvVarRule, string, TextRange, TTarget> report,
        TTarget target,
        string sinkName)
    {
        if (!env.Vars.HasValue || env.Vars.Count == 0 || Config.Utf8Yaml is null)
        {
            return;
        }

        foreach (var pair in env.Vars)
        {
            var envVar = pair.Value;
            if (IsPortableEnvName(envVar.Name.Value))
            {
                continue;
            }

            var name = envVar.Name.Decode();
            report(
                this,
                $"{sinkName} key '{name}' is not portable; use [A-Z_][A-Z0-9_]* naming",
                envVar.Name.Range,
                target);
        }
    }

    private static bool IsPortableEnvName(ReadOnlySpan<byte> name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        var first = name[0];
        if (!((first >= (byte)'A' && first <= (byte)'Z') || first == (byte)'_'))
        {
            return false;
        }

        for (var i = 1; i < name.Length; i++)
        {
            var b = name[i];
            var isUpper = b >= (byte)'A' && b <= (byte)'Z';
            var isDigit = b >= (byte)'0' && b <= (byte)'9';
            if (!isUpper && !isDigit && b != (byte)'_')
            {
                return false;
            }
        }

        return true;
    }
}
