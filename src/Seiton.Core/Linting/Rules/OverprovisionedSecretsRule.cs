using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class OverprovisionedSecretsRule : RuleBase
{
    internal const int DefaultMaxStepEnvSecrets = 5;
    internal const int DefaultMaxJobSecrets = 5;

    private int _maxStepEnvSecrets = DefaultMaxStepEnvSecrets;
    private int _maxJobSecrets = DefaultMaxJobSecrets;

    public override string Id => "overprovisioned-secrets";

    public override string Name => "Overprovisioned Secrets Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        if (config.GetRuleConfig(Id)?.Specific is OverprovisionedSecretsSpecificConfig specific)
        {
            _maxStepEnvSecrets = specific.MaxStepEnvSecrets;
            _maxJobSecrets = specific.MaxJobSecrets;
        }
        else
        {
            _maxStepEnvSecrets = DefaultMaxStepEnvSecrets;
            _maxJobSecrets = DefaultMaxJobSecrets;
        }
    }

    public override void VisitJobPre(Job job)
    {
        if (job.WorkflowCall?.Secrets is not null && job.WorkflowCall.Secrets.Value.Count > _maxJobSecrets)
        {
            AddJobWarning(
                job,
                $"reusable workflow call passes {job.WorkflowCall.Secrets.Value.Count} explicit secrets; map only minimum required secrets",
                BuildUsesLocation(job.WorkflowCall));
        }
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Env?.Vars is null || step.Env.Vars.Value.Count == 0)
        {
            return;
        }

        var secretVarCount = 0;
        foreach (var pair in step.Env.Vars.Value)
        {
            if (!ContainsSecretsReference(pair.Value.Value))
            {
                continue;
            }

            secretVarCount++;
            if (secretVarCount > _maxStepEnvSecrets)
            {
                AddStepWarning(
                    step,
                    $"step env maps more than {_maxStepEnvSecrets} secret values; reduce secret exposure to the minimum required for this step",
                    step.Env.Range);
                return;
            }
        }
    }

    private bool ContainsSecretsReference(StringNode node)
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
