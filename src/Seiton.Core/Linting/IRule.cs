using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>A lint rule that inspects workflow/action AST nodes during visitor traversal and collects diagnostics.</summary>
public interface IRule : IPass
{
    /// <summary>Gets the unique identifier for this rule.</summary>
    public RuleId Id { get; }

    /// <summary>Gets the human-readable display name for this rule.</summary>
    public string Name { get; }

    /// <summary>Returns whether this rule applies to the given <paramref name="documentKind"/>.</summary>
    public bool SupportsDocumentKind(DocumentKind documentKind);

    /// <summary>Returns all diagnostics collected during the most recent visitor traversal.</summary>
    public IReadOnlyList<Diagnostic> GetDiagnostics();

    /// <summary>Configures the rule with the effective <paramref name="config"/> before traversal.</summary>
    public void SetConfig(LintConfig config);
}
