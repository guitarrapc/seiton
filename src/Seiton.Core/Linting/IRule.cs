using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

public interface IRule : IPass
{
    /// <summary>Gets the unique identifier for this rule.</summary>
    RuleId Id { get; }

    /// <summary>Gets the human-readable display name for this rule.</summary>
    string Name { get; }

    /// <summary>Returns whether this rule applies to the given <paramref name="documentKind"/>.</summary>
    bool SupportsDocumentKind(DocumentKind documentKind);

    /// <summary>Returns all diagnostics collected during the most recent visitor traversal.</summary>
    IReadOnlyList<Diagnostic> GetDiagnostics();

    /// <summary>Configures the rule with the effective <paramref name="config"/> before traversal.</summary>
    void SetConfig(LintConfig config);
}
