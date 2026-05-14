namespace Seiton.Core.Parsing;

/// <summary>
/// A scoped handle that pairs a <see cref="Parsing.ParseResult"/> with its owning <see cref="AstArena"/>.
/// <para>
/// This is a <c>ref struct</c> — the compiler prevents storing it in fields, capturing it in closures
/// that escape the current scope, or passing it to async methods. This guarantees that the
/// <see cref="AstArena"/> cannot outlive the scope where the handle is consumed.
/// </para>
/// <para>
/// Usage pattern:
/// <code>
/// using var handle = WorkflowParser.Parse(utf8Yaml, filePath);
/// // Access handle.Result.Diagnostics, handle.Result.Workflow, etc.
/// // Arena is automatically disposed at the end of the using scope.
/// </code>
/// </para>
/// </summary>
public ref struct ParseHandle : IDisposable
{
    private AstArena? _arena;

    internal ParseHandle(ParseResult result, AstArena? arena)
    {
        Result = result;
        _arena = arena;
    }

    /// <summary>Gets the parse result (AST, diagnostics, fatal error flag).</summary>
    public ParseResult Result { get; }

    /// <summary>Gets the arena associated with this parse handle. For internal/advanced use only.</summary>
    internal AstArena? Arena => _arena;

    /// <summary>Gets whether the parse result contains a fatal error.</summary>
    public bool HasFatalError => Result.HasFatalError;

    /// <summary>Gets the parsed workflow AST, if available.</summary>
    public Ast.Workflow? Workflow => Result.Workflow;

    /// <summary>Gets the parsed action metadata AST, if available.</summary>
    public Ast.ActionMetadata? ActionMetadata => Result.ActionMetadata;

    /// <summary>Gets the diagnostics produced during parsing.</summary>
    public DiagnosticList Diagnostics => Result.Diagnostics;

    /// <summary>
    /// Returns a caller-owned copy of the diagnostics collection.
    /// The returned <see cref="OwnedDiagnostics"/> is safe to retain beyond this handle's lifetime.
    /// </summary>
    public OwnedDiagnostics CopyDiagnostics()
    {
        var diags = Result.Diagnostics;
        if (diags.Length == 0)
            return default;
        return new OwnedDiagnostics(diags.AsSpan().ToArray());
    }

    /// <summary>Disposes the underlying <see cref="AstArena"/>, returning pooled buffers.</summary>
    public void Dispose()
    {
        _arena?.Dispose();
        _arena = null;
    }
}
