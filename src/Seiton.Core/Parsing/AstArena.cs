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

    // Data-oriented composite node tables (Stage 2). Rows are addressed by typed IDs
    // (ConcurrencyId, ...) and copied wholesale by BulkImportFrom for incremental parse.
    // Shared list store for StringNodeId ranges (needs, labels, filter values, ...).
    private NodeTable<StringNodeId> _stringIdItems;

    private NodeTable<PermissionsData> _permissionsTable;
    private NodeTable<PermissionScopeData> _permissionScopeTable;
    private NodeTable<EnvData> _envTable;
    private NodeTable<EnvVarData> _envVarTable;
    private NodeTable<StrategyData> _strategyTable;
    private NodeTable<MatrixData> _matrixTable;
    private NodeTable<MatrixRowData> _matrixRowTable;
    private NodeTable<MatrixCombinationsData> _matrixCombinationsTable;
    private NodeTable<NodeRange> _combinationEntryList;
    private NodeTable<RawYamlData> _rawYamlTable;
    private NodeTable<RawYamlId> _rawYamlIdItems;
    private NodeTable<RawYamlPropData> _rawYamlPropTable;
    private NodeTable<ContainerData> _containerTable;
    private NodeTable<ServicesData> _servicesTable;
    private NodeTable<ServiceData> _serviceTable;
    private NodeTable<WorkflowCallData> _workflowCallTable;
    private NodeTable<WorkflowCallInputData> _workflowCallInputTable;
    private NodeTable<WorkflowCallSecretData> _workflowCallSecretTable;
    private NodeTable<EventData> _eventTable;
    private NodeTable<WebhookEventData> _webhookEventTable;
    private NodeTable<WebhookEventFilterData> _webhookFilterTable;
    private NodeTable<ScheduledEventData> _scheduledEventTable;
    private NodeTable<ScheduleEntry> _scheduleEntryTable;
    private NodeTable<WorkflowDispatchEventData> _workflowDispatchEventTable;
    private NodeTable<DispatchInputData> _dispatchInputTable;
    private NodeTable<WorkflowCallEventData> _workflowCallEventTable;
    private NodeTable<WorkflowCallEventInputData> _wceInputTable;
    private NodeTable<WorkflowCallEventSecretData> _wceSecretTable;
    private NodeTable<WorkflowCallEventOutputData> _wceOutputTable;
    private NodeTable<RepositoryDispatchEventData> _repositoryDispatchEventTable;
    private NodeTable<ImageVersionEventData> _imageVersionEventTable;
    private NodeTable<RunnerData> _runnerTable;
    private NodeTable<ConcurrencyData> _concurrencyTable;
    private NodeTable<EnvironmentData> _environmentTable;
    private NodeTable<CredentialsData> _credentialsTable;
    private NodeTable<SnapshotData> _snapshotTable;
    private NodeTable<DefaultsData> _defaultsTable;
    private NodeTable<DefaultsRunData> _defaultsRunTable;

    // Step family tables (Stage 2). Step rows are addressed by StepId; step lists go
    // through the shared _stepIdItems store (nested parallel parsing appends step rows
    // non-contiguously). Exec payload tables are addressed by 1-based payload index.
    private NodeTable<StepData> _stepTable;
    private NodeTable<StepId> _stepIdItems;
    private NodeTable<ExecRunData> _execRunTable;
    private NodeTable<ExecActionData> _execActionTable;
    private NodeTable<ActionInputData> _actionInputTable;
    private NodeTable<ExecWaitData> _execWaitTable;
    private NodeTable<ExecWaitAllData> _execWaitAllTable;
    private NodeTable<ExecCancelData> _execCancelTable;
    private NodeTable<ExecParallelData> _execParallelTable;

    // ActionMetadata family tables (Stage 2). Inputs/Outputs are key-embedded row maps
    // (NodeRange over contiguous rows); Runs/Branding are single rows addressed by typed IDs.
    private NodeTable<ActionMetadataInputData> _actionMetadataInputTable;
    private NodeTable<ActionMetadataOutputData> _actionMetadataOutputTable;
    private NodeTable<ActionMetadataRunsData> _actionMetadataRunsTable;
    private NodeTable<ActionMetadataBrandingData> _actionMetadataBrandingTable;

    // Job family tables (Stage 3). Job rows are addressed by JobId; the workflow jobs
    // map is a NodeRange over key-embedded JobEntryData rows (key + JobId indirection so
    // incremental parse can splice reused JobIds next to freshly parsed ones), and job
    // outputs maps are NodeRanges over key-embedded JobOutputData rows.
    private NodeTable<JobData> _jobTable;
    private NodeTable<JobEntryData> _jobEntryTable;
    private NodeTable<JobOutputData> _jobOutputTable;

    // D-1: Pooled diagnostics buffer registered by ParseClassified/ParseIncremental.
    // Returned to ArrayPool<Diagnostic>.Shared on Dispose.
    private Diagnostic[]? _diagnosticsBuffer;

    // D-2: Pooled lint diagnostics buffer registered by LintEngine.
    // Returned to ArrayPool<Diagnostic>.Shared on Dispose.
    private Diagnostic[]? _lintDiagnosticsBuffer;

    // Bumped at the start of every ResetForSource/Dispose so stale handles/refs can be
    // detected in DEBUG builds. The field and increments are unconditional (an int is
    // free); only the checks are DEBUG-gated.
    private int _generation;

    /// <summary>Gets the current generation counter (bumped on every reset/dispose).</summary>
    internal int Generation => _generation;

    /// <summary>
    /// DEBUG-only: throws when the given captured generation no longer matches this arena's
    /// current generation, i.e. the arena was reset or disposed after the handle/ref was created.
    /// Compiled out entirely in Release builds.
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    internal void AssertGeneration(int generation)
    {
        if (generation != _generation)
        {
            throw new InvalidOperationException(
                "AST handle used after its arena was reset or disposed (use-after-dispose). " +
                "Copy needed values out before disposing the ParseResult/LintResult.");
        }
    }

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
    /// Registers a pooled diagnostics array with this arena. The array will be returned
    /// to <see cref="ArrayPool{T}.Shared"/> when this arena is disposed.
    /// </summary>
    internal void RegisterDiagnosticsBuffer(Diagnostic[] buffer) => _diagnosticsBuffer = buffer;

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
        _generation++;

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

        // Data-oriented node tables: clear counts, cap retained capacity
        _stringIdItems.Reset();
        _permissionsTable.Reset();
        _permissionScopeTable.Reset();
        _envTable.Reset();
        _envVarTable.Reset();
        _strategyTable.Reset();
        _matrixTable.Reset();
        _matrixRowTable.Reset();
        _matrixCombinationsTable.Reset();
        _combinationEntryList.Reset();
        _rawYamlTable.Reset();
        _rawYamlIdItems.Reset();
        _rawYamlPropTable.Reset();
        _containerTable.Reset();
        _servicesTable.Reset();
        _serviceTable.Reset();
        _workflowCallTable.Reset();
        _workflowCallInputTable.Reset();
        _workflowCallSecretTable.Reset();
        _eventTable.Reset();
        _webhookEventTable.Reset();
        _webhookFilterTable.Reset();
        _scheduledEventTable.Reset();
        _scheduleEntryTable.Reset();
        _workflowDispatchEventTable.Reset();
        _dispatchInputTable.Reset();
        _workflowCallEventTable.Reset();
        _wceInputTable.Reset();
        _wceSecretTable.Reset();
        _wceOutputTable.Reset();
        _repositoryDispatchEventTable.Reset();
        _imageVersionEventTable.Reset();
        _runnerTable.Reset();
        _concurrencyTable.Reset();
        _environmentTable.Reset();
        _credentialsTable.Reset();
        _snapshotTable.Reset();
        _defaultsTable.Reset();
        _defaultsRunTable.Reset();
        _stepTable.Reset();
        _stepIdItems.Reset();
        _execRunTable.Reset();
        _execActionTable.Reset();
        _actionInputTable.Reset();
        _execWaitTable.Reset();
        _execWaitAllTable.Reset();
        _execCancelTable.Reset();
        _execParallelTable.Reset();
        _actionMetadataInputTable.Reset();
        _actionMetadataOutputTable.Reset();
        _actionMetadataRunsTable.Reset();
        _actionMetadataBrandingTable.Reset();
        _jobTable.Reset();
        _jobEntryTable.Reset();
        _jobOutputTable.Reset();
        _stringIdItems.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _permissionsTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _permissionScopeTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _envTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _envVarTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _strategyTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _matrixTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _matrixRowTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _matrixCombinationsTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _combinationEntryList.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _rawYamlTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _rawYamlIdItems.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _rawYamlPropTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _containerTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _servicesTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _serviceTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _workflowCallTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _workflowCallInputTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _workflowCallSecretTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _eventTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _webhookEventTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _webhookFilterTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _scheduledEventTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _scheduleEntryTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _workflowDispatchEventTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _dispatchInputTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _workflowCallEventTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _wceInputTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _wceSecretTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _wceOutputTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _repositoryDispatchEventTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _imageVersionEventTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _runnerTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _concurrencyTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _environmentTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _credentialsTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _snapshotTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _defaultsTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _defaultsRunTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _stepTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _stepIdItems.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _execRunTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _execActionTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _actionInputTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _execWaitTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _execWaitAllTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _execCancelTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _execParallelTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _actionMetadataInputTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _actionMetadataOutputTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);
        _actionMetadataRunsTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _actionMetadataBrandingTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _jobTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _jobEntryTable.ReleaseOversized(DefaultNodeTableRetainedCapacity);
        _jobOutputTable.ReleaseOversized(DefaultStringIdItemsRetainedCapacity);

        _stringCount = 0;
        _boolCount = 0;
        _intCount = 0;
        _floatCount = 0;
        _source = [];

        if (cached is null)
        {
            // Cap backing arrays to prevent unbounded growth of the ThreadStatic cache
            // (the WASM memory concern the caps exist for). Scalar arrays go through
            // ArrayPool (re-renting large buckets is allocation-free), so shrinking to
            // the default cap does not cause per-parse alloc/free ping-pong.
            ShrinkIfOversized(ref _strings, DefaultStringCapacity);
            ShrinkIfOversized(ref _bools, DefaultBoolCapacity);
            ShrinkIfOversized(ref _ints, DefaultIntCapacity);
            ShrinkIfOversized(ref _floats, DefaultFloatCapacity);
            cached = this;
        }
        else
        {
            // Cache is already occupied — return all pool-rented arrays and discard this arena.
            ArrayPool<StringNodeData>.Shared.Return(_strings);
            ArrayPool<BoolNodeData>.Shared.Return(_bools);
            ArrayPool<IntNodeData>.Shared.Return(_ints);
            ArrayPool<FloatNodeData>.Shared.Return(_floats);
            _stringIdItems.ReleaseAll();
            _permissionsTable.ReleaseAll();
            _permissionScopeTable.ReleaseAll();
            _envTable.ReleaseAll();
            _envVarTable.ReleaseAll();
            _strategyTable.ReleaseAll();
            _matrixTable.ReleaseAll();
            _matrixRowTable.ReleaseAll();
            _matrixCombinationsTable.ReleaseAll();
            _combinationEntryList.ReleaseAll();
            _rawYamlTable.ReleaseAll();
            _rawYamlIdItems.ReleaseAll();
            _rawYamlPropTable.ReleaseAll();
            _containerTable.ReleaseAll();
            _servicesTable.ReleaseAll();
            _serviceTable.ReleaseAll();
            _workflowCallTable.ReleaseAll();
            _workflowCallInputTable.ReleaseAll();
            _workflowCallSecretTable.ReleaseAll();
            _eventTable.ReleaseAll();
            _webhookEventTable.ReleaseAll();
            _webhookFilterTable.ReleaseAll();
            _scheduledEventTable.ReleaseAll();
            _scheduleEntryTable.ReleaseAll();
            _workflowDispatchEventTable.ReleaseAll();
            _dispatchInputTable.ReleaseAll();
            _workflowCallEventTable.ReleaseAll();
            _wceInputTable.ReleaseAll();
            _wceSecretTable.ReleaseAll();
            _wceOutputTable.ReleaseAll();
            _repositoryDispatchEventTable.ReleaseAll();
            _imageVersionEventTable.ReleaseAll();
            _runnerTable.ReleaseAll();
            _concurrencyTable.ReleaseAll();
            _environmentTable.ReleaseAll();
            _credentialsTable.ReleaseAll();
            _snapshotTable.ReleaseAll();
            _defaultsTable.ReleaseAll();
            _defaultsRunTable.ReleaseAll();
            _stepTable.ReleaseAll();
            _stepIdItems.ReleaseAll();
            _execRunTable.ReleaseAll();
            _execActionTable.ReleaseAll();
            _actionInputTable.ReleaseAll();
            _execWaitTable.ReleaseAll();
            _execWaitAllTable.ReleaseAll();
            _execCancelTable.ReleaseAll();
            _execParallelTable.ReleaseAll();
            _actionMetadataInputTable.ReleaseAll();
            _actionMetadataOutputTable.ReleaseAll();
            _actionMetadataRunsTable.ReleaseAll();
            _actionMetadataBrandingTable.ReleaseAll();
            _jobTable.ReleaseAll();
            _jobEntryTable.ReleaseAll();
            _jobOutputTable.ReleaseAll();
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

    // Section node pool default capacities. Env appears per step + per job + workflow-level,
    // Runner/Strategy/Matrix/MatrixRow per job, the rest are occasional per-job sections.
    private const int DefaultSectionNodeCapacity = 8;

    // Retained-capacity cap for data-oriented node tables (rows are small structs;
    // ArrayPool re-rent is allocation-free, so the cap only bounds ThreadStatic retention).
    private const int DefaultNodeTableRetainedCapacity = 64;

    // The shared StringNodeId list store grows with needs/labels/filter values across the file.
    private const int DefaultStringIdItemsRetainedCapacity = 512;
    private const int DefaultEnvCapacity = 64;
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
        _generation++;
        _source = source;
        _stringCount = 0;
        _boolCount = 0;
        _intCount = 0;
        _floatCount = 0;
        _stringIdItems.Reset();
        _permissionsTable.Reset();
        _permissionScopeTable.Reset();
        _envTable.Reset();
        _envVarTable.Reset();
        _strategyTable.Reset();
        _matrixTable.Reset();
        _matrixRowTable.Reset();
        _matrixCombinationsTable.Reset();
        _combinationEntryList.Reset();
        _rawYamlTable.Reset();
        _rawYamlIdItems.Reset();
        _rawYamlPropTable.Reset();
        _containerTable.Reset();
        _servicesTable.Reset();
        _serviceTable.Reset();
        _workflowCallTable.Reset();
        _workflowCallInputTable.Reset();
        _workflowCallSecretTable.Reset();
        _eventTable.Reset();
        _webhookEventTable.Reset();
        _webhookFilterTable.Reset();
        _scheduledEventTable.Reset();
        _scheduleEntryTable.Reset();
        _workflowDispatchEventTable.Reset();
        _dispatchInputTable.Reset();
        _workflowCallEventTable.Reset();
        _wceInputTable.Reset();
        _wceSecretTable.Reset();
        _wceOutputTable.Reset();
        _repositoryDispatchEventTable.Reset();
        _imageVersionEventTable.Reset();
        _runnerTable.Reset();
        _concurrencyTable.Reset();
        _environmentTable.Reset();
        _credentialsTable.Reset();
        _snapshotTable.Reset();
        _defaultsTable.Reset();
        _defaultsRunTable.Reset();
        _stepTable.Reset();
        _stepIdItems.Reset();
        _execRunTable.Reset();
        _execActionTable.Reset();
        _actionInputTable.Reset();
        _execWaitTable.Reset();
        _execWaitAllTable.Reset();
        _execCancelTable.Reset();
        _execParallelTable.Reset();
        _actionMetadataInputTable.Reset();
        _actionMetadataOutputTable.Reset();
        _actionMetadataRunsTable.Reset();
        _actionMetadataBrandingTable.Reset();
        _jobTable.Reset();
        _jobEntryTable.Reset();
        _jobOutputTable.Reset();
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

    // Data-oriented node table accessors (Stage 2)

    /// <summary>Copies the given string scalar handles into the shared list store and returns their range.</summary>
    public StringIdRange AddStringIdList(ReadOnlySpan<StringNodeId> items)
    {
        var first = _stringIdItems.Count;
        for (var i = 0; i < items.Length; i++)
        {
            _stringIdItems.Add(in items[i]);
        }

        return new StringIdRange(first, items.Length);
    }

    /// <summary>Resolves one element of a <see cref="StringIdRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal StringNodeId GetStringIdAt(StringIdRange range, int index) => _stringIdItems[range.First + index];

    // Step family accessors. Step rows are addressed by StepId; step lists are ranges over
    // the shared StepId list store (nested parallel parsing appends step rows non-contiguously).
    // StepData.ExecPayload is a 1-based index into the kind-specific payload table.

    /// <summary>Appends a <see cref="StepData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StepId AddStep(in StepData data) => new(_stepTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="StepData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly StepData GetStep(StepId id) => ref _stepTable[id.Index];

    /// <summary>Copies the given step handles into the shared list store and returns their range.</summary>
    public StepIdRange AddStepIdList(ReadOnlySpan<StepId> items)
    {
        var first = _stepIdItems.Count;
        for (var i = 0; i < items.Length; i++)
        {
            _stepIdItems.Add(in items[i]);
        }

        return new StepIdRange(first, items.Length);
    }

    /// <summary>Resolves one element of a <see cref="StepIdRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal StepId GetStepIdAt(StepIdRange range, int index) => _stepIdItems[range.First + index];

    /// <summary>Appends an <see cref="ExecRunData"/> payload row; returns its 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddExecRun(in ExecRunData data) => _execRunTable.Add(in data) + 1;

    /// <summary>Resolves a <c>run:</c> payload row by 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ExecRunData GetExecRun(int payload) => ref _execRunTable[payload - 1];

    /// <summary>Appends an <see cref="ExecActionData"/> payload row; returns its 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddExecAction(in ExecActionData data) => _execActionTable.Add(in data) + 1;

    /// <summary>Resolves a <c>uses:</c> payload row by 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ExecActionData GetExecAction(int payload) => ref _execActionTable[payload - 1];

    /// <summary>Appends an <see cref="ActionInputData"/> row (rows of one <c>with:</c> block must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddActionInput(in ActionInputData data) => _actionInputTable.Add(in data);

    /// <summary>Gets the current action-input row count (range start capture).</summary>
    internal int ActionInputCount => _actionInputTable.Count;

    /// <summary>Resolves one element of an action-input <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ActionInputData GetActionInputAt(NodeRange range, int index) => ref _actionInputTable[range.First + index];

    /// <summary>Appends an <see cref="ExecWaitData"/> payload row; returns its 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddExecWait(in ExecWaitData data) => _execWaitTable.Add(in data) + 1;

    /// <summary>Resolves a <c>wait:</c> payload row by 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ExecWaitData GetExecWait(int payload) => ref _execWaitTable[payload - 1];

    /// <summary>Appends an <see cref="ExecWaitAllData"/> payload row; returns its 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddExecWaitAll(in ExecWaitAllData data) => _execWaitAllTable.Add(in data) + 1;

    /// <summary>Resolves a <c>wait-all:</c> payload row by 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ExecWaitAllData GetExecWaitAll(int payload) => ref _execWaitAllTable[payload - 1];

    /// <summary>Appends an <see cref="ExecCancelData"/> payload row; returns its 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddExecCancel(in ExecCancelData data) => _execCancelTable.Add(in data) + 1;

    /// <summary>Resolves a <c>cancel:</c> payload row by 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ExecCancelData GetExecCancel(int payload) => ref _execCancelTable[payload - 1];

    /// <summary>Appends an <see cref="ExecParallelData"/> payload row; returns its 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddExecParallel(in ExecParallelData data) => _execParallelTable.Add(in data) + 1;

    /// <summary>Resolves a <c>parallel:</c> payload row by 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ExecParallelData GetExecParallel(int payload) => ref _execParallelTable[payload - 1];

    // ActionMetadata family accessors. Inputs/Outputs rows of one map are contiguous
    // (key embedded in the row); Runs/Branding are single rows addressed by typed IDs.

    /// <summary>Appends an <see cref="ActionMetadataInputData"/> row (rows of one <c>inputs:</c> map must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddActionMetadataInput(in ActionMetadataInputData data) => _actionMetadataInputTable.Add(in data);

    /// <summary>Gets the current action-metadata-input row count (range start capture).</summary>
    internal int ActionMetadataInputCount => _actionMetadataInputTable.Count;

    /// <summary>Resolves one element of an action-metadata-input <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ActionMetadataInputData GetActionMetadataInputAt(NodeRange range, int index) => ref _actionMetadataInputTable[range.First + index];

    /// <summary>Appends an <see cref="ActionMetadataOutputData"/> row (rows of one <c>outputs:</c> map must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddActionMetadataOutput(in ActionMetadataOutputData data) => _actionMetadataOutputTable.Add(in data);

    /// <summary>Gets the current action-metadata-output row count (range start capture).</summary>
    internal int ActionMetadataOutputCount => _actionMetadataOutputTable.Count;

    /// <summary>Resolves one element of an action-metadata-output <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ActionMetadataOutputData GetActionMetadataOutputAt(NodeRange range, int index) => ref _actionMetadataOutputTable[range.First + index];

    /// <summary>Appends an <see cref="ActionMetadataRunsData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ActionMetadataRunsId AddActionMetadataRuns(in ActionMetadataRunsData data) => new(_actionMetadataRunsTable.Add(in data) + 1);

    /// <summary>Resolves an <see cref="ActionMetadataRunsData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ActionMetadataRunsData GetActionMetadataRuns(ActionMetadataRunsId id) => ref _actionMetadataRunsTable[id.Index];

    /// <summary>Appends an <see cref="ActionMetadataBrandingData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ActionMetadataBrandingId AddActionMetadataBranding(in ActionMetadataBrandingData data) => new(_actionMetadataBrandingTable.Add(in data) + 1);

    /// <summary>Resolves an <see cref="ActionMetadataBrandingData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ActionMetadataBrandingData GetActionMetadataBranding(ActionMetadataBrandingId id) => ref _actionMetadataBrandingTable[id.Index];

    /// <summary>Appends a <see cref="PermissionsData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PermissionsId AddPermissions(in PermissionsData data) => new(_permissionsTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="PermissionsData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly PermissionsData GetPermissions(PermissionsId id) => ref _permissionsTable[id.Index];

    /// <summary>Appends a <see cref="PermissionScopeData"/> row (rows of one map must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddPermissionScope(in PermissionScopeData data) => _permissionScopeTable.Add(in data);

    /// <summary>Gets the current permission-scope row count (range start capture).</summary>
    internal int PermissionScopeCount => _permissionScopeTable.Count;

    /// <summary>Resolves one element of a permission-scope <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly PermissionScopeData GetPermissionScopeAt(NodeRange range, int index) => ref _permissionScopeTable[range.First + index];

    /// <summary>Appends an <see cref="EnvData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EnvId AddEnv(in EnvData data) => new(_envTable.Add(in data) + 1);

    /// <summary>Resolves an <see cref="EnvData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly EnvData GetEnv(EnvId id) => ref _envTable[id.Index];

    /// <summary>Appends an <see cref="EnvVarData"/> row (rows of one map must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddEnvVar(in EnvVarData data) => _envVarTable.Add(in data);

    /// <summary>Gets the current env-var row count (range start capture).</summary>
    internal int EnvVarCount => _envVarTable.Count;

    /// <summary>Resolves one element of an env-var <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly EnvVarData GetEnvVarAt(NodeRange range, int index) => ref _envVarTable[range.First + index];

    /// <summary>Appends a <see cref="StrategyData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StrategyId AddStrategy(in StrategyData data) => new(_strategyTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="StrategyData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly StrategyData GetStrategy(StrategyId id) => ref _strategyTable[id.Index];

    /// <summary>Appends a <see cref="MatrixData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MatrixId AddMatrix(in MatrixData data) => new(_matrixTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="MatrixData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly MatrixData GetMatrix(MatrixId id) => ref _matrixTable[id.Index];

    /// <summary>Appends a <see cref="MatrixRowData"/> row (rows of one map must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddMatrixRow(in MatrixRowData data) => _matrixRowTable.Add(in data);

    /// <summary>Gets the current matrix-row count (range start capture).</summary>
    internal int MatrixRowCount => _matrixRowTable.Count;

    /// <summary>Resolves one element of a matrix-row <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly MatrixRowData GetMatrixRowAt(NodeRange range, int index) => ref _matrixRowTable[range.First + index];

    /// <summary>Appends a <see cref="MatrixCombinationsData"/> row (rows of one list must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddMatrixCombinations(in MatrixCombinationsData data) => _matrixCombinationsTable.Add(in data);

    /// <summary>Gets the current matrix-combinations count (range start capture).</summary>
    internal int MatrixCombinationsCount => _matrixCombinationsTable.Count;

    /// <summary>Resolves one element of a matrix-combinations <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly MatrixCombinationsData GetMatrixCombinationsAt(NodeRange range, int index) => ref _matrixCombinationsTable[range.First + index];

    /// <summary>Copies combination-entry prop ranges into the shared entry-list store and returns their range.</summary>
    public NodeRange AddCombinationEntryList(ReadOnlySpan<NodeRange> entries)
    {
        var first = _combinationEntryList.Count;
        for (var i = 0; i < entries.Length; i++)
        {
            _combinationEntryList.Add(in entries[i]);
        }

        return new NodeRange(first, entries.Length);
    }

    /// <summary>Resolves one element of a combination-entry-list <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NodeRange GetCombinationEntryAt(NodeRange range, int index) => _combinationEntryList[range.First + index];

    /// <summary>Appends a <see cref="RawYamlData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RawYamlId AddRawYaml(in RawYamlData data) => new(_rawYamlTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="RawYamlData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly RawYamlData GetRawYaml(RawYamlId id) => ref _rawYamlTable[id.Index];

    /// <summary>Copies raw-yaml ids into the shared id-list store and returns their range.</summary>
    public NodeRange AddRawYamlIdList(ReadOnlySpan<RawYamlId> items)
    {
        var first = _rawYamlIdItems.Count;
        for (var i = 0; i < items.Length; i++)
        {
            _rawYamlIdItems.Add(in items[i]);
        }

        return new NodeRange(first, items.Length);
    }

    /// <summary>Resolves one element of a raw-yaml id-list <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal RawYamlId GetRawYamlIdAt(NodeRange range, int index) => _rawYamlIdItems[range.First + index];

    /// <summary>Copies raw-yaml prop rows into the shared prop table and returns their range.</summary>
    public NodeRange AddRawYamlPropList(ReadOnlySpan<RawYamlPropData> props)
    {
        var first = _rawYamlPropTable.Count;
        for (var i = 0; i < props.Length; i++)
        {
            _rawYamlPropTable.Add(in props[i]);
        }

        return new NodeRange(first, props.Length);
    }

    /// <summary>Resolves one element of a raw-yaml prop <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly RawYamlPropData GetRawYamlPropAt(NodeRange range, int index) => ref _rawYamlPropTable[range.First + index];

    // Event family accessors. Event header rows for one `on:` section are contiguous;
    // EventData.Payload is a 1-based index into the kind-specific payload table.

    /// <summary>Appends an <see cref="EventData"/> header row (rows of one <c>on:</c> section must be contiguous).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddEvent(in EventData data) => _eventTable.Add(in data);

    /// <summary>Gets the current event header row count (range start capture).</summary>
    internal int EventCount => _eventTable.Count;

    /// <summary>Resolves an event header row by absolute index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly EventData GetEvent(int index) => ref _eventTable[index];

    /// <summary>Appends a <see cref="WebhookEventData"/> payload row; returns its 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddWebhookEvent(in WebhookEventData data) => _webhookEventTable.Add(in data) + 1;

    /// <summary>Resolves a webhook payload row by 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly WebhookEventData GetWebhookEvent(int payload) => ref _webhookEventTable[payload - 1];

    /// <summary>Appends a <see cref="WebhookEventFilterData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WebhookFilterId AddWebhookFilter(in WebhookEventFilterData data) => new(_webhookFilterTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="WebhookEventFilterData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly WebhookEventFilterData GetWebhookFilter(WebhookFilterId id) => ref _webhookFilterTable[id.Index];

    /// <summary>Appends a <see cref="ScheduledEventData"/> payload row; returns its 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddScheduledEvent(in ScheduledEventData data) => _scheduledEventTable.Add(in data) + 1;

    /// <summary>Resolves a schedule payload row by 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ScheduledEventData GetScheduledEvent(int payload) => ref _scheduledEventTable[payload - 1];

    /// <summary>Appends a <see cref="ScheduleEntry"/> row (rows of one schedule must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddScheduleEntry(in ScheduleEntry data) => _scheduleEntryTable.Add(in data);

    /// <summary>Gets the current schedule-entry row count (range start capture).</summary>
    internal int ScheduleEntryCount => _scheduleEntryTable.Count;

    /// <summary>Resolves one element of a schedule-entry <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ScheduleEntry GetScheduleEntryAt(NodeRange range, int index) => ref _scheduleEntryTable[range.First + index];

    /// <summary>Appends a <see cref="WorkflowDispatchEventData"/> payload row; returns its 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddWorkflowDispatchEvent(in WorkflowDispatchEventData data) => _workflowDispatchEventTable.Add(in data) + 1;

    /// <summary>Resolves a workflow_dispatch payload row by 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly WorkflowDispatchEventData GetWorkflowDispatchEvent(int payload) => ref _workflowDispatchEventTable[payload - 1];

    /// <summary>Appends a <see cref="DispatchInputData"/> row (rows of one map must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddDispatchInput(in DispatchInputData data) => _dispatchInputTable.Add(in data);

    /// <summary>Gets the current dispatch-input row count (range start capture).</summary>
    internal int DispatchInputCount => _dispatchInputTable.Count;

    /// <summary>Resolves one element of a dispatch-input <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly DispatchInputData GetDispatchInputAt(NodeRange range, int index) => ref _dispatchInputTable[range.First + index];

    /// <summary>Appends a <see cref="WorkflowCallEventData"/> payload row; returns its 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddWorkflowCallEvent(in WorkflowCallEventData data) => _workflowCallEventTable.Add(in data) + 1;

    /// <summary>Resolves a workflow_call payload row by 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly WorkflowCallEventData GetWorkflowCallEvent(int payload) => ref _workflowCallEventTable[payload - 1];

    /// <summary>Appends a <see cref="WorkflowCallEventInputData"/> row (rows of one list must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddWorkflowCallEventInput(in WorkflowCallEventInputData data) => _wceInputTable.Add(in data);

    /// <summary>Gets the current workflow-call event input row count (range start capture).</summary>
    internal int WorkflowCallEventInputCount => _wceInputTable.Count;

    /// <summary>Resolves one element of a workflow-call event input <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly WorkflowCallEventInputData GetWorkflowCallEventInputAt(NodeRange range, int index) => ref _wceInputTable[range.First + index];

    /// <summary>Appends a <see cref="WorkflowCallEventSecretData"/> row (rows of one map must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddWorkflowCallEventSecret(in WorkflowCallEventSecretData data) => _wceSecretTable.Add(in data);

    /// <summary>Gets the current workflow-call event secret row count (range start capture).</summary>
    internal int WorkflowCallEventSecretCount => _wceSecretTable.Count;

    /// <summary>Resolves one element of a workflow-call event secret <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly WorkflowCallEventSecretData GetWorkflowCallEventSecretAt(NodeRange range, int index) => ref _wceSecretTable[range.First + index];

    /// <summary>Appends a <see cref="WorkflowCallEventOutputData"/> row (rows of one map must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddWorkflowCallEventOutput(in WorkflowCallEventOutputData data) => _wceOutputTable.Add(in data);

    /// <summary>Gets the current workflow-call event output row count (range start capture).</summary>
    internal int WorkflowCallEventOutputCount => _wceOutputTable.Count;

    /// <summary>Resolves one element of a workflow-call event output <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly WorkflowCallEventOutputData GetWorkflowCallEventOutputAt(NodeRange range, int index) => ref _wceOutputTable[range.First + index];

    /// <summary>Appends a <see cref="RepositoryDispatchEventData"/> payload row; returns its 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddRepositoryDispatchEvent(in RepositoryDispatchEventData data) => _repositoryDispatchEventTable.Add(in data) + 1;

    /// <summary>Resolves a repository_dispatch payload row by 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly RepositoryDispatchEventData GetRepositoryDispatchEvent(int payload) => ref _repositoryDispatchEventTable[payload - 1];

    /// <summary>Appends an <see cref="ImageVersionEventData"/> payload row; returns its 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddImageVersionEvent(in ImageVersionEventData data) => _imageVersionEventTable.Add(in data) + 1;

    /// <summary>Resolves an image_version payload row by 1-based payload index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ImageVersionEventData GetImageVersionEvent(int payload) => ref _imageVersionEventTable[payload - 1];

    /// <summary>Appends a <see cref="ContainerData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ContainerId AddContainer(in ContainerData data) => new(_containerTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="ContainerData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ContainerData GetContainer(ContainerId id) => ref _containerTable[id.Index];

    /// <summary>Appends a <see cref="ServicesData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ServicesId AddServices(in ServicesData data) => new(_servicesTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="ServicesData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ServicesData GetServices(ServicesId id) => ref _servicesTable[id.Index];

    /// <summary>Appends a <see cref="ServiceData"/> row (rows of one map must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddService(in ServiceData data) => _serviceTable.Add(in data);

    /// <summary>Gets the current service row count (range start capture).</summary>
    internal int ServiceCount => _serviceTable.Count;

    /// <summary>Resolves one element of a service <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ServiceData GetServiceAt(NodeRange range, int index) => ref _serviceTable[range.First + index];

    /// <summary>Appends a <see cref="WorkflowCallData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WorkflowCallId AddWorkflowCall(in WorkflowCallData data) => new(_workflowCallTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="WorkflowCallData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly WorkflowCallData GetWorkflowCall(WorkflowCallId id) => ref _workflowCallTable[id.Index];

    /// <summary>Appends a <see cref="WorkflowCallInputData"/> row (rows of one map must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddWorkflowCallInput(in WorkflowCallInputData data) => _workflowCallInputTable.Add(in data);

    /// <summary>Gets the current workflow-call input row count (range start capture).</summary>
    internal int WorkflowCallInputCount => _workflowCallInputTable.Count;

    /// <summary>Resolves one element of a workflow-call input <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly WorkflowCallInputData GetWorkflowCallInputAt(NodeRange range, int index) => ref _workflowCallInputTable[range.First + index];

    /// <summary>Appends a <see cref="WorkflowCallSecretData"/> row (rows of one map must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddWorkflowCallSecret(in WorkflowCallSecretData data) => _workflowCallSecretTable.Add(in data);

    /// <summary>Gets the current workflow-call secret row count (range start capture).</summary>
    internal int WorkflowCallSecretCount => _workflowCallSecretTable.Count;

    /// <summary>Resolves one element of a workflow-call secret <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly WorkflowCallSecretData GetWorkflowCallSecretAt(NodeRange range, int index) => ref _workflowCallSecretTable[range.First + index];

    /// <summary>Appends a <see cref="RunnerData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RunnerId AddRunner(in RunnerData data) => new(_runnerTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="RunnerData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly RunnerData GetRunner(RunnerId id) => ref _runnerTable[id.Index];

    /// <summary>Appends a <see cref="DefaultsData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DefaultsId AddDefaults(in DefaultsData data) => new(_defaultsTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="DefaultsData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly DefaultsData GetDefaults(DefaultsId id) => ref _defaultsTable[id.Index];

    /// <summary>Appends a <see cref="DefaultsRunData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DefaultsRunId AddDefaultsRun(in DefaultsRunData data) => new(_defaultsRunTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="DefaultsRunData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly DefaultsRunData GetDefaultsRun(DefaultsRunId id) => ref _defaultsRunTable[id.Index];

    /// <summary>Appends a <see cref="ConcurrencyData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ConcurrencyId AddConcurrency(in ConcurrencyData data) => new(_concurrencyTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="ConcurrencyData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ConcurrencyData GetConcurrency(ConcurrencyId id) => ref _concurrencyTable[id.Index];

    /// <summary>Appends an <see cref="EnvironmentData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EnvironmentId AddEnvironment(in EnvironmentData data) => new(_environmentTable.Add(in data) + 1);

    /// <summary>Resolves an <see cref="EnvironmentData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly EnvironmentData GetEnvironment(EnvironmentId id) => ref _environmentTable[id.Index];

    /// <summary>Appends a <see cref="CredentialsData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CredentialsId AddCredentials(in CredentialsData data) => new(_credentialsTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="CredentialsData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly CredentialsData GetCredentials(CredentialsId id) => ref _credentialsTable[id.Index];

    /// <summary>Appends a <see cref="SnapshotData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SnapshotId AddSnapshot(in SnapshotData data) => new(_snapshotTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="SnapshotData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly SnapshotData GetSnapshot(SnapshotId id) => ref _snapshotTable[id.Index];

    // Job family accessors (Stage 3). Job rows are addressed by JobId; the entries of one
    // workflow jobs map and the rows of one job outputs map are appended contiguously.

    /// <summary>Appends a <see cref="JobData"/> row and returns its handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JobId AddJob(in JobData data) => new(_jobTable.Add(in data) + 1);

    /// <summary>Resolves a <see cref="JobData"/> row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly JobData GetJob(JobId id) => ref _jobTable[id.Index];

    /// <summary>Appends a <see cref="JobEntryData"/> row (entries of one jobs map must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddJobEntry(in JobEntryData data) => _jobEntryTable.Add(in data);

    /// <summary>Gets the current job-entry row count (range start capture).</summary>
    internal int JobEntryCount => _jobEntryTable.Count;

    /// <summary>Resolves one element of a jobs-map <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly JobEntryData GetJobEntryAt(NodeRange range, int index) => ref _jobEntryTable[range.First + index];

    /// <summary>Appends a <see cref="JobOutputData"/> row (rows of one <c>outputs:</c> map must be appended contiguously).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddJobOutput(in JobOutputData data) => _jobOutputTable.Add(in data);

    /// <summary>Gets the current job-output row count (range start capture).</summary>
    internal int JobOutputCount => _jobOutputTable.Count;

    /// <summary>Resolves one element of a job-outputs <see cref="NodeRange"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly JobOutputData GetJobOutputAt(NodeRange range, int index) => ref _jobOutputTable[range.First + index];

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

        // Data-oriented node tables are copied wholesale: reused sections/jobs are only
        // spliced when their source bytes are byte-identical at identical offsets, so
        // row indices (and the IDs stored in reused nodes) stay valid in this arena.
        _stringIdItems.CopyFrom(in source._stringIdItems, source._stringIdItems.Count);
        _permissionsTable.CopyFrom(in source._permissionsTable, source._permissionsTable.Count);
        _permissionScopeTable.CopyFrom(in source._permissionScopeTable, source._permissionScopeTable.Count);
        _envTable.CopyFrom(in source._envTable, source._envTable.Count);
        _envVarTable.CopyFrom(in source._envVarTable, source._envVarTable.Count);
        _strategyTable.CopyFrom(in source._strategyTable, source._strategyTable.Count);
        _matrixTable.CopyFrom(in source._matrixTable, source._matrixTable.Count);
        _matrixRowTable.CopyFrom(in source._matrixRowTable, source._matrixRowTable.Count);
        _matrixCombinationsTable.CopyFrom(in source._matrixCombinationsTable, source._matrixCombinationsTable.Count);
        _combinationEntryList.CopyFrom(in source._combinationEntryList, source._combinationEntryList.Count);
        _rawYamlTable.CopyFrom(in source._rawYamlTable, source._rawYamlTable.Count);
        _rawYamlIdItems.CopyFrom(in source._rawYamlIdItems, source._rawYamlIdItems.Count);
        _rawYamlPropTable.CopyFrom(in source._rawYamlPropTable, source._rawYamlPropTable.Count);
        _containerTable.CopyFrom(in source._containerTable, source._containerTable.Count);
        _servicesTable.CopyFrom(in source._servicesTable, source._servicesTable.Count);
        _serviceTable.CopyFrom(in source._serviceTable, source._serviceTable.Count);
        _workflowCallTable.CopyFrom(in source._workflowCallTable, source._workflowCallTable.Count);
        _workflowCallInputTable.CopyFrom(in source._workflowCallInputTable, source._workflowCallInputTable.Count);
        _workflowCallSecretTable.CopyFrom(in source._workflowCallSecretTable, source._workflowCallSecretTable.Count);
        _eventTable.CopyFrom(in source._eventTable, source._eventTable.Count);
        _webhookEventTable.CopyFrom(in source._webhookEventTable, source._webhookEventTable.Count);
        _webhookFilterTable.CopyFrom(in source._webhookFilterTable, source._webhookFilterTable.Count);
        _scheduledEventTable.CopyFrom(in source._scheduledEventTable, source._scheduledEventTable.Count);
        _scheduleEntryTable.CopyFrom(in source._scheduleEntryTable, source._scheduleEntryTable.Count);
        _workflowDispatchEventTable.CopyFrom(in source._workflowDispatchEventTable, source._workflowDispatchEventTable.Count);
        _dispatchInputTable.CopyFrom(in source._dispatchInputTable, source._dispatchInputTable.Count);
        _workflowCallEventTable.CopyFrom(in source._workflowCallEventTable, source._workflowCallEventTable.Count);
        _wceInputTable.CopyFrom(in source._wceInputTable, source._wceInputTable.Count);
        _wceSecretTable.CopyFrom(in source._wceSecretTable, source._wceSecretTable.Count);
        _wceOutputTable.CopyFrom(in source._wceOutputTable, source._wceOutputTable.Count);
        _repositoryDispatchEventTable.CopyFrom(in source._repositoryDispatchEventTable, source._repositoryDispatchEventTable.Count);
        _imageVersionEventTable.CopyFrom(in source._imageVersionEventTable, source._imageVersionEventTable.Count);
        _runnerTable.CopyFrom(in source._runnerTable, source._runnerTable.Count);
        _concurrencyTable.CopyFrom(in source._concurrencyTable, source._concurrencyTable.Count);
        _environmentTable.CopyFrom(in source._environmentTable, source._environmentTable.Count);
        _credentialsTable.CopyFrom(in source._credentialsTable, source._credentialsTable.Count);
        _snapshotTable.CopyFrom(in source._snapshotTable, source._snapshotTable.Count);
        _defaultsTable.CopyFrom(in source._defaultsTable, source._defaultsTable.Count);
        _defaultsRunTable.CopyFrom(in source._defaultsRunTable, source._defaultsRunTable.Count);
        _stepTable.CopyFrom(in source._stepTable, source._stepTable.Count);
        _stepIdItems.CopyFrom(in source._stepIdItems, source._stepIdItems.Count);
        _execRunTable.CopyFrom(in source._execRunTable, source._execRunTable.Count);
        _execActionTable.CopyFrom(in source._execActionTable, source._execActionTable.Count);
        _actionInputTable.CopyFrom(in source._actionInputTable, source._actionInputTable.Count);
        _execWaitTable.CopyFrom(in source._execWaitTable, source._execWaitTable.Count);
        _execWaitAllTable.CopyFrom(in source._execWaitAllTable, source._execWaitAllTable.Count);
        _execCancelTable.CopyFrom(in source._execCancelTable, source._execCancelTable.Count);
        _execParallelTable.CopyFrom(in source._execParallelTable, source._execParallelTable.Count);
        _actionMetadataInputTable.CopyFrom(in source._actionMetadataInputTable, source._actionMetadataInputTable.Count);
        _actionMetadataOutputTable.CopyFrom(in source._actionMetadataOutputTable, source._actionMetadataOutputTable.Count);
        _actionMetadataRunsTable.CopyFrom(in source._actionMetadataRunsTable, source._actionMetadataRunsTable.Count);
        _actionMetadataBrandingTable.CopyFrom(in source._actionMetadataBrandingTable, source._actionMetadataBrandingTable.Count);
        _jobTable.CopyFrom(in source._jobTable, source._jobTable.Count);
        _jobEntryTable.CopyFrom(in source._jobEntryTable, source._jobEntryTable.Count);
        _jobOutputTable.CopyFrom(in source._jobOutputTable, source._jobOutputTable.Count);
    }

    /// <summary>Gets the current number of string entries in the arena.</summary>
    internal int StringCount => _stringCount;

    /// <summary>Gets the current number of bool entries in the arena.</summary>
    internal int BoolCount => _boolCount;

    /// <summary>Gets the current number of int entries in the arena.</summary>
    internal int IntCount => _intCount;

    /// <summary>Gets the current number of float entries in the arena.</summary>
    internal int FloatCount => _floatCount;

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

