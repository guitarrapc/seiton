namespace Seiton.Core.Linting;

/// <summary>
/// Bit flags representing rule-specific YAML configuration keys.
/// Used both by <see cref="LintConfigYamlParser"/> (to track seen keys) and
/// <see cref="RuleCatalog"/> (to define allowed keys per rule).
/// When adding a new rule-specific key, also add a corresponding entry in
/// <see cref="LintConfigYamlParser"/>'s <c>RuleKeyFlagEntries</c> and <c>AddRule()</c> switch.
/// </summary>
[Flags]
internal enum RuleKeyFlags : ushort
{
    None = 0,
    Events = 1 << 0,
    KnownHostedLabels = 1 << 1,
    PublicRegistries = 1 << 2,
    UntrustedTriggers = 1 << 3,
    OutputCommands = 1 << 4,
    AssumeEvents = 1 << 5,
    Allow = 1 << 6,
    Deny = 1 << 7,
    MaxStepEnvSecrets = 1 << 8,
    MaxJobSecrets = 1 << 9,
    IgnoreActions = 1 << 10,
    FixMapping = 1 << 11,
    StrictDetection = 1 << 12,
    Strict = 1 << 13,
}
