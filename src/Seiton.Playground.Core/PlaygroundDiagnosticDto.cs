namespace Seiton.Playground;

/// <summary>JSON-serializable diagnostic for the browser playground UI.</summary>
public sealed class PlaygroundDiagnosticDto
{
    /// <summary>Human-readable diagnostic text.</summary>
    public required string Message { get; init; }

    /// <summary>1-based start line in the source file.</summary>
    public required int Line { get; init; }

    /// <summary>1-based start column in the source file.</summary>
    public required int Column { get; init; }

    /// <summary>Diagnostic severity (<c>Info</c>, <c>Warning</c>, or <c>Error</c>).</summary>
    public required string Severity { get; init; }

    /// <summary>Lint rule identifier, if applicable.</summary>
    public string? RuleId { get; init; }

    /// <summary>Whether an automatic fix is available for this diagnostic.</summary>
    public bool Fixable { get; init; }

    /// <summary>Summary of the suggested fix, when <see cref="Fixable"/> is true.</summary>
    public string? FixDescription { get; init; }
}
