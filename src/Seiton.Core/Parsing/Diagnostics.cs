using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;
/// <summary>Severity level for parser and lint diagnostics.</summary>
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A source text replacement.</summary>
public readonly record struct TextEdit(
    int Offset,
    int Length,
    string NewText);

/// <summary>A suggested fix consisting of a description and one or more text edits.</summary>
public readonly record struct DiagnosticFix(
    string Description,
    TextEdit[] Edits);

/// <summary>A diagnostic message produced by the parser or linter.</summary>
public readonly record struct Diagnostic(
    DiagnosticSeverity Severity,
    string Message,
    TextRange Location,
    string? RuleId = null,
    TextRange[]? RelatedLocations = null,
    string? Help = null,
    string? FilePath = null,
    DiagnosticFix? Fix = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>The result of parsing a YAML document into an AST.</summary>
/// <remarks>
/// This is a pure data carrier. Resource management (Arena disposal) is handled by
/// <see cref="ParseResult"/> or <see cref="Linting.LintResult"/> which wrap this result.
/// </remarks>
internal readonly record struct ParseResultData(
    Workflow? Workflow,
    ActionMetadata? ActionMetadata,
    DiagnosticList Diagnostics,
    bool HasFatalError)
{
    /// <summary>
    /// Pre-parsed expression artifacts produced during parsing.
    /// Populated only when the parser is invoked with artifact storage enabled.
    /// When present, the linter can consume these instead of re-parsing expressions.
    /// </summary>
    internal ExpressionArtifactStore? ExpressionArtifacts { get; init; }
}
