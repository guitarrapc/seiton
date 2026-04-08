using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

public interface IRule : IPass
{
    string Id { get; }

    string Name { get; }

    Diagnostic[] GetDiagnostics();

    void SetConfig(LintConfig config);
}
