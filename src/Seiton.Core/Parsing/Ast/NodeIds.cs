using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing.Ast;

// Typed 1-based handles into AstArena node tables (Stage 2). `default` = absent.
// Same convention as StringNodeId/BoolNodeId in AstArena.cs.

/// <summary>
/// A (first, count) range into the arena's shared <see cref="StringNodeId"/> list store.
/// Replaces <c>IReadOnlyList&lt;StringNodeId&gt;</c> AST fields (needs, labels, filter values, ...).
/// <c>default</c> = section absent; an empty-but-present list has <see cref="HasValue"/> with Count 0.
/// </summary>
public readonly record struct StringIdRange
{
    private readonly int _firstRaw;
    private readonly int _count;

    internal StringIdRange(int first, int count)
    {
        _firstRaw = first + 1;
        _count = count;
    }

    public bool HasValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _firstRaw > 0;
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    internal int First
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _firstRaw - 1;
    }
}

/// <summary>Handle referencing a <see cref="ConcurrencyData"/> row.</summary>
public readonly record struct ConcurrencyId
{
    private readonly int _raw;

    internal ConcurrencyId(int raw) => _raw = raw;

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
}

/// <summary>Handle referencing an <see cref="EnvironmentData"/> row.</summary>
public readonly record struct EnvironmentId
{
    private readonly int _raw;

    internal EnvironmentId(int raw) => _raw = raw;

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
}

/// <summary>Handle referencing a <see cref="CredentialsData"/> row.</summary>
public readonly record struct CredentialsId
{
    private readonly int _raw;

    internal CredentialsId(int raw) => _raw = raw;

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
}

/// <summary>Handle referencing a <see cref="SnapshotData"/> row.</summary>
public readonly record struct SnapshotId
{
    private readonly int _raw;

    internal SnapshotId(int raw) => _raw = raw;

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
}

/// <summary>Handle referencing a <see cref="DefaultsData"/> row.</summary>
public readonly record struct DefaultsId
{
    private readonly int _raw;

    internal DefaultsId(int raw) => _raw = raw;

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
}

/// <summary>Handle referencing a <see cref="DefaultsRunData"/> row.</summary>
public readonly record struct DefaultsRunId
{
    private readonly int _raw;

    internal DefaultsRunId(int raw) => _raw = raw;

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
}
