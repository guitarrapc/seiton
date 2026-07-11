namespace Seiton.Core.Parsing.Ast;

// Map facades over SliceMap<TNode>. Named wrapper types (JobRefMap, StringRefMap, ...)
// are the stable public contract; RefMap<TNode, TRef> is shared plumbing whose type
// arguments change in Stage 2 (storage swap). Rule code should never name
// RefMap<,> explicitly — hold the named wrapper and use foreach/var.

/// <summary>Factory contract used by <see cref="RefMap{TNode, TRef}"/> to materialize refs from stored nodes.</summary>
public interface INodeRef<TNode, TSelf> where TSelf : struct
{
    internal static abstract TSelf Create(AstArena? arena, TNode node);
}

/// <summary>Shared implementation for keyed map facades. Do not name this type in rule code.</summary>
public readonly struct RefMap<TNode, TRef> where TRef : struct, INodeRef<TNode, TRef>
{
    private readonly AstArena? _arena;
    private readonly SliceMap<TNode> _map;
    private readonly bool _hasValue;

    internal RefMap(AstArena? arena, SliceMap<TNode>? map)
    {
        _arena = arena;
        _map = map ?? default;
        _hasValue = map.HasValue && arena is not null;
    }

    public bool HasValue => _hasValue;

    public int Count => _hasValue ? _map.Count : 0;

    public bool TryGetValue(ReadOnlySpan<byte> key, out TRef value)
    {
        if (_hasValue && _map.TryGetValue(_arena!.Source, key, out var node))
        {
            value = TRef.Create(_arena, node);
            return true;
        }

        value = default;
        return false;
    }

    public bool ContainsKey(ReadOnlySpan<byte> key) => TryGetValue(key, out _);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public Entry GetAt(int index)
    {
        if (!_hasValue || (uint)index >= (uint)_map.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        ref readonly var entry = ref _map.Entries[index];
        return new Entry(new KeyRef(_arena, entry.Key), TRef.Create(_arena, entry.Value));
    }

    public Enumerator GetEnumerator() => new(_arena, _hasValue ? _map : default);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, TRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public TRef Value { get; }

        public void Deconstruct(out KeyRef key, out TRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
        private SliceMap<TNode>.Enumerator _inner;

        internal Enumerator(AstArena? arena, SliceMap<TNode> map)
        {
            _arena = arena;
            _inner = map.GetEnumerator();
        }

        public bool MoveNext() => _inner.MoveNext();

        public readonly Entry Current
        {
            get
            {
                ref readonly var entry = ref _inner.Current;
                return new Entry(new KeyRef(_arena, entry.Key), TRef.Create(_arena, entry.Value));
            }
        }
    }
}

/// <summary>The <c>jobs:</c> map of a workflow.</summary>
public readonly struct JobRefMap
{
    private readonly RefMap<Job, JobRef> _core;

    internal JobRefMap(AstArena? arena, SliceMap<Job>? map) => _core = new(arena, map);

    public bool HasValue => _core.HasValue;

    public int Count => _core.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out JobRef value) => _core.TryGetValue(key, out value);

    public bool ContainsKey(ReadOnlySpan<byte> key) => _core.ContainsKey(key);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public RefMap<Job, JobRef>.Entry GetAt(int index) => _core.GetAt(index);

    public RefMap<Job, JobRef>.Enumerator GetEnumerator() => _core.GetEnumerator();
}

/// <summary>A map whose values are string scalars (e.g. job <c>outputs</c>, action <c>with:</c> inputs).</summary>
public readonly struct StringRefMap
{
    private readonly RefMap<StringNodeId, StringRef> _core;

    internal StringRefMap(AstArena? arena, SliceMap<StringNodeId>? map) => _core = new(arena, map);

    public bool HasValue => _core.HasValue;

    public int Count => _core.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out StringRef value) => _core.TryGetValue(key, out value);

    public bool ContainsKey(ReadOnlySpan<byte> key) => _core.ContainsKey(key);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public RefMap<StringNodeId, StringRef>.Entry GetAt(int index) => _core.GetAt(index);

    public RefMap<StringNodeId, StringRef>.Enumerator GetEnumerator() => _core.GetEnumerator();
}

/// <summary>The per-scope entries of a <c>permissions:</c> map (case-sensitive keys, row-table backed).</summary>
public readonly struct PermissionScopeRefMap
{
    private readonly AstArena? _arena;
    private readonly NodeRange _range;

    internal PermissionScopeRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out PermissionScopeRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var row = ref _arena.GetPermissionScopeAt(_range, i);
                if (row.Key.AsSpan(_arena.Source).SequenceEqual(key))
                {
                    value = new PermissionScopeRef(_arena, in row);
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public bool ContainsKey(ReadOnlySpan<byte> key) => TryGetValue(key, out _);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public Entry GetAt(int index)
    {
        if (_arena is null || (uint)index >= (uint)_range.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        ref readonly var row = ref _arena.GetPermissionScopeAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new PermissionScopeRef(_arena, in row));
    }

    public Enumerator GetEnumerator() => new(_arena, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, PermissionScopeRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public PermissionScopeRef Value { get; }

        public void Deconstruct(out KeyRef key, out PermissionScopeRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref _arena!.GetPermissionScopeAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new PermissionScopeRef(_arena, in row));
            }
        }
    }
}

/// <summary>The variable entries of an <c>env:</c> map (case-sensitive keys, row-table backed).</summary>
public readonly struct EnvVarRefMap
{
    private readonly AstArena? _arena;
    private readonly NodeRange _range;

    internal EnvVarRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out EnvVarRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var row = ref _arena.GetEnvVarAt(_range, i);
                if (row.Key.AsSpan(_arena.Source).SequenceEqual(key))
                {
                    value = new EnvVarRef(_arena, in row);
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public bool ContainsKey(ReadOnlySpan<byte> key) => TryGetValue(key, out _);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public Entry GetAt(int index)
    {
        if (_arena is null || (uint)index >= (uint)_range.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        ref readonly var row = ref _arena.GetEnvVarAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new EnvVarRef(_arena, in row));
    }

    public Enumerator GetEnumerator() => new(_arena, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, EnvVarRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public EnvVarRef Value { get; }

        public void Deconstruct(out KeyRef key, out EnvVarRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref _arena!.GetEnvVarAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new EnvVarRef(_arena, in row));
            }
        }
    }
}

/// <summary>The row (dimension) entries of a <c>matrix:</c> map.</summary>
public readonly struct MatrixRowRefMap
{
    private readonly RefMap<MatrixRow, MatrixRowRef> _core;

    internal MatrixRowRefMap(AstArena? arena, SliceMap<MatrixRow>? map) => _core = new(arena, map);

    public bool HasValue => _core.HasValue;

    public int Count => _core.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out MatrixRowRef value) => _core.TryGetValue(key, out value);

    public bool ContainsKey(ReadOnlySpan<byte> key) => _core.ContainsKey(key);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public RefMap<MatrixRow, MatrixRowRef>.Entry GetAt(int index) => _core.GetAt(index);

    public RefMap<MatrixRow, MatrixRowRef>.Enumerator GetEnumerator() => _core.GetEnumerator();
}

/// <summary>The named service containers of a <c>services:</c> map.</summary>
public readonly struct ServiceRefMap
{
    private readonly RefMap<Service, ServiceRef> _core;

    internal ServiceRefMap(AstArena? arena, SliceMap<Service>? map) => _core = new(arena, map);

    public bool HasValue => _core.HasValue;

    public int Count => _core.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out ServiceRef value) => _core.TryGetValue(key, out value);

    public bool ContainsKey(ReadOnlySpan<byte> key) => _core.ContainsKey(key);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public RefMap<Service, ServiceRef>.Entry GetAt(int index) => _core.GetAt(index);

    public RefMap<Service, ServiceRef>.Enumerator GetEnumerator() => _core.GetEnumerator();
}

/// <summary>The <c>with:</c> inputs of a reusable workflow call.</summary>
public readonly struct WorkflowCallInputRefMap
{
    private readonly RefMap<WorkflowCallInput, WorkflowCallInputRef> _core;

    internal WorkflowCallInputRefMap(AstArena? arena, SliceMap<WorkflowCallInput>? map) => _core = new(arena, map);

    public bool HasValue => _core.HasValue;

    public int Count => _core.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out WorkflowCallInputRef value) => _core.TryGetValue(key, out value);

    public bool ContainsKey(ReadOnlySpan<byte> key) => _core.ContainsKey(key);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public RefMap<WorkflowCallInput, WorkflowCallInputRef>.Entry GetAt(int index) => _core.GetAt(index);

    public RefMap<WorkflowCallInput, WorkflowCallInputRef>.Enumerator GetEnumerator() => _core.GetEnumerator();
}

/// <summary>The <c>secrets:</c> entries of a reusable workflow call.</summary>
public readonly struct WorkflowCallSecretRefMap
{
    private readonly RefMap<WorkflowCallSecret, WorkflowCallSecretRef> _core;

    internal WorkflowCallSecretRefMap(AstArena? arena, SliceMap<WorkflowCallSecret>? map) => _core = new(arena, map);

    public bool HasValue => _core.HasValue;

    public int Count => _core.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out WorkflowCallSecretRef value) => _core.TryGetValue(key, out value);

    public bool ContainsKey(ReadOnlySpan<byte> key) => _core.ContainsKey(key);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public RefMap<WorkflowCallSecret, WorkflowCallSecretRef>.Entry GetAt(int index) => _core.GetAt(index);

    public RefMap<WorkflowCallSecret, WorkflowCallSecretRef>.Enumerator GetEnumerator() => _core.GetEnumerator();
}

/// <summary>The inputs declared on a <c>workflow_dispatch</c> event.</summary>
public readonly struct DispatchInputRefMap
{
    private readonly RefMap<DispatchInput, DispatchInputRef> _core;

    internal DispatchInputRefMap(AstArena? arena, SliceMap<DispatchInput>? map) => _core = new(arena, map);

    public bool HasValue => _core.HasValue;

    public int Count => _core.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out DispatchInputRef value) => _core.TryGetValue(key, out value);

    public bool ContainsKey(ReadOnlySpan<byte> key) => _core.ContainsKey(key);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public RefMap<DispatchInput, DispatchInputRef>.Entry GetAt(int index) => _core.GetAt(index);

    public RefMap<DispatchInput, DispatchInputRef>.Enumerator GetEnumerator() => _core.GetEnumerator();
}

/// <summary>The secrets declared on a <c>workflow_call</c> event.</summary>
public readonly struct WorkflowCallEventSecretRefMap
{
    private readonly RefMap<WorkflowCallEventSecret, WorkflowCallEventSecretRef> _core;

    internal WorkflowCallEventSecretRefMap(AstArena? arena, SliceMap<WorkflowCallEventSecret>? map) => _core = new(arena, map);

    public bool HasValue => _core.HasValue;

    public int Count => _core.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out WorkflowCallEventSecretRef value) => _core.TryGetValue(key, out value);

    public bool ContainsKey(ReadOnlySpan<byte> key) => _core.ContainsKey(key);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public RefMap<WorkflowCallEventSecret, WorkflowCallEventSecretRef>.Entry GetAt(int index) => _core.GetAt(index);

    public RefMap<WorkflowCallEventSecret, WorkflowCallEventSecretRef>.Enumerator GetEnumerator() => _core.GetEnumerator();
}

/// <summary>The outputs declared on a <c>workflow_call</c> event.</summary>
public readonly struct WorkflowCallEventOutputRefMap
{
    private readonly RefMap<WorkflowCallEventOutput, WorkflowCallEventOutputRef> _core;

    internal WorkflowCallEventOutputRefMap(AstArena? arena, SliceMap<WorkflowCallEventOutput>? map) => _core = new(arena, map);

    public bool HasValue => _core.HasValue;

    public int Count => _core.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out WorkflowCallEventOutputRef value) => _core.TryGetValue(key, out value);

    public bool ContainsKey(ReadOnlySpan<byte> key) => _core.ContainsKey(key);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public RefMap<WorkflowCallEventOutput, WorkflowCallEventOutputRef>.Entry GetAt(int index) => _core.GetAt(index);

    public RefMap<WorkflowCallEventOutput, WorkflowCallEventOutputRef>.Enumerator GetEnumerator() => _core.GetEnumerator();
}

/// <summary>The properties of a raw YAML mapping value.</summary>
public readonly struct RawYamlRefMap
{
    private readonly RefMap<RawYamlValue, RawYamlRef> _core;

    internal RawYamlRefMap(AstArena? arena, SliceMap<RawYamlValue>? map) => _core = new(arena, map);

    public bool HasValue => _core.HasValue;

    public int Count => _core.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out RawYamlRef value) => _core.TryGetValue(key, out value);

    public bool ContainsKey(ReadOnlySpan<byte> key) => _core.ContainsKey(key);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public RefMap<RawYamlValue, RawYamlRef>.Entry GetAt(int index) => _core.GetAt(index);

    public RefMap<RawYamlValue, RawYamlRef>.Enumerator GetEnumerator() => _core.GetEnumerator();
}

/// <summary>The <c>inputs:</c> map of action metadata.</summary>
public readonly struct ActionMetadataInputRefMap
{
    private readonly RefMap<ActionMetadataInput, ActionMetadataInputRef> _core;

    internal ActionMetadataInputRefMap(AstArena? arena, SliceMap<ActionMetadataInput>? map) => _core = new(arena, map);

    public bool HasValue => _core.HasValue;

    public int Count => _core.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out ActionMetadataInputRef value) => _core.TryGetValue(key, out value);

    public bool ContainsKey(ReadOnlySpan<byte> key) => _core.ContainsKey(key);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public RefMap<ActionMetadataInput, ActionMetadataInputRef>.Entry GetAt(int index) => _core.GetAt(index);

    public RefMap<ActionMetadataInput, ActionMetadataInputRef>.Enumerator GetEnumerator() => _core.GetEnumerator();
}

/// <summary>The <c>outputs:</c> map of action metadata.</summary>
public readonly struct ActionMetadataOutputRefMap
{
    private readonly RefMap<ActionMetadataOutput, ActionMetadataOutputRef> _core;

    internal ActionMetadataOutputRefMap(AstArena? arena, SliceMap<ActionMetadataOutput>? map) => _core = new(arena, map);

    public bool HasValue => _core.HasValue;

    public int Count => _core.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out ActionMetadataOutputRef value) => _core.TryGetValue(key, out value);

    public bool ContainsKey(ReadOnlySpan<byte> key) => _core.ContainsKey(key);

    /// <summary>Returns the entry at the given document-order index.</summary>
    public RefMap<ActionMetadataOutput, ActionMetadataOutputRef>.Entry GetAt(int index) => _core.GetAt(index);

    public RefMap<ActionMetadataOutput, ActionMetadataOutputRef>.Enumerator GetEnumerator() => _core.GetEnumerator();
}
