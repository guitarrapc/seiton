using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class InsecureCommandsRule : RuleBase
{
    public override string Id => "insecure-commands";

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
            var key = Decode(pair.Key);
            if (!string.Equals(key, "ACTIONS_ALLOW_UNSECURE_COMMANDS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var valueText = Decode(Arena.GetStringSlice(pair.Value.Value)).Trim();
            if (!IsTruthy(valueText))
            {
                continue;
            }

            envName = key;
            location = Arena.GetStringRange(pair.Value.Value);
            return true;
        }

        return false;
    }

    private static bool IsTruthy(string value)
    {
        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.Ordinal)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
