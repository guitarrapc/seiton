namespace Seiton.Core.Parsing;

/// <summary>Display rule IDs for diagnostics that have no lint <see cref="Diagnostic.RuleId"/>.</summary>
public static class DiagnosticDisplayRuleIds
{
    /// <summary>
    /// Parser-origin diagnostics (internal <c>RuleId: null</c>) are labeled <c>syntax-check</c> in CLI/JSON/SARIF output,
    /// matching actionlint's rule tag. This is a display-only pseudo ID — not a configurable lint rule.
    /// </summary>
    public const string ParserSyntaxCheck = "syntax-check";

    /// <summary>Resolves the user-facing rule ID for output formatting.</summary>
    public static string Resolve(string? ruleId) => ruleId ?? ParserSyntaxCheck;
}
