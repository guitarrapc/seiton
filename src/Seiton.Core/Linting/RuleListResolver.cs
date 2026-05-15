namespace Seiton.Core.Linting;

/// <summary>
/// Describes a rule's effective enabled state given a configuration.
/// </summary>
public readonly record struct RuleStatus(
    RuleDescriptor Rule,
    bool Enabled,
    string Reason);

/// <summary>
/// Resolves the effective enabled/disabled state of all rules given a <see cref="LintConfig"/>.
/// </summary>
public static class RuleListResolver
{
    /// <summary>
    /// Returns the status of every registered rule, reflecting the provided configuration.
    /// </summary>
    public static IReadOnlyList<RuleStatus> Resolve(LintConfig? config)
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var statuses = new RuleStatus[descriptors.Count];

        for (var i = 0; i < descriptors.Count; i++)
        {
            var d = descriptors[i];
            statuses[i] = ResolveStatus(d, config);
        }

        return statuses;
    }

    private static RuleStatus ResolveStatus(RuleDescriptor descriptor, LintConfig? config)
    {
        // Check config for explicit override
        var ruleConfig = config?.GetRuleConfig(descriptor.Id);

        if (ruleConfig is not null)
        {
            if (!ruleConfig.Enabled)
            {
                return new RuleStatus(descriptor, Enabled: false, Reason: "config (disabled)");
            }

            // Opt-in rule explicitly configured (with enabled: true or just presence)
            if (descriptor.IsOptIn)
            {
                return new RuleStatus(descriptor, Enabled: true, Reason: "config (enabled)");
            }

            // Default-on rule with config present but enabled = true
            return new RuleStatus(descriptor, Enabled: true, Reason: "default");
        }

        // No config entry: opt-in rules are disabled by default
        if (descriptor.IsOptIn)
        {
            return new RuleStatus(descriptor, Enabled: false, Reason: "opt-in (not configured)");
        }

        // Default-on rule with no config override
        return new RuleStatus(descriptor, Enabled: true, Reason: "default");
    }
}
