using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class OverprovisionedSecretsRule() : RuleBase(RuleId.OverprovisionedSecrets)
{
    internal const int DefaultMaxStepEnvSecrets = 5;
    internal const int DefaultMaxJobSecrets = 5;

    private int _maxStepEnvSecrets = DefaultMaxStepEnvSecrets;
    private int _maxJobSecrets = DefaultMaxJobSecrets;

    public override string Name => "Overprovisioned Secrets Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        var ruleConfig = config.GetRuleConfig(Id);
        if (ruleConfig?.MaxStepEnvSecrets is not null || ruleConfig?.MaxJobSecrets is not null)
        {
            _maxStepEnvSecrets = ruleConfig.MaxStepEnvSecrets ?? DefaultMaxStepEnvSecrets;
            _maxJobSecrets = ruleConfig.MaxJobSecrets ?? DefaultMaxJobSecrets;
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

    private bool ContainsSecretsReference(StringNodeId node)
    {
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        var value = Arena.GetStringValue(node);
        if (ContainsAsciiIgnoreCase(value, "secrets."u8)
            || ContainsAsciiIgnoreCase(value, "secrets["u8)
            || ContainsAsciiIgnoreCase(value, "tojson(secrets)"u8)
            || ContainsAsciiIgnoreCase(value, "tojson (secrets)"u8))
        {
            return true;
        }

        if (!Arena.GetStringExpression(node).HasValue)
        {
            return false;
        }

        var expression = Arena.GetStringValue(Arena.GetStringExpression(node));
        return ContainsAsciiIgnoreCase(expression, "secrets"u8);
    }
}
