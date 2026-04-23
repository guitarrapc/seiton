using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

public interface IRule : IPass
{
    RuleId Id { get; }

    string Name { get; }

    bool SupportsDocumentKind(DocumentKind documentKind);

    IReadOnlyList<Diagnostic> GetDiagnostics();

    void SetConfig(LintConfig config);
}
