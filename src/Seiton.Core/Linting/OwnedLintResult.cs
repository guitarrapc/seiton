using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

/// <summary>
/// A caller-owned lint result that retains the <see cref="AstArena"/> to keep AST objects
/// and scalar node handles valid indefinitely.
/// <para>
/// Unlike <see cref="LintHandle"/> (which is a <c>ref struct</c> that must be consumed within
/// a single scope), <see cref="OwnedLintResult"/> is a regular class that can be stored in
/// fields, captured in closures, and passed across async boundaries.
/// </para>
/// <para>
/// Obtain an instance via <see cref="LintHandle.Detach"/>.
/// </para>
/// <para>
/// Call <see cref="Dispose"/> when done to return pooled arrays to the shared pool.
/// If not disposed, resources will be reclaimed by the finalizer (but with delayed cleanup).
/// </para>
/// </summary>
public sealed class OwnedLintResult : IDisposable
{
    private AstArena? _arena;

    internal OwnedLintResult(
        Workflow? workflow,
        ActionMetadata? actionMetadata,
        OwnedDiagnostics diagnostics,
        OwnedDiagnostics parseDiagnostics,
        bool hasFatalError,
        SuppressionSummary suppressionSummary,
        AstArena? arena)
    {
        Workflow = workflow;
        ActionMetadata = actionMetadata;
        Diagnostics = diagnostics;
        ParseDiagnostics = parseDiagnostics;
        HasFatalError = hasFatalError;
        SuppressionSummary = suppressionSummary;
        _arena = arena;
    }

    /// <summary>Gets the parsed workflow AST, if the document is a workflow file.</summary>
    public Workflow? Workflow { get; }

    /// <summary>Gets the parsed action metadata AST, if the document is an action file.</summary>
    public ActionMetadata? ActionMetadata { get; }

    /// <summary>
    /// Gets the caller-owned lint diagnostics (combined parse + rule diagnostics, post-processed).
    /// These are safe to retain indefinitely, independent of the arena lifetime.
    /// </summary>
    public OwnedDiagnostics Diagnostics { get; }

    /// <summary>
    /// Gets the caller-owned parse-phase diagnostics.
    /// These are safe to retain indefinitely.
    /// </summary>
    public OwnedDiagnostics ParseDiagnostics { get; }

    /// <summary>Gets whether the parse result contains a fatal error.</summary>
    public bool HasFatalError { get; }

    /// <summary>Gets the suppression summary from inline and exclusion rules.</summary>
    public SuppressionSummary SuppressionSummary { get; }

    /// <summary>Gets the number of lint diagnostics.</summary>
    public int DiagnosticCount => Diagnostics.Length;

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
