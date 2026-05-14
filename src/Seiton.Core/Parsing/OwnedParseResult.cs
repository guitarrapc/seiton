using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

/// <summary>
/// An owned parse result that retains the <see cref="AstArena"/> to keep AST objects
/// and scalar node handles valid until disposal.
/// <para>
/// <see cref="OwnedParseResult"/> is a regular class that can be stored in fields,
/// captured in closures, and passed across async boundaries.
/// </para>
/// </summary>
public sealed class OwnedParseResult : IDisposable
{
    private AstArena? _arena;

    internal OwnedParseResult(ParseResult result, AstArena? arena)
    {
        Result = result;
        _arena = arena;
    }

    /// <summary>Gets the underlying parse result data.</summary>
    public ParseResult Result { get; }

    /// <summary>Gets the parsed workflow AST, if the document is a workflow file.</summary>
    public Workflow? Workflow => Result.Workflow;

    /// <summary>Gets the parsed action metadata AST, if the document is an action file.</summary>
    public ActionMetadata? ActionMetadata => Result.ActionMetadata;

    /// <summary>Gets the parse diagnostics. These remain valid until this result is disposed.</summary>
    public DiagnosticList Diagnostics => Result.Diagnostics;

    /// <summary>Gets whether the parse result contains a fatal error.</summary>
    public bool HasFatalError => Result.HasFatalError;

    /// <summary>
    /// Returns a caller-owned copy of the diagnostics collection that remains valid
    /// even after this result has been disposed.
    /// </summary>
    public OwnedDiagnostics CopyDiagnostics()
    {
        var diags = Diagnostics;
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
