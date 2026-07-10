using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

/// <summary>
/// Type-safe handle referencing a string scalar node stored in <see cref="AstArena"/>.
/// Default value (<c>default</c>) represents "no value" (equivalent to <c>null</c> on the old <c>StringNode?</c>).
/// </summary>
/// <remarks>To get the string value, call <c>result.GetString(id)</c> on the <c>ParseResult</c> or <c>LintResult</c> that produced this handle.</remarks>
[DebuggerDisplay("{DebugDisplay,nq}")]
public readonly record struct StringNodeId : IEquatable<StringNodeId>
{
    // 0 = None (default), positive = valid (actual index = _raw - 1)
    private readonly int _raw;

    private StringNodeId(int raw) => _raw = raw;

    /// <summary>Gets whether this handle points to a valid node (<c>false</c> for <c>default</c>).</summary>
    public bool HasValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _raw > 0;
    }

    internal int Index
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _raw - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static StringNodeId FromIndex(int index) => new(index + 1);

    public override string ToString() => HasValue ? $"StringNodeId({Index})" : "StringNodeId(None)";
    private string DebugDisplay => HasValue ? $"String[{Index}]" : "(none)";
}

/// <summary>
/// Type-safe handle referencing a bool scalar node stored in <see cref="AstArena"/>.
/// </summary>
/// <remarks>To get the bool value, call <c>result.GetBool(id)</c> on the <c>ParseResult</c> or <c>LintResult</c> that produced this handle.</remarks>
[DebuggerDisplay("{DebugDisplay,nq}")]
public readonly record struct BoolNodeId : IEquatable<BoolNodeId>
{
    private readonly int _raw;

    private BoolNodeId(int raw) => _raw = raw;

    /// <summary>Gets whether this handle points to a valid node.</summary>
    public bool HasValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _raw > 0;
    }

    internal int Index
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _raw - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static BoolNodeId FromIndex(int index) => new(index + 1);

    public override string ToString() => HasValue ? $"BoolNodeId({Index})" : "BoolNodeId(None)";
    private string DebugDisplay => HasValue ? $"Bool[{Index}]" : "(none)";
}

/// <summary>
/// Type-safe handle referencing an int scalar node stored in <see cref="AstArena"/>.
/// </summary>
/// <remarks>To get the int value, call <c>result.GetInt(id)</c> on the <c>ParseResult</c> or <c>LintResult</c> that produced this handle.</remarks>
[DebuggerDisplay("{DebugDisplay,nq}")]
public readonly record struct IntNodeId : IEquatable<IntNodeId>
{
    private readonly int _raw;

    private IntNodeId(int raw) => _raw = raw;

    /// <summary>Gets whether this handle points to a valid node.</summary>
    public bool HasValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _raw > 0;
    }

    internal int Index
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _raw - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IntNodeId FromIndex(int index) => new(index + 1);

    public override string ToString() => HasValue ? $"IntNodeId({Index})" : "IntNodeId(None)";
    private string DebugDisplay => HasValue ? $"Int[{Index}]" : "(none)";
}

/// <summary>
/// Type-safe handle referencing a float scalar node stored in <see cref="AstArena"/>.
/// </summary>
/// <remarks>To get the float value, call <c>result.GetFloat(id)</c> on the <c>ParseResult</c> or <c>LintResult</c> that produced this handle.</remarks>
[DebuggerDisplay("{DebugDisplay,nq}")]
public readonly record struct FloatNodeId : IEquatable<FloatNodeId>
{
    private readonly int _raw;

    private FloatNodeId(int raw) => _raw = raw;

    /// <summary>Gets whether this handle points to a valid node.</summary>
    public bool HasValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _raw > 0;
    }

    internal int Index
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _raw - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static FloatNodeId FromIndex(int index) => new(index + 1);

    public override string ToString() => HasValue ? $"FloatNodeId({Index})" : "FloatNodeId(None)";
    private string DebugDisplay => HasValue ? $"Float[{Index}]" : "(none)";
}

/// <summary>
/// Dense flat store for all scalar AST node data. Scalar node properties on composite AST nodes
/// (Job, Step, Event, etc.) are replaced by lightweight handle structs that index into this arena.
/// Supports ThreadStatic pooling via <see cref="Rent"/>/<see cref="Dispose"/> to reuse backing arrays
/// across parse calls and eliminate repeated array allocations.
/// </summary>
[DebuggerDisplay("AstArena: {_stringCount} strings, {_boolCount} bools, {_intCount} ints, {_floatCount} floats")]
internal sealed class AstArena : IDisposable
{
    [ThreadStatic] private static AstArena? cached;

    private byte[] _source;

    private StringNodeData[] _strings;
    private int _stringCount;

    private BoolNodeData[] _bools;
    private int _boolCount;

    private IntNodeData[] _ints;
    private int _intCount;

    private FloatNodeData[] _floats;
    private int _floatCount;

    // Object pools for composite AST nodes (reused across parse calls)
    private Job[] _jobs;
    private int _jobCount;

    private Step[] _steps;
    private int _stepCount;

    private ExecRun[] _execRuns;
    private int _execRunCount;

    private ExecAction[] _execActions;
    private int _execActionCount;

    private ExecWait[] _execWaits;
    private int _execWaitCount;

    private ExecWaitAll[] _execWaitAlls;
    private int _execWaitAllCount;

    private ExecCancel[] _execCancels;
    private int _execCancelCount;

    private ExecParallel[] _execParallels;
    private int _execParallelCount;

    // Object pools for section AST nodes (Permissions, Env, Runner, ...).
    // Same reuse semantics as the Job/Step pools above, via AstNodePool<T>.
    private AstNodePool<Permissions> _permissionsPool = new(DefaultSectionNodeCapacity, static n => n.Reset());
    private AstNodePool<Env> _envPool = new(DefaultEnvCapacity, static n => n.Reset());
    private AstNodePool<Defaults> _defaultsPool = new(DefaultSectionNodeCapacity, static n => n.Reset());
    private AstNodePool<DefaultsRun> _defaultsRunPool = new(DefaultSectionNodeCapacity, static n => n.Reset());
    private AstNodePool<Concurrency> _concurrencyPool = new(DefaultSectionNodeCapacity, static n => n.Reset());
    private AstNodePool<Ast.Environment> _environmentPool = new(DefaultSectionNodeCapacity, static n => n.Reset());
    private AstNodePool<Runner> _runnerPool = new(DefaultRunnerCapacity, static n => n.Reset());
    private AstNodePool<Strategy> _strategyPool = new(DefaultStrategyCapacity, static n => n.Reset());
    private AstNodePool<Matrix> _matrixPool = new(DefaultStrategyCapacity, static n => n.Reset());
    private AstNodePool<MatrixRow> _matrixRowPool = new(DefaultMatrixRowCapacity, static n => n.Reset());
    private AstNodePool<MatrixCombinations> _matrixCombinationsPool = new(DefaultStrategyCapacity, static n => n.Reset());
    private AstNodePool<RawYamlString> _rawYamlStringPool = new(DefaultRawYamlValueCapacity, static n => n.Reset());
    private AstNodePool<RawYamlArray> _rawYamlArrayPool = new(DefaultSectionNodeCapacity, static n => n.Reset());
    private AstNodePool<RawYamlObject> _rawYamlObjectPool = new(DefaultSectionNodeCapacity, static n => n.Reset());
    private AstNodePool<Container> _containerPool = new(DefaultSectionNodeCapacity, static n => n.Reset());
    private AstNodePool<Services> _servicesPool = new(DefaultSectionNodeCapacity, static n => n.Reset());
    private AstNodePool<Service> _servicePool = new(DefaultSectionNodeCapacity, static n => n.Reset());
    private AstNodePool<Credentials> _credentialsPool = new(DefaultSectionNodeCapacity, static n => n.Reset());
    private AstNodePool<WorkflowCall> _workflowCallPool = new(DefaultSectionNodeCapacity, static n => n.Reset());
    private AstNodePool<Snapshot> _snapshotPool = new(DefaultSectionNodeCapacity, static n => n.Reset());

    // D-1: Pooled diagnostics buffer registered by ParseClassified/ParseIncremental.
    // Returned to ArrayPool<Diagnostic>.Shared on Dispose.
    private Diagnostic[]? _diagnosticsBuffer;

    // D-2: Pooled lint diagnostics buffer registered by LintEngine.
    // Returned to ArrayPool<Diagnostic>.Shared on Dispose.
    private Diagnostic[]? _lintDiagnosticsBuffer;

    // D-4: Pooled SliceMap Entry[] arrays registered during parsing.
    // Each entry stores the array reference + a cached return delegate.
    // Returned to the appropriate ArrayPool<T>.Shared on Dispose/Reset.
    private (Array Buffer, Action<Array> Return)[] _sliceMapBuffers = new (Array, Action<Array>)[32];
    private int _sliceMapBufferCount;

    internal AstArena(byte[] source, int stringCapacity = 64, int boolCapacity = 8, int intCapacity = 4, int floatCapacity = 4)
    {
        _source = source;
        _strings = ArrayPool<StringNodeData>.Shared.Rent(stringCapacity);
        _bools = ArrayPool<BoolNodeData>.Shared.Rent(boolCapacity);
        _ints = ArrayPool<IntNodeData>.Shared.Rent(intCapacity);
        _floats = ArrayPool<FloatNodeData>.Shared.Rent(floatCapacity);
        _jobs = new Job[DefaultJobCapacity];
        _steps = new Step[DefaultStepCapacity];
        _execRuns = new ExecRun[DefaultExecRunCapacity];
        _execActions = new ExecAction[DefaultExecActionCapacity];
        _execWaits = new ExecWait[DefaultExecWaitCapacity];
        _execWaitAlls = new ExecWaitAll[DefaultExecWaitAllCapacity];
        _execCancels = new ExecCancel[DefaultExecCancelCapacity];
        _execParallels = new ExecParallel[DefaultExecParallelCapacity];
    }

    /// <summary>
    /// Rents an arena from the ThreadStatic cache or creates a new one.
    /// The returned arena must be disposed after use to return it to the cache.
    /// </summary>
    public static AstArena Rent(byte[] source)
    {
        var arena = cached;
        if (arena is not null)
        {
            cached = null;
            arena.ResetForSource(source);
            return arena;
        }

        return CreateNew(source);
    }

    /// <summary>
    /// Registers a pooled diagnostics array with this arena. The array will be returned
    /// to <see cref="ArrayPool{T}.Shared"/> when this arena is disposed.
    /// </summary>
    internal void RegisterDiagnosticsBuffer(Diagnostic[] buffer) => _diagnosticsBuffer = buffer;

    /// <summary>
    /// Registers a pooled SliceMap Entry[] array with this arena. The array will be returned
    /// to <see cref="ArrayPool{T}.Shared"/> when this arena is disposed or reset.
    /// Uses a static cached delegate per type T to avoid per-call allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RegisterSliceMapBuffer<T>(T[] array)
    {
        if (_sliceMapBufferCount == _sliceMapBuffers.Length)
        {
            Array.Resize(ref _sliceMapBuffers, _sliceMapBuffers.Length * 2);
        }

        _sliceMapBuffers[_sliceMapBufferCount++] = (array, PoolReturnCache<T>.Instance);
    }

    /// <summary>
    /// Registers a pooled lint diagnostics array with this arena. The array will be returned
    /// to <see cref="ArrayPool{T}.Shared"/> when this arena is disposed.
    /// If a previous lint buffer was registered, it is returned to the pool immediately
    /// (supports repeated lint calls on the same arena, e.g. IncrementalParseContext).
    /// </summary>
    internal void RegisterLintDiagnosticsBuffer(Diagnostic[] buffer)
    {
        if (_lintDiagnosticsBuffer is not null)
        {
            ArrayPool<Diagnostic>.Shared.Return(_lintDiagnosticsBuffer, clearArray: true);
        }

        _lintDiagnosticsBuffer = buffer;
    }

    /// <summary>
    /// Returns the lint diagnostics buffer to the pool without disposing the arena.
    /// Call this before retaining an arena whose lint data has already been consumed.
    /// </summary>
    internal void ReleaseLintDiagnosticsBuffer()
    {
        if (_lintDiagnosticsBuffer is not null)
        {
            ArrayPool<Diagnostic>.Shared.Return(_lintDiagnosticsBuffer, clearArray: true);
            _lintDiagnosticsBuffer = null;
        }
    }

    /// <summary>
    /// Returns the parse diagnostics buffer to the pool without disposing the arena.
    /// Call this before retaining an arena whose parse diagnostics have already been consumed.
    /// </summary>
    internal void ReleaseDiagnosticsBuffer()
    {
        if (_diagnosticsBuffer is not null)
        {
            ArrayPool<Diagnostic>.Shared.Return(_diagnosticsBuffer, clearArray: true);
            _diagnosticsBuffer = null;
        }
    }

    /// <summary>
    /// Returns the arena to the ThreadStatic cache for reuse.
    /// After disposal, handles obtained from this arena must not be resolved.
    /// Backing arrays that have grown beyond their default capacity are returned to
    /// ArrayPool and replaced with default-sized pool arrays, preventing the ThreadStatic
    /// cache from permanently retaining high-water-mark allocations (critical for
    /// memory-constrained environments like WASM).
    /// </summary>
    public void Dispose()
    {
        // Return pooled diagnostics buffer if registered
        if (_diagnosticsBuffer is not null)
        {
            ArrayPool<Diagnostic>.Shared.Return(_diagnosticsBuffer, clearArray: true);
            _diagnosticsBuffer = null;
        }

        // Return pooled lint diagnostics buffer if registered
        if (_lintDiagnosticsBuffer is not null)
        {
            ArrayPool<Diagnostic>.Shared.Return(_lintDiagnosticsBuffer, clearArray: true);
            _lintDiagnosticsBuffer = null;
        }

        // D-4: Return all registered SliceMap Entry[] arrays to their respective pools
        for (var i = 0; i < _sliceMapBufferCount; i++)
        {
            _sliceMapBuffers[i].Return(_sliceMapBuffers[i].Buffer);
            _sliceMapBuffers[i] = default;
        }
        _sliceMapBufferCount = 0;

        // Reset pooled objects to release references to prior AST graphs (Steps lists, SliceMaps, etc.)
        // This prevents memory retention across parse calls, which is critical in WASM.
        for (var i = 0; i < _jobCount; i++) _jobs[i]?.Reset();
        for (var i = 0; i < _stepCount; i++) _steps[i]?.Reset();
        for (var i = 0; i < _execRunCount; i++) _execRuns[i]?.Reset();
        for (var i = 0; i < _execActionCount; i++) _execActions[i]?.Reset();
        for (var i = 0; i < _execWaitCount; i++) _execWaits[i]?.Reset();
        for (var i = 0; i < _execWaitAllCount; i++) _execWaitAlls[i]?.Reset();
        for (var i = 0; i < _execCancelCount; i++) _execCancels[i]?.Reset();
        for (var i = 0; i < _execParallelCount; i++) _execParallels[i]?.Reset();

        // Section node pools: reset allocated nodes and cap retained capacity
        _permissionsPool.Release(DefaultSectionNodeCapacity);
        _envPool.Release(DefaultEnvCapacity);
        _defaultsPool.Release(DefaultSectionNodeCapacity);
        _defaultsRunPool.Release(DefaultSectionNodeCapacity);
        _concurrencyPool.Release(DefaultSectionNodeCapacity);
        _environmentPool.Release(DefaultSectionNodeCapacity);
        _runnerPool.Release(DefaultRunnerCapacity);
        _strategyPool.Release(DefaultStrategyCapacity);
        _matrixPool.Release(DefaultStrategyCapacity);
        _matrixRowPool.Release(DefaultMatrixRowCapacity);
        _matrixCombinationsPool.Release(DefaultStrategyCapacity);
        _rawYamlStringPool.Release(DefaultRawYamlValueCapacity);
        _rawYamlArrayPool.Release(DefaultSectionNodeCapacity);
        _rawYamlObjectPool.Release(DefaultSectionNodeCapacity);
        _containerPool.Release(DefaultSectionNodeCapacity);
        _servicesPool.Release(DefaultSectionNodeCapacity);
        _servicePool.Release(DefaultSectionNodeCapacity);
        _credentialsPool.Release(DefaultSectionNodeCapacity);
        _workflowCallPool.Release(DefaultSectionNodeCapacity);
        _snapshotPool.Release(DefaultSectionNodeCapacity);

        _stringCount = 0;
        _boolCount = 0;
        _intCount = 0;
        _floatCount = 0;
        _jobCount = 0;
        _stepCount = 0;
        _execRunCount = 0;
        _execActionCount = 0;
        _execWaitCount = 0;
        _execWaitAllCount = 0;
        _execCancelCount = 0;
        _execParallelCount = 0;
        _source = [];

        if (cached is null)
        {
            // Cap backing arrays to default sizes to prevent unbounded growth.
            // Grow() doubles arrays but Dispose() must shrink them back so the ThreadStatic
            // cache doesn't permanently retain peak-sized arrays.
            // Uses ArrayPool.Rent for replacements (may return slightly oversized arrays but
            // always from the smallest matching bucket, far below peak). All arrays stay
            // pool-rented so EnsureMinCapacity/Return in subsequent Rent() calls are safe.
            ShrinkIfOversized(ref _strings, DefaultStringCapacity);
            ShrinkIfOversized(ref _bools, DefaultBoolCapacity);
            ShrinkIfOversized(ref _ints, DefaultIntCapacity);
            ShrinkIfOversized(ref _floats, DefaultFloatCapacity);
            ShrinkObjectPoolIfOversized(ref _jobs, DefaultJobCapacity);
            ShrinkObjectPoolIfOversized(ref _steps, DefaultStepCapacity);
            ShrinkObjectPoolIfOversized(ref _execRuns, DefaultExecRunCapacity);
            ShrinkObjectPoolIfOversized(ref _execActions, DefaultExecActionCapacity);
            ShrinkObjectPoolIfOversized(ref _execWaits, DefaultExecWaitCapacity);
            ShrinkObjectPoolIfOversized(ref _execWaitAlls, DefaultExecWaitAllCapacity);
            ShrinkObjectPoolIfOversized(ref _execCancels, DefaultExecCancelCapacity);
            ShrinkObjectPoolIfOversized(ref _execParallels, DefaultExecParallelCapacity);
            cached = this;
        }
        else
        {
            // Cache is already occupied — return all pool-rented arrays and discard this arena.
            ArrayPool<StringNodeData>.Shared.Return(_strings);
            ArrayPool<BoolNodeData>.Shared.Return(_bools);
            ArrayPool<IntNodeData>.Shared.Return(_ints);
            ArrayPool<FloatNodeData>.Shared.Return(_floats);
            _strings = null!;
            _bools = null!;
            _ints = null!;
            _floats = null!;
            _jobs = null!;
            _steps = null!;
            _execRuns = null!;
            _execActions = null!;
            _execWaits = null!;
            _execWaitAlls = null!;
            _execCancels = null!;
            _execParallels = null!;
        }
    }

    /// <summary>Default capacities used for size cap in Dispose.</summary>
    private const int DefaultStringCapacity = 256;
    private const int DefaultBoolCapacity = 32;
    private const int DefaultIntCapacity = 16;
    private const int DefaultFloatCapacity = 8;

    // Object pool default capacities (retain up to these sizes across parses)
    private const int DefaultJobCapacity = 24;
    private const int DefaultStepCapacity = 128;
    private const int DefaultExecRunCapacity = 128;
    private const int DefaultExecActionCapacity = 128;
    private const int DefaultExecWaitCapacity = 32;
    private const int DefaultExecWaitAllCapacity = 16;
    private const int DefaultExecCancelCapacity = 16;
    private const int DefaultExecParallelCapacity = 32;

    // Section node pool default capacities. Env appears per step + per job + workflow-level,
    // Runner/Strategy/Matrix/MatrixRow per job, the rest are occasional per-job sections.
    private const int DefaultSectionNodeCapacity = 8;
    private const int DefaultEnvCapacity = 64;
    private const int DefaultRunnerCapacity = 24;
    private const int DefaultStrategyCapacity = 16;
    private const int DefaultMatrixRowCapacity = 32;
    private const int DefaultRawYamlValueCapacity = 64;

    private static void ShrinkIfOversized<T>(ref T[] array, int maxRetainedCapacity)
    {
        if (array.Length > maxRetainedCapacity)
        {
            ArrayPool<T>.Shared.Return(array);
            array = ArrayPool<T>.Shared.Rent(maxRetainedCapacity);
        }
    }

    private static AstArena CreateNew(byte[] source)
    {
        var stringCap = Math.Max(64, source.Length / 20);
        var boolCap = Math.Max(8, source.Length / 200);
        var intCap = Math.Max(4, source.Length / 500);
        return new AstArena(source, stringCap, boolCap, intCap, 4);
    }

    private void ResetForSource(byte[] source)
    {
        _source = source;
        _stringCount = 0;
        _boolCount = 0;
        _intCount = 0;
        _floatCount = 0;
        _jobCount = 0;
        _stepCount = 0;
        _execRunCount = 0;
        _execActionCount = 0;
        _execWaitCount = 0;
        _execWaitAllCount = 0;
        _execCancelCount = 0;
        _execParallelCount = 0;
        EnsureMinCapacity(ref _strings, Math.Max(64, source.Length / 20));
        EnsureMinCapacity(ref _bools, Math.Max(8, source.Length / 200));
        EnsureMinCapacity(ref _ints, Math.Max(4, source.Length / 500));
    }

    /// <summary>Gets the raw UTF-8 source bytes that this arena indexes into.</summary>
    public byte[] Source => _source;

    // String allocation

    /// <summary>Allocates a string node with no embedded expression.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringNodeId AddString(Utf8Slice value, bool quoted, TextRange range)
    {
        if (_stringCount == _strings.Length) Grow(ref _strings);
        _strings[_stringCount] = new StringNodeData(value, quoted, default, range);
        return StringNodeId.FromIndex(_stringCount++);
    }

    /// <summary>Allocates a string node with an embedded expression (e.g. <c>${{ ... }}</c>).</summary>
    public StringNodeId AddString(Utf8Slice value, bool quoted, StringNodeId expression, TextRange range)
    {
        if (_stringCount == _strings.Length) Grow(ref _strings);
        _strings[_stringCount] = new StringNodeData(value, quoted, expression, range);
        return StringNodeId.FromIndex(_stringCount++);
    }

    // Bool allocation

    /// <summary>Allocates a bool node with no embedded expression.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BoolNodeId AddBool(bool value, TextRange range)
    {
        if (_boolCount == _bools.Length) Grow(ref _bools);
        _bools[_boolCount] = new BoolNodeData(value, default, range);
        return BoolNodeId.FromIndex(_boolCount++);
    }

    /// <summary>Allocates a bool node with an embedded expression.</summary>
    public BoolNodeId AddBool(bool value, StringNodeId expression, TextRange range)
    {
        if (_boolCount == _bools.Length) Grow(ref _bools);
        _bools[_boolCount] = new BoolNodeData(value, expression, range);
        return BoolNodeId.FromIndex(_boolCount++);
    }

    // Int allocation

    /// <summary>Allocates an integer node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IntNodeId AddInt(long value, TextRange range)
    {
        if (_intCount == _ints.Length) Grow(ref _ints);
        _ints[_intCount] = new IntNodeData(value, default, range);
        return IntNodeId.FromIndex(_intCount++);
    }

    /// <summary>Allocates an integer node with an embedded expression.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IntNodeId AddInt(long value, StringNodeId expression, TextRange range)
    {
        if (_intCount == _ints.Length) Grow(ref _ints);
        _ints[_intCount] = new IntNodeData(value, expression, range);
        return IntNodeId.FromIndex(_intCount++);
    }

    // Float allocation

    /// <summary>Allocates a float node with no embedded expression.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FloatNodeId AddFloat(double value, TextRange range)
    {
        if (_floatCount == _floats.Length) Grow(ref _floats);
        _floats[_floatCount] = new FloatNodeData(value, default, range);
        return FloatNodeId.FromIndex(_floatCount++);
    }

    /// <summary>Allocates a float node with an embedded expression.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FloatNodeId AddFloat(double value, StringNodeId expression, TextRange range)
    {
        if (_floatCount == _floats.Length) Grow(ref _floats);
        _floats[_floatCount] = new FloatNodeData(value, expression, range);
        return FloatNodeId.FromIndex(_floatCount++);
    }

    // String read

    /// <summary>Resolves a string node's UTF-8 value bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetStringValue(StringNodeId id)
    {
        if (!id.HasValue) return ReadOnlySpan<byte>.Empty;
        return _strings[id.Index].Value.AsSpan(_source);
    }

    /// <summary>Resolves a string node's value as a <see cref="Utf8Slice"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Utf8Slice GetStringSlice(StringNodeId id)
    {
        if (!id.HasValue) return default;
        return _strings[id.Index].Value;
    }

    /// <summary>Returns whether the string node was YAML-quoted.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetStringQuoted(StringNodeId id)
    {
        if (!id.HasValue) return false;
        return _strings[id.Index].Quoted;
    }

    /// <summary>Returns the source location of a string node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TextRange GetStringRange(StringNodeId id)
    {
        if (!id.HasValue) return default;
        return _strings[id.Index].Range;
    }

    /// <summary>Returns the embedded expression handle of a string node, or <c>default</c> if none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringNodeId GetStringExpression(StringNodeId id)
    {
        if (!id.HasValue) return default;
        return _strings[id.Index].Expression;
    }

    // Bool read

    /// <summary>Resolves a bool node's value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetBoolValue(BoolNodeId id)
    {
        if (!id.HasValue) return false;
        return _bools[id.Index].Value;
    }

    /// <summary>Returns the source location of a bool node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TextRange GetBoolRange(BoolNodeId id)
    {
        if (!id.HasValue) return default;
        return _bools[id.Index].Range;
    }

    /// <summary>Returns the embedded expression handle of a bool node, or <c>default</c> if none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringNodeId GetBoolExpression(BoolNodeId id)
    {
        if (!id.HasValue) return default;
        return _bools[id.Index].Expression;
    }

    // Int read

    /// <summary>Resolves an integer node's value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetIntValue(IntNodeId id)
    {
        if (!id.HasValue) return 0;
        return _ints[id.Index].Value;
    }

    /// <summary>Returns the source location of an integer node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TextRange GetIntRange(IntNodeId id)
    {
        if (!id.HasValue) return default;
        return _ints[id.Index].Range;
    }

    /// <summary>Returns the embedded expression handle of an integer node, or <c>default</c> if none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringNodeId GetIntExpression(IntNodeId id)
    {
        if (!id.HasValue) return default;
        return _ints[id.Index].Expression;
    }

    // Float read

    /// <summary>Resolves a float node's value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double GetFloatValue(FloatNodeId id)
    {
        if (!id.HasValue) return 0;
        return _floats[id.Index].Value;
    }

    /// <summary>Returns the source location of a float node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TextRange GetFloatRange(FloatNodeId id)
    {
        if (!id.HasValue) return default;
        return _floats[id.Index].Range;
    }

    /// <summary>Returns the embedded expression handle of a float node, or <c>default</c> if none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringNodeId GetFloatExpression(FloatNodeId id)
    {
        if (!id.HasValue) return default;
        return _floats[id.Index].Expression;
    }

    // Private

    private static void Grow<T>(ref T[] array)
    {
        var old = array;
        array = ArrayPool<T>.Shared.Rent(old.Length * 2);
        Array.Copy(old, array, old.Length);
        ArrayPool<T>.Shared.Return(old);
    }

    private static void EnsureMinCapacity<T>(ref T[] array, int minCapacity)
    {
        if (array.Length < minCapacity)
        {
            ArrayPool<T>.Shared.Return(array);
            array = ArrayPool<T>.Shared.Rent(minCapacity);
        }
    }

    private static void ShrinkObjectPoolIfOversized<T>(ref T[] array, int maxRetainedCapacity) where T : class
    {
        if (array.Length > maxRetainedCapacity)
        {
            var newArr = new T[maxRetainedCapacity];
            Array.Copy(array, newArr, maxRetainedCapacity);
            array = newArr;
        }
    }

    private static void GrowObjectPool<T>(ref T[] array) where T : class
    {
        var newArr = new T[array.Length * 2];
        Array.Copy(array, newArr, array.Length);
        array = newArr;
    }

    // Object pool allocation methods

    /// <summary>Returns a pooled or new Job instance with all fields reset to default.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Job AllocJob()
    {
        if (_jobCount == _jobs.Length) GrowObjectPool(ref _jobs);
        var obj = _jobs[_jobCount];
        if (obj is null)
        {
            obj = new Job();
            _jobs[_jobCount] = obj;
        }
        obj.Reset();
        _jobCount++;
        return obj;
    }

    /// <summary>Returns a pooled or new Step instance with all fields reset to default.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Step AllocStep()
    {
        if (_stepCount == _steps.Length) GrowObjectPool(ref _steps);
        var obj = _steps[_stepCount];
        if (obj is null)
        {
            obj = new Step();
            _steps[_stepCount] = obj;
        }
        obj.Reset();
        _stepCount++;
        return obj;
    }

    /// <summary>Returns a pooled or new ExecRun instance with all fields reset to default.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExecRun AllocExecRun()
    {
        if (_execRunCount == _execRuns.Length) GrowObjectPool(ref _execRuns);
        var obj = _execRuns[_execRunCount];
        if (obj is null)
        {
            obj = new ExecRun();
            _execRuns[_execRunCount] = obj;
        }
        obj.Reset();
        _execRunCount++;
        return obj;
    }

    /// <summary>Returns a pooled or new ExecAction instance with all fields reset to default.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExecAction AllocExecAction()
    {
        if (_execActionCount == _execActions.Length) GrowObjectPool(ref _execActions);
        var obj = _execActions[_execActionCount];
        if (obj is null)
        {
            obj = new ExecAction();
            _execActions[_execActionCount] = obj;
        }
        obj.Reset();
        _execActionCount++;
        return obj;
    }

    /// <summary>Returns a pooled or new ExecWait instance with all fields reset to default.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExecWait AllocExecWait()
    {
        if (_execWaitCount == _execWaits.Length) GrowObjectPool(ref _execWaits);
        var obj = _execWaits[_execWaitCount];
        if (obj is null)
        {
            obj = new ExecWait();
            _execWaits[_execWaitCount] = obj;
        }
        obj.Reset();
        _execWaitCount++;
        return obj;
    }

    /// <summary>Returns a pooled or new ExecWaitAll instance with all fields reset to default.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExecWaitAll AllocExecWaitAll()
    {
        if (_execWaitAllCount == _execWaitAlls.Length) GrowObjectPool(ref _execWaitAlls);
        var obj = _execWaitAlls[_execWaitAllCount];
        if (obj is null)
        {
            obj = new ExecWaitAll();
            _execWaitAlls[_execWaitAllCount] = obj;
        }
        obj.Reset();
        _execWaitAllCount++;
        return obj;
    }

    /// <summary>Returns a pooled or new ExecCancel instance with all fields reset to default.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExecCancel AllocExecCancel()
    {
        if (_execCancelCount == _execCancels.Length) GrowObjectPool(ref _execCancels);
        var obj = _execCancels[_execCancelCount];
        if (obj is null)
        {
            obj = new ExecCancel();
            _execCancels[_execCancelCount] = obj;
        }
        obj.Reset();
        _execCancelCount++;
        return obj;
    }

    /// <summary>Returns a pooled or new ExecParallel instance with all fields reset to default.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExecParallel AllocExecParallel()
    {
        if (_execParallelCount == _execParallels.Length) GrowObjectPool(ref _execParallels);
        var obj = _execParallels[_execParallelCount];
        if (obj is null)
        {
            obj = new ExecParallel();
            _execParallels[_execParallelCount] = obj;
        }
        obj.Reset();
        _execParallelCount++;
        return obj;
    }

    // Section node pool allocation methods (same reset-on-alloc semantics as Job/Step above)

    /// <summary>Returns a pooled or new <see cref="Permissions"/> with all fields reset to default.</summary>
    public Permissions AllocPermissions() => _permissionsPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="Env"/> with all fields reset to default.</summary>
    public Env AllocEnv() => _envPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="Defaults"/> with all fields reset to default.</summary>
    public Defaults AllocDefaults() => _defaultsPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="DefaultsRun"/> with all fields reset to default.</summary>
    public DefaultsRun AllocDefaultsRun() => _defaultsRunPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="Concurrency"/> with all fields reset to default.</summary>
    public Concurrency AllocConcurrency() => _concurrencyPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="Ast.Environment"/> with all fields reset to default.</summary>
    public Ast.Environment AllocEnvironment() => _environmentPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="Runner"/> with all fields reset to default.</summary>
    public Runner AllocRunner() => _runnerPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="Strategy"/> with all fields reset to default.</summary>
    public Strategy AllocStrategy() => _strategyPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="Matrix"/> with all fields reset to default.</summary>
    public Matrix AllocMatrix() => _matrixPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="MatrixRow"/> with all fields reset to default.</summary>
    public MatrixRow AllocMatrixRow() => _matrixRowPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="MatrixCombinations"/> with all fields reset to default.</summary>
    public MatrixCombinations AllocMatrixCombinations() => _matrixCombinationsPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="RawYamlString"/> with all fields reset to default.</summary>
    public RawYamlString AllocRawYamlString() => _rawYamlStringPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="RawYamlArray"/> with all fields reset to default.</summary>
    public RawYamlArray AllocRawYamlArray() => _rawYamlArrayPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="RawYamlObject"/> with all fields reset to default.</summary>
    public RawYamlObject AllocRawYamlObject() => _rawYamlObjectPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="Container"/> with all fields reset to default.</summary>
    public Container AllocContainer() => _containerPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="Services"/> with all fields reset to default.</summary>
    public Services AllocServices() => _servicesPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="Service"/> with all fields reset to default.</summary>
    public Service AllocService() => _servicePool.Alloc();

    /// <summary>Returns a pooled or new <see cref="Credentials"/> with all fields reset to default.</summary>
    public Credentials AllocCredentials() => _credentialsPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="WorkflowCall"/> with all fields reset to default.</summary>
    public WorkflowCall AllocWorkflowCall() => _workflowCallPool.Alloc();

    /// <summary>Returns a pooled or new <see cref="Snapshot"/> with all fields reset to default.</summary>
    public Snapshot AllocSnapshot() => _snapshotPool.Alloc();

    // Incremental parse support

    /// <summary>
    /// Copies node entries (strings, bools, ints, floats) from <paramref name="source"/> into this arena,
    /// limited to the specified counts. After this call, handles from the source arena in the imported
    /// range resolve correctly against this arena. New entries added after this call receive indices
    /// beyond the imported range.
    /// </summary>
    internal void BulkImportFrom(AstArena source, int stringLimit, int boolLimit, int intLimit, int floatLimit)
    {
        var sc = Math.Min(source._stringCount, stringLimit);
        if (sc > 0)
        {
            EnsureMinCapacity(ref _strings, sc);
            Array.Copy(source._strings, 0, _strings, 0, sc);
            _stringCount = sc;
        }

        var bc = Math.Min(source._boolCount, boolLimit);
        if (bc > 0)
        {
            EnsureMinCapacity(ref _bools, bc);
            Array.Copy(source._bools, 0, _bools, 0, bc);
            _boolCount = bc;
        }

        var ic = Math.Min(source._intCount, intLimit);
        if (ic > 0)
        {
            EnsureMinCapacity(ref _ints, ic);
            Array.Copy(source._ints, 0, _ints, 0, ic);
            _intCount = ic;
        }

        var fc = Math.Min(source._floatCount, floatLimit);
        if (fc > 0)
        {
            EnsureMinCapacity(ref _floats, fc);
            Array.Copy(source._floats, 0, _floats, 0, fc);
            _floatCount = fc;
        }
    }

    /// <summary>Gets the current number of string entries in the arena.</summary>
    internal int StringCount => _stringCount;

    /// <summary>Gets the current number of bool entries in the arena.</summary>
    internal int BoolCount => _boolCount;

    /// <summary>Gets the current number of int entries in the arena.</summary>
    internal int IntCount => _intCount;

    /// <summary>Gets the current number of float entries in the arena.</summary>
    internal int FloatCount => _floatCount;

    /// <summary>Gets the number of Job objects allocated from this arena's pool.</summary>
    internal int JobCount => _jobCount;

    // Debug helpers (§6.2 debugging experience)

    /// <summary>
    /// Returns a human-readable representation of the string value for a handle.
    /// Intended for debugger watch windows and diagnostic output.
    /// </summary>
    public string DebugGetStringText(StringNodeId id)
    {
        if (!id.HasValue) return "(none)";
        var span = _strings[id.Index].Value.AsSpan(_source);
        return span.Length == 0 ? "(empty)" : Encoding.UTF8.GetString(span);
    }

    /// <summary>
    /// Returns a diagnostic summary of arena utilization.
    /// </summary>
    public string DebugDump()
    {
        return $"AstArena: strings={_stringCount}/{_strings.Length}, bools={_boolCount}/{_bools.Length}, ints={_intCount}/{_ints.Length}, floats={_floatCount}/{_floats.Length}, source={_source.Length}B";
    }

    private struct StringNodeData(Utf8Slice value, bool quoted, StringNodeId expression, TextRange range)
    {
        public Utf8Slice Value = value;
        public bool Quoted = quoted;
        public StringNodeId Expression = expression;
        public TextRange Range = range;
    }

    private struct BoolNodeData(bool value, StringNodeId expression, TextRange range)
    {
        public bool Value = value;
        public StringNodeId Expression = expression;
        public TextRange Range = range;
    }

    private struct IntNodeData(long value, StringNodeId expression, TextRange range)
    {
        public long Value = value;
        public StringNodeId Expression = expression;
        public TextRange Range = range;
    }

    private struct FloatNodeData(double value, StringNodeId expression, TextRange range)
    {
        public double Value = value;
        public StringNodeId Expression = expression;
        public TextRange Range = range;
    }
}

/// <summary>
/// Caches a single <see cref="Action{Array}"/> delegate per type T that returns the array
/// to <see cref="ArrayPool{T}.Shared"/>. Uses <c>clearArray: true</c> when T contains
/// references to prevent retaining prior AST objects in the shared pool.
/// </summary>
internal static class PoolReturnCache<T>
{
    public static readonly Action<Array> Instance = static arr =>
        ArrayPool<T>.Shared.Return((T[])arr, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
}
