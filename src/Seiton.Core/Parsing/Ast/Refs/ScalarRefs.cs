using System.Text;

namespace Seiton.Core.Parsing.Ast;

// Readonly-struct facade layer over the AST.
//
// Refs wrap (arena, typed id) and expose ergonomic accessors so that rules and
// tests never touch AstArena row tables or raw handles directly. `default` refs
// represent absence (`HasValue == false`) and every accessor is default-safe:
// scalar accessors return empty values and child accessors return default refs,
// so chained reads like `job.Strategy.Matrix.Rows` never throw.
//
// In DEBUG builds every ref captures its arena's generation at construction and
// dereferencing after the arena is reset/disposed throws immediately.
// See `.github/docs/architecture_spec_ast.md`.

/// <summary>A string scalar in the AST, resolved against its owning arena.</summary>
public readonly struct StringRef : IEquatable<StringRef>
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly StringNodeId _id;

    internal StringRef(AstArena? arena, StringNodeId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    /// <summary>Whether this ref points to a value present in the document.</summary>
    public bool HasValue => _arena is not null && _id.HasValue;

    /// <summary>The underlying scalar handle (for caching / advanced scenarios).</summary>
    public StringNodeId Id => _id;

    /// <summary>The UTF-8 value bytes (empty when absent).</summary>
    public ReadOnlySpan<byte> Value => _arena is null ? default : ArenaChecked!.GetStringValue(_id);

    /// <summary>The value as an offset/length slice into the source YAML.</summary>
    public Utf8Slice Slice => _arena is null ? default : ArenaChecked!.GetStringSlice(_id);

    /// <summary>The source location of the scalar.</summary>
    public TextRange Range => _arena is null ? default : ArenaChecked!.GetStringRange(_id);

    /// <summary>Whether the scalar was quoted in YAML.</summary>
    public bool Quoted => _arena is not null && ArenaChecked!.GetStringQuoted(_id);

    /// <summary>The embedded <c>${{ }}</c> expression scalar, if any.</summary>
    public StringRef Expression => _arena is null ? default : new(ArenaChecked, ArenaChecked!.GetStringExpression(_id));

    /// <summary>Whether the value is absent or empty.</summary>
    public bool IsEmpty => Value.IsEmpty;

    /// <summary>Whether the value is present and non-empty.</summary>
    public bool HasText => HasValue && !Value.IsEmpty;

    /// <summary>Compares the value bytes to the given UTF-8 text.</summary>
    public bool ValueEquals(ReadOnlySpan<byte> utf8) => Value.SequenceEqual(utf8);

    /// <summary>Decodes the value to a UTF-16 string. Intended for diagnostics; avoid on hot paths.</summary>
    public string Decode()
    {
        var value = Value;
        return value.IsEmpty ? string.Empty : Encoding.UTF8.GetString(value);
    }

    public bool Equals(StringRef other) => ReferenceEquals(_arena, other._arena) && _id.Equals(other._id);

    public override bool Equals(object? obj) => obj is StringRef other && Equals(other);

    public override int GetHashCode() => _id.GetHashCode();

    public static bool operator ==(StringRef left, StringRef right) => left.Equals(right);

    public static bool operator !=(StringRef left, StringRef right) => !left.Equals(right);
}

/// <summary>A bool scalar in the AST.</summary>
public readonly struct BoolRef
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly BoolNodeId _id;

    internal BoolRef(AstArena? arena, BoolNodeId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public BoolNodeId Id => _id;

    public bool Value => _arena is not null && ArenaChecked!.GetBoolValue(_id);

    public TextRange Range => _arena is null ? default : ArenaChecked!.GetBoolRange(_id);

    /// <summary>The embedded <c>${{ }}</c> expression scalar, if any.</summary>
    public StringRef Expression => _arena is null ? default : new(ArenaChecked, ArenaChecked!.GetBoolExpression(_id));
}

/// <summary>An integer scalar in the AST.</summary>
public readonly struct IntRef
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly IntNodeId _id;

    internal IntRef(AstArena? arena, IntNodeId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public IntNodeId Id => _id;

    public long Value => _arena is null ? 0 : ArenaChecked!.GetIntValue(_id);

    public TextRange Range => _arena is null ? default : ArenaChecked!.GetIntRange(_id);

    /// <summary>The embedded <c>${{ }}</c> expression scalar, if any.</summary>
    public StringRef Expression => _arena is null ? default : new(ArenaChecked, ArenaChecked!.GetIntExpression(_id));
}

/// <summary>A float scalar in the AST.</summary>
public readonly struct FloatRef
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly FloatNodeId _id;

    internal FloatRef(AstArena? arena, FloatNodeId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public FloatNodeId Id => _id;

    public double Value => _arena is null ? 0 : ArenaChecked!.GetFloatValue(_id);

    public TextRange Range => _arena is null ? default : ArenaChecked!.GetFloatRange(_id);

    /// <summary>The embedded <c>${{ }}</c> expression scalar, if any.</summary>
    public StringRef Expression => _arena is null ? default : new(ArenaChecked, ArenaChecked!.GetFloatExpression(_id));
}

/// <summary>A map key (raw YAML key text) resolved against its owning arena.</summary>
public readonly struct KeyRef
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly Utf8Slice _slice;

    internal KeyRef(AstArena? arena, Utf8Slice slice)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _slice = slice;
    }

    /// <summary>The key as an offset/length slice into the source YAML.</summary>
    public Utf8Slice Slice => _slice;

    /// <summary>The UTF-8 key bytes.</summary>
    public ReadOnlySpan<byte> Bytes => _arena is null ? default : _slice.AsSpan(ArenaChecked!.Source);

    /// <summary>Compares the key bytes to the given UTF-8 text.</summary>
    public bool ValueEquals(ReadOnlySpan<byte> utf8) => Bytes.SequenceEqual(utf8);

    /// <summary>Decodes the key to a UTF-16 string. Intended for diagnostics; avoid on hot paths.</summary>
    public string Decode()
    {
        var bytes = Bytes;
        return bytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes);
    }
}
