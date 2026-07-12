namespace Seiton.Core.Parsing.Ast;

// Map facades over key-embedded arena row tables. Named wrapper types (JobRefMap,
// StringRefMap, ...) are the stable public contract; each wraps (arena, NodeRange)
// over its row table. Rule code should hold the named wrapper and use foreach/var.

/// <summary>The <c>jobs:</c> map of a workflow (case-insensitive keys, row-table backed).</summary>
public readonly struct JobRefMap
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

    private readonly NodeRange _range;

    internal JobRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out JobRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var entry = ref ArenaChecked!.GetJobEntryAt(_range, i);
                if (SpanHelpers.EqualsAsciiIgnoreCase(entry.Key.AsSpan(ArenaChecked!.Source), key))
                {
                    value = new JobRef(_arena, entry.Job);
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

        ref readonly var entry = ref ArenaChecked!.GetJobEntryAt(_range, index);
        return new Entry(new KeyRef(_arena, entry.Key), new JobRef(_arena, entry.Job));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, JobRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public JobRef Value { get; }

        public void Deconstruct(out KeyRef key, out JobRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var entry = ref ArenaChecked!.GetJobEntryAt(_range, _index);
                return new Entry(new KeyRef(_arena, entry.Key), new JobRef(_arena, entry.Job));
            }
        }
    }
}

/// <summary>A job <c>outputs:</c> map (case-insensitive keys, row-table backed; values are string scalars).</summary>
public readonly struct StringRefMap
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

    private readonly NodeRange _range;

    internal StringRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out StringRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var row = ref ArenaChecked!.GetJobOutputAt(_range, i);
                if (SpanHelpers.EqualsAsciiIgnoreCase(row.Key.AsSpan(ArenaChecked!.Source), key))
                {
                    value = new StringRef(_arena, row.Value);
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

        ref readonly var row = ref ArenaChecked!.GetJobOutputAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new StringRef(_arena, row.Value));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, StringRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public StringRef Value { get; }

        public void Deconstruct(out KeyRef key, out StringRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref ArenaChecked!.GetJobOutputAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new StringRef(_arena, row.Value));
            }
        }
    }
}

/// <summary>The <c>with:</c> inputs of an action step (case-insensitive keys, row-table backed).</summary>
public readonly struct ActionInputRefMap
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

    private readonly NodeRange _range;

    internal ActionInputRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out StringRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var row = ref ArenaChecked!.GetActionInputAt(_range, i);
                if (SpanHelpers.EqualsAsciiIgnoreCase(row.Key.AsSpan(ArenaChecked!.Source), key))
                {
                    value = new StringRef(_arena, row.Value);
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

        ref readonly var row = ref ArenaChecked!.GetActionInputAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new StringRef(_arena, row.Value));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, StringRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public StringRef Value { get; }

        public void Deconstruct(out KeyRef key, out StringRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref ArenaChecked!.GetActionInputAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new StringRef(_arena, row.Value));
            }
        }
    }
}

/// <summary>The per-scope entries of a <c>permissions:</c> map (case-sensitive keys, row-table backed).</summary>
public readonly struct PermissionScopeRefMap
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

    private readonly NodeRange _range;

    internal PermissionScopeRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
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
                ref readonly var row = ref ArenaChecked!.GetPermissionScopeAt(_range, i);
                if (row.Key.AsSpan(ArenaChecked!.Source).SequenceEqual(key))
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

        ref readonly var row = ref ArenaChecked!.GetPermissionScopeAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new PermissionScopeRef(_arena, in row));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

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
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref ArenaChecked!.GetPermissionScopeAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new PermissionScopeRef(_arena, in row));
            }
        }
    }
}

/// <summary>The variable entries of an <c>env:</c> map (case-sensitive keys, row-table backed).</summary>
public readonly struct EnvVarRefMap
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

    private readonly NodeRange _range;

    internal EnvVarRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
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
                ref readonly var row = ref ArenaChecked!.GetEnvVarAt(_range, i);
                if (row.Key.AsSpan(ArenaChecked!.Source).SequenceEqual(key))
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

        ref readonly var row = ref ArenaChecked!.GetEnvVarAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new EnvVarRef(_arena, in row));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

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
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref ArenaChecked!.GetEnvVarAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new EnvVarRef(_arena, in row));
            }
        }
    }
}

/// <summary>The row (dimension) entries of a <c>matrix:</c> map (case-insensitive keys, row-table backed).</summary>
public readonly struct MatrixRowRefMap
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

    private readonly NodeRange _range;

    internal MatrixRowRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out MatrixRowRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var row = ref ArenaChecked!.GetMatrixRowAt(_range, i);
                if (SpanHelpers.EqualsAsciiIgnoreCase(row.Key.AsSpan(ArenaChecked!.Source), key))
                {
                    value = new MatrixRowRef(_arena, in row);
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

        ref readonly var row = ref ArenaChecked!.GetMatrixRowAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new MatrixRowRef(_arena, in row));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, MatrixRowRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public MatrixRowRef Value { get; }

        public void Deconstruct(out KeyRef key, out MatrixRowRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref ArenaChecked!.GetMatrixRowAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new MatrixRowRef(_arena, in row));
            }
        }
    }
}

/// <summary>The named service containers of a <c>services:</c> map (case-insensitive keys, row-table backed).</summary>
public readonly struct ServiceRefMap
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

    private readonly NodeRange _range;

    internal ServiceRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out ServiceRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var row = ref ArenaChecked!.GetServiceAt(_range, i);
                if (SpanHelpers.EqualsAsciiIgnoreCase(row.Key.AsSpan(ArenaChecked!.Source), key))
                {
                    value = new ServiceRef(_arena, in row);
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

        ref readonly var row = ref ArenaChecked!.GetServiceAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new ServiceRef(_arena, in row));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, ServiceRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public ServiceRef Value { get; }

        public void Deconstruct(out KeyRef key, out ServiceRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref ArenaChecked!.GetServiceAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new ServiceRef(_arena, in row));
            }
        }
    }
}

/// <summary>The <c>with:</c> inputs of a reusable workflow call (case-insensitive keys, row-table backed).</summary>
public readonly struct WorkflowCallInputRefMap
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

    private readonly NodeRange _range;

    internal WorkflowCallInputRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out WorkflowCallInputRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var row = ref ArenaChecked!.GetWorkflowCallInputAt(_range, i);
                if (SpanHelpers.EqualsAsciiIgnoreCase(row.Key.AsSpan(ArenaChecked!.Source), key))
                {
                    value = new WorkflowCallInputRef(_arena, in row);
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

        ref readonly var row = ref ArenaChecked!.GetWorkflowCallInputAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new WorkflowCallInputRef(_arena, in row));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, WorkflowCallInputRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public WorkflowCallInputRef Value { get; }

        public void Deconstruct(out KeyRef key, out WorkflowCallInputRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref ArenaChecked!.GetWorkflowCallInputAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new WorkflowCallInputRef(_arena, in row));
            }
        }
    }
}

/// <summary>The <c>secrets:</c> entries of a reusable workflow call (case-insensitive keys, row-table backed).</summary>
public readonly struct WorkflowCallSecretRefMap
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

    private readonly NodeRange _range;

    internal WorkflowCallSecretRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out WorkflowCallSecretRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var row = ref ArenaChecked!.GetWorkflowCallSecretAt(_range, i);
                if (SpanHelpers.EqualsAsciiIgnoreCase(row.Key.AsSpan(ArenaChecked!.Source), key))
                {
                    value = new WorkflowCallSecretRef(_arena, in row);
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

        ref readonly var row = ref ArenaChecked!.GetWorkflowCallSecretAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new WorkflowCallSecretRef(_arena, in row));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, WorkflowCallSecretRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public WorkflowCallSecretRef Value { get; }

        public void Deconstruct(out KeyRef key, out WorkflowCallSecretRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref ArenaChecked!.GetWorkflowCallSecretAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new WorkflowCallSecretRef(_arena, in row));
            }
        }
    }
}

/// <summary>The inputs declared on a <c>workflow_dispatch</c> event (case-insensitive keys, row-table backed).</summary>
public readonly struct DispatchInputRefMap
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

    private readonly NodeRange _range;

    internal DispatchInputRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out DispatchInputRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var row = ref ArenaChecked!.GetDispatchInputAt(_range, i);
                if (SpanHelpers.EqualsAsciiIgnoreCase(row.Key.AsSpan(ArenaChecked!.Source), key))
                {
                    value = new DispatchInputRef(_arena, in row);
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

        ref readonly var row = ref ArenaChecked!.GetDispatchInputAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new DispatchInputRef(_arena, in row));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, DispatchInputRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public DispatchInputRef Value { get; }

        public void Deconstruct(out KeyRef key, out DispatchInputRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref ArenaChecked!.GetDispatchInputAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new DispatchInputRef(_arena, in row));
            }
        }
    }
}

/// <summary>The secrets declared on a <c>workflow_call</c> event (case-insensitive keys, row-table backed).</summary>
public readonly struct WorkflowCallEventSecretRefMap
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

    private readonly NodeRange _range;

    internal WorkflowCallEventSecretRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out WorkflowCallEventSecretRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var row = ref ArenaChecked!.GetWorkflowCallEventSecretAt(_range, i);
                if (SpanHelpers.EqualsAsciiIgnoreCase(row.Key.AsSpan(ArenaChecked!.Source), key))
                {
                    value = new WorkflowCallEventSecretRef(_arena, in row);
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

        ref readonly var row = ref ArenaChecked!.GetWorkflowCallEventSecretAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new WorkflowCallEventSecretRef(_arena, in row));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, WorkflowCallEventSecretRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public WorkflowCallEventSecretRef Value { get; }

        public void Deconstruct(out KeyRef key, out WorkflowCallEventSecretRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref ArenaChecked!.GetWorkflowCallEventSecretAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new WorkflowCallEventSecretRef(_arena, in row));
            }
        }
    }
}

/// <summary>The outputs declared on a <c>workflow_call</c> event (case-insensitive keys, row-table backed).</summary>
public readonly struct WorkflowCallEventOutputRefMap
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

    private readonly NodeRange _range;

    internal WorkflowCallEventOutputRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out WorkflowCallEventOutputRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var row = ref ArenaChecked!.GetWorkflowCallEventOutputAt(_range, i);
                if (SpanHelpers.EqualsAsciiIgnoreCase(row.Key.AsSpan(ArenaChecked!.Source), key))
                {
                    value = new WorkflowCallEventOutputRef(_arena, in row);
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

        ref readonly var row = ref ArenaChecked!.GetWorkflowCallEventOutputAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new WorkflowCallEventOutputRef(_arena, in row));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, WorkflowCallEventOutputRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public WorkflowCallEventOutputRef Value { get; }

        public void Deconstruct(out KeyRef key, out WorkflowCallEventOutputRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref ArenaChecked!.GetWorkflowCallEventOutputAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new WorkflowCallEventOutputRef(_arena, in row));
            }
        }
    }
}

/// <summary>The properties of a raw YAML mapping value (case-insensitive keys, row-table backed).</summary>
public readonly struct RawYamlRefMap
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

    private readonly NodeRange _range;

    internal RawYamlRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out RawYamlRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var prop = ref ArenaChecked!.GetRawYamlPropAt(_range, i);
                if (SpanHelpers.EqualsAsciiIgnoreCase(prop.Key.AsSpan(ArenaChecked!.Source), key))
                {
                    value = new RawYamlRef(_arena, prop.Value);
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

        ref readonly var prop = ref ArenaChecked!.GetRawYamlPropAt(_range, index);
        return new Entry(new KeyRef(_arena, prop.Key), new RawYamlRef(_arena, prop.Value));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, RawYamlRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public RawYamlRef Value { get; }

        public void Deconstruct(out KeyRef key, out RawYamlRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var prop = ref ArenaChecked!.GetRawYamlPropAt(_range, _index);
                return new Entry(new KeyRef(_arena, prop.Key), new RawYamlRef(_arena, prop.Value));
            }
        }
    }
}

/// <summary>The <c>inputs:</c> map of action metadata (case-insensitive keys, row-table backed).</summary>
public readonly struct ActionMetadataInputRefMap
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

    private readonly NodeRange _range;

    internal ActionMetadataInputRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out ActionMetadataInputRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var row = ref ArenaChecked!.GetActionMetadataInputAt(_range, i);
                if (SpanHelpers.EqualsAsciiIgnoreCase(row.Key.AsSpan(ArenaChecked!.Source), key))
                {
                    value = new ActionMetadataInputRef(_arena, in row);
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

        ref readonly var row = ref ArenaChecked!.GetActionMetadataInputAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new ActionMetadataInputRef(_arena, in row));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, ActionMetadataInputRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public ActionMetadataInputRef Value { get; }

        public void Deconstruct(out KeyRef key, out ActionMetadataInputRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref ArenaChecked!.GetActionMetadataInputAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new ActionMetadataInputRef(_arena, in row));
            }
        }
    }
}

/// <summary>The <c>outputs:</c> map of action metadata (case-insensitive keys, row-table backed).</summary>
public readonly struct ActionMetadataOutputRefMap
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

    private readonly NodeRange _range;

    internal ActionMetadataOutputRefMap(AstArena? arena, NodeRange range)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, out ActionMetadataOutputRef value)
    {
        if (_arena is not null)
        {
            for (var i = 0; i < _range.Count; i++)
            {
                ref readonly var row = ref ArenaChecked!.GetActionMetadataOutputAt(_range, i);
                if (SpanHelpers.EqualsAsciiIgnoreCase(row.Key.AsSpan(ArenaChecked!.Source), key))
                {
                    value = new ActionMetadataOutputRef(_arena, in row);
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

        ref readonly var row = ref ArenaChecked!.GetActionMetadataOutputAt(_range, index);
        return new Entry(new KeyRef(_arena, row.Key), new ActionMetadataOutputRef(_arena, in row));
    }

    public Enumerator GetEnumerator() => new(ArenaChecked, _range);

    /// <summary>A key-value pair yielded during enumeration.</summary>
    public readonly struct Entry
    {
        internal Entry(KeyRef key, ActionMetadataOutputRef value)
        {
            Key = key;
            Value = value;
        }

        public KeyRef Key { get; }

        public ActionMetadataOutputRef Value { get; }

        public void Deconstruct(out KeyRef key, out ActionMetadataOutputRef value)
        {
            key = Key;
            value = Value;
        }
    }

    public struct Enumerator
    {
        private readonly AstArena? _arena;
#if DEBUG
        private readonly int _generation;
#endif

        private readonly AstArena? ArenaChecked
        {
            get
            {
#if DEBUG
                _arena?.AssertGeneration(_generation);
#endif
                return _arena;
            }
        }

        private readonly NodeRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, NodeRange range)
        {
            _arena = arena;
#if DEBUG
            _generation = arena?.Generation ?? 0;
#endif
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly Entry Current
        {
            get
            {
                ref readonly var row = ref ArenaChecked!.GetActionMetadataOutputAt(_range, _index);
                return new Entry(new KeyRef(_arena, row.Key), new ActionMetadataOutputRef(_arena, in row));
            }
        }
    }
}
