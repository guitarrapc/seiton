using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing;

/// <summary>
/// Type-safe handle referencing a string scalar node stored in <see cref="AstArena"/>.
/// Default value (<c>default</c>) represents "no value" (equivalent to <c>null</c> on the old <c>StringNode?</c>).
/// </summary>
public readonly struct StringNodeId : IEquatable<StringNodeId>
{
    // 0 = None (default), positive = valid (actual index = _raw - 1)
    private readonly int _raw;

    private StringNodeId(int raw) => _raw = raw;

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

    public bool Equals(StringNodeId other) => _raw == other._raw;
    public override bool Equals(object? obj) => obj is StringNodeId other && Equals(other);
    public override int GetHashCode() => _raw;
    public static bool operator ==(StringNodeId left, StringNodeId right) => left._raw == right._raw;
    public static bool operator !=(StringNodeId left, StringNodeId right) => left._raw != right._raw;
    public override string ToString() => HasValue ? $"StringNodeId({Index})" : "StringNodeId(None)";
}

/// <summary>
/// Type-safe handle referencing a bool scalar node stored in <see cref="AstArena"/>.
/// </summary>
public readonly struct BoolNodeId : IEquatable<BoolNodeId>
{
    private readonly int _raw;

    private BoolNodeId(int raw) => _raw = raw;

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

    public bool Equals(BoolNodeId other) => _raw == other._raw;
    public override bool Equals(object? obj) => obj is BoolNodeId other && Equals(other);
    public override int GetHashCode() => _raw;
    public static bool operator ==(BoolNodeId left, BoolNodeId right) => left._raw == right._raw;
    public static bool operator !=(BoolNodeId left, BoolNodeId right) => left._raw != right._raw;
    public override string ToString() => HasValue ? $"BoolNodeId({Index})" : "BoolNodeId(None)";
}

/// <summary>
/// Type-safe handle referencing an int scalar node stored in <see cref="AstArena"/>.
/// </summary>
public readonly struct IntNodeId : IEquatable<IntNodeId>
{
    private readonly int _raw;

    private IntNodeId(int raw) => _raw = raw;

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

    public bool Equals(IntNodeId other) => _raw == other._raw;
    public override bool Equals(object? obj) => obj is IntNodeId other && Equals(other);
    public override int GetHashCode() => _raw;
    public static bool operator ==(IntNodeId left, IntNodeId right) => left._raw == right._raw;
    public static bool operator !=(IntNodeId left, IntNodeId right) => left._raw != right._raw;
    public override string ToString() => HasValue ? $"IntNodeId({Index})" : "IntNodeId(None)";
}

/// <summary>
/// Type-safe handle referencing a float scalar node stored in <see cref="AstArena"/>.
/// </summary>
public readonly struct FloatNodeId : IEquatable<FloatNodeId>
{
    private readonly int _raw;

    private FloatNodeId(int raw) => _raw = raw;

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

    public bool Equals(FloatNodeId other) => _raw == other._raw;
    public override bool Equals(object? obj) => obj is FloatNodeId other && Equals(other);
    public override int GetHashCode() => _raw;
    public static bool operator ==(FloatNodeId left, FloatNodeId right) => left._raw == right._raw;
    public static bool operator !=(FloatNodeId left, FloatNodeId right) => left._raw != right._raw;
    public override string ToString() => HasValue ? $"FloatNodeId({Index})" : "FloatNodeId(None)";
}

/// <summary>
/// Dense flat store for all scalar AST node data. Scalar node properties on composite AST nodes
/// (Job, Step, Event, etc.) are replaced by lightweight handle structs that index into this arena.
/// </summary>
public sealed class AstArena
{
    private readonly byte[] _source;

    private StringNodeData[] _strings;
    private int _stringCount;

    private BoolNodeData[] _bools;
    private int _boolCount;

    private IntNodeData[] _ints;
    private int _intCount;

    private FloatNodeData[] _floats;
    private int _floatCount;

    public AstArena(byte[] source, int stringCapacity = 64, int boolCapacity = 8, int intCapacity = 4, int floatCapacity = 4)
    {
        _source = source;
        _strings = new StringNodeData[stringCapacity];
        _bools = new BoolNodeData[boolCapacity];
        _ints = new IntNodeData[intCapacity];
        _floats = new FloatNodeData[floatCapacity];
    }

    /// <summary>
    /// Creates an arena with initial capacities estimated from the YAML source size.
    /// </summary>
    public static AstArena CreateForSource(byte[] source)
    {
        // Heuristic: ~1 string node per 20 source bytes, bools ~1/200, ints/floats rare.
        var stringCap = Math.Max(64, source.Length / 20);
        var boolCap = Math.Max(8, source.Length / 200);
        var intCap = Math.Max(4, source.Length / 500);
        return new AstArena(source, stringCap, boolCap, intCap);
    }

    public byte[] Source => _source;

    // ---- String allocation ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringNodeId AddString(Utf8Slice value, bool quoted, TextRange range)
    {
        if (_stringCount == _strings.Length) Grow(ref _strings);
        _strings[_stringCount] = new StringNodeData(value, quoted, default, range);
        return StringNodeId.FromIndex(_stringCount++);
    }

    public StringNodeId AddString(Utf8Slice value, bool quoted, StringNodeId expression, TextRange range)
    {
        if (_stringCount == _strings.Length) Grow(ref _strings);
        _strings[_stringCount] = new StringNodeData(value, quoted, expression, range);
        return StringNodeId.FromIndex(_stringCount++);
    }

    // ---- Bool allocation ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BoolNodeId AddBool(bool value, TextRange range)
    {
        if (_boolCount == _bools.Length) Grow(ref _bools);
        _bools[_boolCount] = new BoolNodeData(value, default, range);
        return BoolNodeId.FromIndex(_boolCount++);
    }

    public BoolNodeId AddBool(bool value, StringNodeId expression, TextRange range)
    {
        if (_boolCount == _bools.Length) Grow(ref _bools);
        _bools[_boolCount] = new BoolNodeData(value, expression, range);
        return BoolNodeId.FromIndex(_boolCount++);
    }

    // ---- Int allocation ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IntNodeId AddInt(long value, TextRange range)
    {
        if (_intCount == _ints.Length) Grow(ref _ints);
        _ints[_intCount] = new IntNodeData(value, default, range);
        return IntNodeId.FromIndex(_intCount++);
    }

    // ---- Float allocation ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FloatNodeId AddFloat(double value, TextRange range)
    {
        if (_floatCount == _floats.Length) Grow(ref _floats);
        _floats[_floatCount] = new FloatNodeData(value, default, range);
        return FloatNodeId.FromIndex(_floatCount++);
    }

    // ---- String read ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetStringValue(StringNodeId id)
    {
        if (!id.HasValue) return ReadOnlySpan<byte>.Empty;
        return _strings[id.Index].Value.AsSpan(_source);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Utf8Slice GetStringSlice(StringNodeId id)
    {
        if (!id.HasValue) return default;
        return _strings[id.Index].Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetStringQuoted(StringNodeId id)
    {
        if (!id.HasValue) return false;
        return _strings[id.Index].Quoted;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TextRange GetStringRange(StringNodeId id)
    {
        if (!id.HasValue) return default;
        return _strings[id.Index].Range;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringNodeId GetStringExpression(StringNodeId id)
    {
        if (!id.HasValue) return default;
        return _strings[id.Index].Expression;
    }

    // ---- Bool read ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetBoolValue(BoolNodeId id)
    {
        if (!id.HasValue) return false;
        return _bools[id.Index].Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TextRange GetBoolRange(BoolNodeId id)
    {
        if (!id.HasValue) return default;
        return _bools[id.Index].Range;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringNodeId GetBoolExpression(BoolNodeId id)
    {
        if (!id.HasValue) return default;
        return _bools[id.Index].Expression;
    }

    // ---- Int read ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetIntValue(IntNodeId id)
    {
        if (!id.HasValue) return 0;
        return _ints[id.Index].Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TextRange GetIntRange(IntNodeId id)
    {
        if (!id.HasValue) return default;
        return _ints[id.Index].Range;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringNodeId GetIntExpression(IntNodeId id)
    {
        if (!id.HasValue) return default;
        return _ints[id.Index].Expression;
    }

    // ---- Float read ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double GetFloatValue(FloatNodeId id)
    {
        if (!id.HasValue) return 0;
        return _floats[id.Index].Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TextRange GetFloatRange(FloatNodeId id)
    {
        if (!id.HasValue) return default;
        return _floats[id.Index].Range;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringNodeId GetFloatExpression(FloatNodeId id)
    {
        if (!id.HasValue) return default;
        return _floats[id.Index].Expression;
    }

    // ---- Private ----

    private static void Grow<T>(ref T[] array)
    {
        var newArray = new T[array.Length * 2];
        Array.Copy(array, newArray, array.Length);
        array = newArray;
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
