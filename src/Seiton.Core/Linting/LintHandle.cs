using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

/// <summary>
/// A scoped handle that pairs a <see cref="Linting.LintResult"/> with its owning <see cref="AstArena"/>.
/// <para>
/// This is a <c>ref struct</c> — the compiler prevents storing it in fields, capturing it in closures
/// that escape the current scope, or passing it to async methods. This guarantees that the
/// <see cref="AstArena"/> cannot outlive the scope where the handle is consumed.
/// </para>
/// <para>
/// Usage pattern:
/// <code>
/// using var handle = engine.Check(utf8Yaml, filePath, config);
/// // Access handle.Diagnostics, handle.Result.Workflow, etc.
/// // Call handle.CopyDiagnostics() if diagnostics must outlive this scope.
/// // Arena is automatically disposed at the end of the using scope.
/// </code>
/// </para>
/// </summary>
public ref struct LintHandle : IDisposable
{
    private AstArena? _arena;

    internal LintHandle(LintResult result, AstArena? arena)
    {
        Result = result;
        _arena = arena;
    }

    /// <summary>Gets the lint result (diagnostics, suppression summary, parse result).</summary>
    public LintResult Result { get; }

    /// <summary>Gets the parse result from the underlying lint result.</summary>
    public ParseResult ParseResult => Result.ParseResult;

    /// <summary>Gets the arena associated with this handle. For internal/advanced use only.</summary>
    internal AstArena? Arena => _arena;

    /// <summary>Gets the diagnostics from the lint result.</summary>
    public DiagnosticList Diagnostics => Result.Diagnostics;

    /// <summary>Gets the number of diagnostics.</summary>
    public int DiagnosticCount => Result.DiagnosticCount;

    /// <summary>Gets the parsed workflow AST, if available.</summary>
    public Workflow? Workflow => Result.Workflow;

    /// <summary>Gets the parsed action metadata AST, if available.</summary>
    public ActionMetadata? ActionMetadata => Result.ActionMetadata;

    /// <summary>Gets whether a fatal parse error occurred.</summary>
    public bool HasFatalError => Result.HasFatalError;

    /// <summary>Gets whether any diagnostics have an associated auto-fix.</summary>
    public bool HasFixableDiagnostics => Result.HasFixableDiagnostics;

    /// <summary>Gets the fixable diagnostics count.</summary>
    public int FixableDiagnosticCount => Result.FixableDiagnosticCount;

    /// <summary>Gets all diagnostics that have an associated auto-fix.</summary>
    public Diagnostic[] FixableDiagnostics => Result.FixableDiagnostics;

    /// <summary>Gets the parse diagnostics.</summary>
    public DiagnosticList ParseDiagnostics => Result.ParseDiagnostics;

    /// <summary>Gets the suppression summary.</summary>
    public SuppressionSummary SuppressionSummary => Result.SuppressionSummary;

    /// <summary>
    /// Returns a caller-owned copy of the diagnostics collection.
    /// The returned <see cref="OwnedDiagnostics"/> is safe to retain beyond this handle's lifetime.
    /// </summary>
    public OwnedDiagnostics CopyDiagnostics() => Result.CopyDiagnostics();

    /// <summary>Disposes the underlying <see cref="AstArena"/>, returning pooled buffers.</summary>
    public void Dispose()
    {
        _arena?.Dispose();
        _arena = null;
    }
}
