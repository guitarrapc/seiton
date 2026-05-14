using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

/// <summary>
/// A caller-owned parse result that retains the <see cref="AstArena"/> to keep AST objects
/// and scalar node handles valid indefinitely.
/// <para>
/// Unlike <see cref="ParseHandle"/> (which is a <c>ref struct</c> that must be consumed within
/// a single scope), <see cref="OwnedParseResult"/> is a regular class that can be stored in
/// fields, captured in closures, and passed across async boundaries.
/// </para>
/// <para>
/// Obtain an instance via <see cref="ParseHandle.Detach"/>.
/// </para>
/// <para>
/// Call <see cref="Dispose"/> when done to return pooled arrays to the shared pool.
/// If not disposed, resources will be reclaimed by the finalizer (but with delayed cleanup).
/// </para>
/// </summary>
public sealed class OwnedParseResult : IDisposable
{
    private AstArena? _arena;

    internal OwnedParseResult(
        Workflow? workflow,
        ActionMetadata? actionMetadata,
        OwnedDiagnostics diagnostics,
        bool hasFatalError,
        AstArena? arena)
    {
        Workflow = workflow;
        ActionMetadata = actionMetadata;
        Diagnostics = diagnostics;
        HasFatalError = hasFatalError;
        _arena = arena;
    }

    /// <summary>Gets the parsed workflow AST, if the document is a workflow file.</summary>
    public Workflow? Workflow { get; }

    /// <summary>Gets the parsed action metadata AST, if the document is an action file.</summary>
    public ActionMetadata? ActionMetadata { get; }

    /// <summary>
    /// Gets the caller-owned diagnostics. These are safe to retain indefinitely,
    /// independent of the arena lifetime.
    /// </summary>
    public OwnedDiagnostics Diagnostics { get; }

    /// <summary>Gets whether the parse result contains a fatal error.</summary>
    public bool HasFatalError { get; }

    /// <summary>
    /// Gets the <see cref="AstArena"/> that backs scalar node handles
    /// (<see cref="StringNodeId"/>, <see cref="BoolNodeId"/>, etc.) in the AST.
    /// <para>
    /// Use this to resolve handle values, e.g. <c>owned.Arena.GetStringValue(job.Id)</c>.
    /// The arena remains valid until this <see cref="OwnedParseResult"/> is disposed.
    /// </para>
    /// </summary>
    public AstArena Arena => _arena ?? throw new ObjectDisposedException(nameof(OwnedParseResult));

    /// <summary>Returns pooled arrays to the shared pool. AST objects become invalid after this call.</summary>
    public void Dispose()
    {
        _arena?.Dispose();
        _arena = null;
    }
}
