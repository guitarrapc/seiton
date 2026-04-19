using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class OverprovisionedSecretsRule : RuleBase
{
    public override string Id => "overprovisioned-secrets";

    public override string Name => "Overprovisioned Secrets Rule";

    public override void VisitJobPre(Job job)
    {
        if (job.WorkflowCall?.Secrets is not null && job.WorkflowCall.Secrets.Count > 1)
        {
            AddJobWarning(
                job,
                $"reusable workflow call passes {job.WorkflowCall.Secrets.Count} explicit secrets; map only minimum required secrets",
                BuildUsesLocation(job.WorkflowCall));
        }
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Env?.Vars is null || step.Env.Vars.Count == 0)
        {
            return;
        }

        var secretVarCount = 0;
        foreach (var pair in step.Env.Vars)
        {
            if (!ContainsSecretsReference(pair.Value.Value))
            {
                continue;
            }

            secretVarCount++;
            if (secretVarCount > 1)
            {
                AddStepWarning(
                    step,
                    "step env maps multiple secret values; reduce secret exposure to the minimum required for this step",
                    step.Env.Range);
                return;
            }
        }
    }

    bool ContainsSecretsReference(StringNode node)
    {
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        var value = node.Value.AsSpan(Config.Utf8Yaml);
        if (ContainsAsciiIgnoreCase(value, "secrets."u8)
            || ContainsAsciiIgnoreCase(value, "secrets["u8)
            || ContainsAsciiIgnoreCase(value, "tojson(secrets)"u8)
            || ContainsAsciiIgnoreCase(value, "tojson (secrets)"u8))
        {
            return true;
        }

        if (node.Expression is null)
        {
            return false;
        }

        var expression = node.Expression.Value.AsSpan(Config.Utf8Yaml);
        return ContainsAsciiIgnoreCase(expression, "secrets"u8);
    }
}
