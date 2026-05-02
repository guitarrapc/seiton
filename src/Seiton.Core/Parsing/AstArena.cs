using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Seiton.Core.Parsing;

/// <summary>
/// Type-safe handle referencing a string scalar node stored in <see cref="AstArena"/>.
/// Default value (<c>default</c>) represents "no value" (equivalent to <c>null</c> on the old <c>StringNode?</c>).
/// </summary>
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
public sealed class AstArena : IDisposable
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

    internal AstArena(byte[] source, int stringCapacity = 64, int boolCapacity = 8, int intCapacity = 4, int floatCapacity = 4)
    {
        _source = source;
        _strings = ArrayPool<StringNodeData>.Shared.Rent(stringCapacity);
        _bools = ArrayPool<BoolNodeData>.Shared.Rent(boolCapacity);
        _ints = ArrayPool<IntNodeData>.Shared.Rent(intCapacity);
        _floats = ArrayPool<FloatNodeData>.Shared.Rent(floatCapacity);
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
    /// Returns the arena to the ThreadStatic cache for reuse.
    /// After disposal, handles obtained from this arena must not be resolved.
    /// Backing arrays that have grown beyond their default capacity are replaced with
    /// default-sized arrays to prevent the ThreadStatic cache from permanently retaining
    /// high-water-mark allocations (critical for memory-constrained environments like WASM).
    /// </summary>
    public void Dispose()
    {
        _stringCount = 0;
        _boolCount = 0;
        _intCount = 0;
        _floatCount = 0;
        _source = [];

        if (cached is null)
        {
            // Cap backing arrays to default sizes to prevent unbounded growth.
            // Grow() doubles arrays but Dispose() must shrink them back so the ThreadStatic
            // cache doesn't permanently retain peak-sized arrays.
            // Use exact-size allocations (not ArrayPool.Rent) because Rent() may return
            // oversized arrays that would defeat the cap.
            ShrinkIfOversized(ref _strings, DefaultStringCapacity);
            ShrinkIfOversized(ref _bools, DefaultBoolCapacity);
            ShrinkIfOversized(ref _ints, DefaultIntCapacity);
            ShrinkIfOversized(ref _floats, DefaultFloatCapacity);
            cached = this;
        }
        else
        {
            // Cache is already occupied — return all pool-rented arrays and discard this arena.
            ReturnToPool(_strings);
            ReturnToPool(_bools);
            ReturnToPool(_ints);
            ReturnToPool(_floats);
            _strings = null!;
            _bools = null!;
            _ints = null!;
            _floats = null!;
        }
    }

    /// <summary>Default capacities used for size cap in Dispose.</summary>
    private const int DefaultStringCapacity = 256;
    private const int DefaultBoolCapacity = 32;
    private const int DefaultIntCapacity = 16;
    private const int DefaultFloatCapacity = 8;

    private static void ShrinkIfOversized<T>(ref T[] array, int maxRetainedCapacity)
    {
        if (array.Length > maxRetainedCapacity)
        {
            ArrayPool<T>.Shared.Return(array);
            // Allocate an exact-size array (not from the pool) so the ThreadStatic
            // cache never retains anything larger than the cap. ArrayPool.Rent() is
            // allowed to return oversized arrays, which would defeat the cap.
            // When the arena is next Rent()'d and Grow() fires, it will switch back
            // to pool-rented arrays.
            array = new T[maxRetainedCapacity];
        }
    }

    private static void ReturnToPool<T>(T[] array)
    {
        ArrayPool<T>.Shared.Return(array);
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
