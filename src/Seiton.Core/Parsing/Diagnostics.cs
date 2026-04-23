namespace Seiton.Core.Parsing;

using System.Collections.Generic;
using Seiton.Core.Parsing.Ast;

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
public readonly record struct ParseResult(
    Workflow? Workflow,
    ActionMetadata? ActionMetadata,
    Diagnostic[] Diagnostics,
    bool HasFatalError,
    AstArena? Arena = null);
