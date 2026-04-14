namespace Seiton.Core.Linting;

internal static class RuleCatalog
{
    // Rule responsibilities are intentionally split:
    // - job-structure: cross-key structural constraints on Job shape.
    // - reusable-workflow: uses/with/secrets semantics and forbidden keys in reusable calls.
    // - permissions: scalar/scope value domain validation for permissions.
    // - popular-action-inputs: known-action input-name validation (warning-level).
    static readonly (string Id, int Priority, Func<IRule> Factory)[] DefaultRuleFactories =
    [
        ("job-structure", 0, static () => new JobStructureRule()),
        ("reusable-workflow", 1, static () => new ReusableWorkflowRule()),
        ("permissions", 2, static () => new PermissionsRule()),
        ("popular-action-inputs", 3, static () => new PopularActionInputsRule()),
    ];

    public static IRule[] CreateDefaultRules()
    {
        var rules = new IRule[DefaultRuleFactories.Length];
        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            rules[i] = DefaultRuleFactories[i].Factory();
        }

        return rules;
    }

    public static int GetPriority(string? ruleId)
    {
        if (string.IsNullOrEmpty(ruleId))
        {
            return int.MaxValue;
        }

        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            if (string.Equals(DefaultRuleFactories[i].Id, ruleId, StringComparison.Ordinal))
            {
                return DefaultRuleFactories[i].Priority;
            }
        }

        return int.MaxValue - 1;
    }
}
