using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class EnvVarRule : RuleBase
{
    public override string Id => "env-var";

    public override string Name => "Env Var Rule";

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        ValidateEnv(workflow.Env, static (rule, message, location, target) =>
            rule.AddWorkflowWarning(target, message, location), workflow, "workflow.env");
    }

    public override void VisitJobPre(Job job)
    {
        ValidateEnv(job.Env, static (rule, message, location, target) =>
            rule.AddJobWarning(target, message, location), job, "job.env");
    }

    public override void VisitStep(Step step)
    {
        ValidateEnv(step.Env, static (rule, message, location, target) =>
            rule.AddStepWarning(target, message, location), step, "step.env");
    }

    private void ValidateEnv<TTarget>(
        Env? env,
        Action<EnvVarRule, string, TextRange, TTarget> report,
        TTarget target,
        string sinkName)
    {
        if (env?.Vars is null || env.Vars.Value.Count == 0 || Config.Utf8Yaml is null)
        {
            return;
        }

        foreach (var pair in env.Vars)
        {
            var envVar = pair.Value;
            if (IsPortableEnvName(Arena.GetStringValue(envVar.Name)))
            {
                continue;
            }

            var name = Decode(Arena.GetStringSlice(envVar.Name));
            report(
                this,
                $"{sinkName} key '{name}' is not portable; use [A-Z_][A-Z0-9_]* naming",
                Arena.GetStringRange(envVar.Name),
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
