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

/// <summary>
/// A (first, count) range over rows in an arena node table. The owning field/accessor
/// implies which table the range addresses (e.g. <c>PermissionsData.Scopes</c> → the
/// permission-scope table). <c>default</c> = section absent; present-but-empty has
/// <see cref="HasValue"/> with Count 0.
/// </summary>
public readonly record struct NodeRange
{
    private readonly int _firstRaw;
    private readonly int _count;

    internal NodeRange(int first, int count)
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

/// <summary>Handle referencing a <see cref="PermissionsData"/> row.</summary>
public readonly record struct PermissionsId
{
    private readonly int _raw;

    internal PermissionsId(int raw) => _raw = raw;

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

/// <summary>Handle referencing an <see cref="EnvData"/> row.</summary>
public readonly record struct EnvId
{
    private readonly int _raw;

    internal EnvId(int raw) => _raw = raw;

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

/// <summary>Handle referencing a <see cref="StrategyData"/> row.</summary>
public readonly record struct StrategyId
{
    private readonly int _raw;

    internal StrategyId(int raw) => _raw = raw;

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

/// <summary>Handle referencing a <see cref="MatrixData"/> row.</summary>
public readonly record struct MatrixId
{
    private readonly int _raw;

    internal MatrixId(int raw) => _raw = raw;

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

/// <summary>Handle referencing a <see cref="RawYamlData"/> row.</summary>
public readonly record struct RawYamlId
{
    private readonly int _raw;

    internal RawYamlId(int raw) => _raw = raw;

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

/// <summary>Handle referencing a <see cref="ContainerData"/> row.</summary>
public readonly record struct ContainerId
{
    private readonly int _raw;

    internal ContainerId(int raw) => _raw = raw;

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

/// <summary>Handle referencing a <see cref="ServicesData"/> row.</summary>
public readonly record struct ServicesId
{
    private readonly int _raw;

    internal ServicesId(int raw) => _raw = raw;

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

/// <summary>Handle referencing a <see cref="WorkflowCallData"/> row.</summary>
public readonly record struct WorkflowCallId
{
    private readonly int _raw;

    internal WorkflowCallId(int raw) => _raw = raw;

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

/// <summary>Handle referencing a <see cref="RunnerData"/> row.</summary>
public readonly record struct RunnerId
{
    private readonly int _raw;

    internal RunnerId(int raw) => _raw = raw;

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

/// <summary>Handle referencing a <see cref="StepData"/> row.</summary>
public readonly record struct StepId
{
    private readonly int _raw;

    internal StepId(int raw) => _raw = raw;

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

/// <summary>
/// A (first, count) range over the arena's shared <see cref="StepId"/> list store.
/// Step lists are ranges into the shared store (not the step row table) because nested
/// <c>parallel:</c> parsing appends step rows non-contiguously.
/// <c>default</c> = absent (<see cref="HasValue"/> false); a present-but-empty list has
/// <see cref="HasValue"/> true and <see cref="Count"/> 0.
/// </summary>
public readonly record struct StepIdRange
{
    private readonly int _firstRaw;
    private readonly int _count;

    internal StepIdRange(int first, int count)
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

/// <summary>Handle referencing an <see cref="ActionMetadataRunsData"/> row.</summary>
public readonly record struct ActionMetadataRunsId
{
    private readonly int _raw;

    internal ActionMetadataRunsId(int raw) => _raw = raw;

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

/// <summary>Handle referencing an <see cref="ActionMetadataBrandingData"/> row.</summary>
public readonly record struct ActionMetadataBrandingId
{
    private readonly int _raw;

    internal ActionMetadataBrandingId(int raw) => _raw = raw;

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
