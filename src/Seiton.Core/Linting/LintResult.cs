using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using System.Text;

namespace Seiton.Core.Linting;

/// <summary>
/// A lint result that retains the <see cref="AstArena"/> to keep AST objects
/// and scalar node handles valid until disposal.
/// </summary>
public sealed class LintResult : IDisposable
{
    private AstArena? _arena;
    private readonly bool _ownsArena;
    private bool _disposed;
#if DEBUG
    private readonly int _generation;
#endif

    internal LintResult(LintResultData data, AstArena? arena, bool ownsArena = true)
    {
        Data = data;
        _arena = arena;
        _ownsArena = ownsArena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
    }

    /// <summary>
    /// DEBUG-only: throws when the arena was reset or disposed after this result was created
    /// (e.g. a non-owning result outliving its <c>IncrementalParseContext</c> arena reuse).
    /// Compiled out entirely in Release builds.
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertGeneration()
    {
#if DEBUG
        _arena?.AssertGeneration(_generation);
#endif
    }

    /// <summary>Gets the underlying lint result data for internal consumers.</summary>
    internal LintResultData Data { get; }

    /// <summary>Gets the parsed workflow AST root. Default ref when the document is not a workflow file.</summary>
    public WorkflowRef Workflow
    {
        get
        {
            ThrowIfDisposed();
            AssertGeneration();
            return new WorkflowRef(_arena, Data.Workflow);
        }
    }

    /// <summary>Gets the parsed action metadata AST root. Default ref when the document is not an action file.</summary>
    public ActionMetadataRef ActionMetadata
    {
        get
        {
            ThrowIfDisposed();
            AssertGeneration();
            return new ActionMetadataRef(_arena, Data.ActionMetadata);
        }
    }

    /// <summary>Gets the parsed workflow AST node, if the document is a workflow file.</summary>
    internal Workflow? WorkflowNode
    {
        get
        {
            ThrowIfDisposed();
            AssertGeneration();
            return Data.Workflow;
        }
    }

    /// <summary>Gets the lint diagnostics. These remain valid until this result is disposed.</summary>
    public DiagnosticList Diagnostics
    {
        get
        {
            ThrowIfDisposed();
            AssertGeneration();
            return Data.Diagnostics;
        }
    }

    /// <summary>Gets the parse-phase diagnostics. These remain valid until this result is disposed.</summary>
    public DiagnosticList ParseDiagnostics
    {
        get
        {
            ThrowIfDisposed();
            AssertGeneration();
            return Data.ParseDiagnostics;
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

    /// <summary>Gets the suppression summary from inline and exclusion rules.</summary>
    public SuppressionSummary SuppressionSummary
    {
        get
        {
            ThrowIfDisposed();
            return Data.SuppressionSummary;
        }
    }

    /// <summary>Gets the document kind (workflow or action metadata) used during linting.</summary>
    public DocumentKind DocumentKind
    {
        get
        {
            ThrowIfDisposed();
            return Data.DocumentKind;
        }
    }

    /// <summary>Gets the number of rules that were active (enabled and applicable) for this document.</summary>
    public int ActiveRuleCount
    {
        get
        {
            ThrowIfDisposed();
            return Data.ActiveRuleCount;
        }
    }

    /// <summary>Gets the number of rules disabled by config or opt-in status (not by document kind mismatch).</summary>
    public int DisabledRuleCount
    {
        get
        {
            ThrowIfDisposed();
            return Data.DisabledRuleCount;
        }
    }

    /// <summary>Gets the IDs of rules disabled by config or opt-in status.</summary>
    public ReadOnlySpan<string> DisabledRuleIds
    {
        get
        {
            ThrowIfDisposed();
            return Data.DisabledRuleIds.AsSpan();
        }
    }

    /// <summary>Gets the number of lint diagnostics.</summary>
    public int DiagnosticCount
    {
        get
        {
            ThrowIfDisposed();
            return Data.DiagnosticCount;
        }
    }

    /// <summary>Gets whether any diagnostics have an associated auto-fix.</summary>
    public bool HasFixableDiagnostics
    {
        get
        {
            ThrowIfDisposed();
            return Data.HasFixableDiagnostics;
        }
    }

    /// <summary>Gets the fixable diagnostics count.</summary>
    public int FixableDiagnosticCount
    {
        get
        {
            ThrowIfDisposed();
            return Data.FixableDiagnosticCount;
        }
    }

    /// <summary>Gets all diagnostics that have an associated auto-fix.</summary>
    public Diagnostic[] FixableDiagnostics
    {
        get
        {
            ThrowIfDisposed();
            return Data.FixableDiagnostics;
        }
    }

    /// <summary>Gets all auto-fix payloads from fixable diagnostics.</summary>
    public DiagnosticFix[] Fixes
    {
        get
        {
            ThrowIfDisposed();
            return Data.Fixes;
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
    /// Returns a caller-owned copy of the lint diagnostics collection that remains valid
    /// even after this result has been disposed.
    /// </summary>
    public OwnedDiagnostics CopyDiagnostics()
    {
        ThrowIfDisposed();
        return Data.CopyDiagnostics();
    }

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

    internal AstArena Arena
    {
        get
        {
            if (_disposed || _arena is null)
            {
                throw new ObjectDisposedException(nameof(LintResult));
            }

            AssertGeneration();
            return _arena;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LintResult));
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
