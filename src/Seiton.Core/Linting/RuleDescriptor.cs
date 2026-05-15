namespace Seiton.Core.Linting;

/// <summary>
/// Describes a lint rule's static metadata without requiring instantiation for lint purposes.
/// </summary>
public readonly record struct RuleDescriptor(
    string Id,
    string Name,
    bool IsOptIn,
    bool IsOnline,
    bool IsNonDisableable,
    bool SupportsWorkflow,
    bool SupportsAction);
