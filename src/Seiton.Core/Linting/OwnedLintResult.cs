using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

/// <summary>
/// An owned lint result that retains the <see cref="AstArena"/> to keep AST objects
/// and scalar node handles valid until disposal.
/// <para>
/// <see cref="OwnedLintResult"/> is a regular class that can be stored in fields,
/// captured in closures, and passed across async boundaries.
/// </para>
/// </summary>
public sealed class OwnedLintResult : IDisposable
{
    private AstArena? _arena;

    internal OwnedLintResult(LintResult result, AstArena? arena)
    {
        Result = result;
        _arena = arena;
    }

    /// <summary>Gets the underlying lint result data.</summary>
    public LintResult Result { get; }

    /// <summary>Gets the parsed workflow AST, if the document is a workflow file.</summary>
    public Workflow? Workflow => Result.Workflow;

    /// <summary>Gets the parsed action metadata AST, if the document is an action file.</summary>
    public ActionMetadata? ActionMetadata => Result.ActionMetadata;

    /// <summary>Gets the lint diagnostics. These remain valid until this result is disposed.</summary>
    public DiagnosticList Diagnostics => Result.Diagnostics;

    /// <summary>Gets the parse-phase diagnostics. These remain valid until this result is disposed.</summary>
    public DiagnosticList ParseDiagnostics => Result.ParseDiagnostics;

    /// <summary>Gets whether the parse result contains a fatal error.</summary>
    public bool HasFatalError => Result.HasFatalError;

    /// <summary>Gets the suppression summary from inline and exclusion rules.</summary>
    public SuppressionSummary SuppressionSummary => Result.SuppressionSummary;

    /// <summary>Gets the number of lint diagnostics.</summary>
    public int DiagnosticCount => Result.DiagnosticCount;

    /// <summary>Gets whether any diagnostics have an associated auto-fix.</summary>
    public bool HasFixableDiagnostics => Result.HasFixableDiagnostics;

    /// <summary>Gets the fixable diagnostics count.</summary>
    public int FixableDiagnosticCount => Result.FixableDiagnosticCount;

    /// <summary>Gets all diagnostics that have an associated auto-fix.</summary>
    public Diagnostic[] FixableDiagnostics => Result.FixableDiagnostics;

    /// <summary>
    /// Returns a caller-owned copy of the lint diagnostics collection that remains valid
    /// even after this result has been disposed.
    /// </summary>
    public OwnedDiagnostics CopyDiagnostics() => Result.CopyDiagnostics();

    /// <summary>
    /// Returns a caller-owned copy of the parse diagnostics collection that remains valid
    /// even after this result has been disposed.
    /// </summary>
    public OwnedDiagnostics CopyParseDiagnostics()
    {
        var diags = ParseDiagnostics;
        if (diags.Length == 0)
        {
            return default;
        }

        return new OwnedDiagnostics(diags.AsSpan().ToArray());
    }

    /// <summary>
    /// Gets the <see cref="AstArena"/> that backs scalar node handles
    /// (<see cref="StringNodeId"/>, <see cref="BoolNodeId"/>, etc.) in the AST.
    /// <para>
    /// Use this to resolve handle values, e.g. <c>owned.Arena.GetStringValue(job.Id)</c>.
    /// The arena remains valid until this <see cref="OwnedLintResult"/> is disposed.
    /// </para>
    /// </summary>
    public AstArena Arena => _arena ?? throw new ObjectDisposedException(nameof(OwnedLintResult));

    /// <summary>Returns pooled arrays to the shared pool. AST objects become invalid after this call.</summary>
    public void Dispose()
    {
        _arena?.Dispose();
        _arena = null;
    }
}
