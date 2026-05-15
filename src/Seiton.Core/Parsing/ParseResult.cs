using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

/// <summary>
/// A parse result that retains the <see cref="AstArena"/> to keep AST objects
/// and scalar node handles valid until disposal.
/// </summary>
public sealed class ParseResult : IDisposable
{
    private AstArena? _arena;
    private readonly bool _ownsArena;
    private bool _disposed;

    internal ParseResult(ParseResultData data, AstArena? arena, bool ownsArena = true)
    {
        Data = data;
        _arena = arena;
        _ownsArena = ownsArena;
    }

    /// <summary>Gets the underlying parse result data for internal consumers.</summary>
    internal ParseResultData Data { get; }

    /// <summary>Gets the parsed workflow AST, if the document is a workflow file.</summary>
    public Workflow? Workflow
    {
        get
        {
            ThrowIfDisposed();
            return Data.Workflow;
        }
    }

    /// <summary>Gets the parsed action metadata AST, if the document is an action file.</summary>
    public ActionMetadata? ActionMetadata
    {
        get
        {
            ThrowIfDisposed();
            return Data.ActionMetadata;
        }
    }

    /// <summary>Gets the parse diagnostics. These remain valid until this result is disposed.</summary>
    public DiagnosticList Diagnostics
    {
        get
        {
            ThrowIfDisposed();
            return Data.Diagnostics;
        }
    }

    /// <summary>Gets whether the parse result contains a fatal error.</summary>
    public bool HasFatalError
    {
        get
        {
            ThrowIfDisposed();
            return Data.HasFatalError;
        }
    }

    /// <summary>Gets the original UTF-8 YAML source bytes.</summary>
    internal ReadOnlySpan<byte> Source => Arena.Source;

    public string GetString(StringNodeId id) => Encoding.UTF8.GetString(GetUtf8(id));

    /// <summary>Decodes a <see cref="Utf8Slice"/> map key into a string using the underlying source bytes.</summary>
    public string GetString(Utf8Slice key) => Encoding.UTF8.GetString(key.AsSpan(Arena.Source));

    public ReadOnlySpan<byte> GetUtf8(StringNodeId id) => Arena.GetStringValue(id);

    internal Utf8Slice GetSlice(StringNodeId id) => Arena.GetStringSlice(id);

    internal bool IsQuoted(StringNodeId id) => Arena.GetStringQuoted(id);

    public TextRange GetRange(StringNodeId id) => Arena.GetStringRange(id);

    public TextRange GetRange(BoolNodeId id) => Arena.GetBoolRange(id);

    public TextRange GetRange(IntNodeId id) => Arena.GetIntRange(id);

    public TextRange GetRange(FloatNodeId id) => Arena.GetFloatRange(id);

    internal StringNodeId GetExpression(StringNodeId id) => Arena.GetStringExpression(id);

    internal StringNodeId GetExpression(BoolNodeId id) => Arena.GetBoolExpression(id);

    internal StringNodeId GetExpression(IntNodeId id) => Arena.GetIntExpression(id);

    internal StringNodeId GetExpression(FloatNodeId id) => Arena.GetFloatExpression(id);

    public bool GetBool(BoolNodeId id) => Arena.GetBoolValue(id);

    public long GetInt(IntNodeId id) => Arena.GetIntValue(id);

    public double GetFloat(FloatNodeId id) => Arena.GetFloatValue(id);

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

    internal AstArena Arena => _disposed || _arena is null ? throw new ObjectDisposedException(nameof(ParseResult)) : _arena;

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ParseResult));
        }
    }

    /// <summary>Returns pooled arrays to the shared pool. AST objects become invalid after this call.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_ownsArena)
        {
            _arena?.Dispose();
        }

        _arena = null;
        _disposed = true;
    }
}
