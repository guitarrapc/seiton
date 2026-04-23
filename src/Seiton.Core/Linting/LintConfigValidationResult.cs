using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>Result of validating and normalizing seiton configuration YAML.</summary>
public readonly record struct LintConfigValidationResult(LintConfig? Config, Diagnostic[] Diagnostics)
{
    /// <summary>Gets whether the validation produced no error-level diagnostics.</summary>
    public bool IsValid
    {
        get
        {
            for (var i = 0; i < Diagnostics.Length; i++)
            {
                if (Diagnostics[i].Severity == DiagnosticSeverity.Error)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
