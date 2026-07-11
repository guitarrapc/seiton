namespace Seiton.Core.Parsing.Ast;

// List facades. `HasValue` distinguishes "key absent in YAML" (default) from
// "present but empty" (Count == 0 with HasValue). Enumeration and Count are
// always safe on default instances.

/// <summary>A list of string scalars (e.g. <c>needs</c>, filter values, labels).</summary>
public readonly struct StringRefList
{
    private readonly AstArena? _arena;
    private readonly IReadOnlyList<StringNodeId>? _nodes;

    internal StringRefList(AstArena? arena, IReadOnlyList<StringNodeId>? nodes)
    {
        _arena = arena;
        _nodes = nodes;
    }

    public bool HasValue => _nodes is not null;

    public int Count => _nodes?.Count ?? 0;

    public StringRef this[int index] => new(_arena, _nodes![index]);

    public Enumerator GetEnumerator() => new(_arena, _nodes);

    public struct Enumerator
    {
        private readonly AstArena? _arena;
        private readonly IReadOnlyList<StringNodeId>? _nodes;
        private int _index;

        internal Enumerator(AstArena? arena, IReadOnlyList<StringNodeId>? nodes)
        {
            _arena = arena;
            _nodes = nodes;
            _index = -1;
        }

        public bool MoveNext() => _nodes is not null && ++_index < _nodes.Count;

        public readonly StringRef Current => new(_arena, _nodes![_index]);
    }
}

/// <summary>A list of steps (job steps, parallel children, composite action steps).</summary>
public readonly struct StepRefList
{
    private readonly AstArena? _arena;
    private readonly IReadOnlyList<Step>? _nodes;

    internal StepRefList(AstArena? arena, IReadOnlyList<Step>? nodes)
    {
        _arena = arena;
        _nodes = nodes;
    }

    public bool HasValue => _nodes is not null;

    public int Count => _nodes?.Count ?? 0;

    public StepRef this[int index] => new(_arena, _nodes![index]);

    public Enumerator GetEnumerator() => new(_arena, _nodes);

    public struct Enumerator
    {
        private readonly AstArena? _arena;
        private readonly IReadOnlyList<Step>? _nodes;
        private int _index;

        internal Enumerator(AstArena? arena, IReadOnlyList<Step>? nodes)
        {
            _arena = arena;
            _nodes = nodes;
            _index = -1;
        }

        public bool MoveNext() => _nodes is not null && ++_index < _nodes.Count;

        public readonly StepRef Current => new(_arena, _nodes![_index]);
    }
}

/// <summary>The list of trigger events in the <c>on:</c> section.</summary>
public readonly struct EventRefList
{
    private readonly AstArena? _arena;
    private readonly IReadOnlyList<Event>? _nodes;

    internal EventRefList(AstArena? arena, IReadOnlyList<Event>? nodes)
    {
        _arena = arena;
        _nodes = nodes;
    }

    public bool HasValue => _nodes is not null;

    public int Count => _nodes?.Count ?? 0;

    public EventRef this[int index] => new(_arena, _nodes![index]);

    public Enumerator GetEnumerator() => new(_arena, _nodes);

    public struct Enumerator
    {
        private readonly AstArena? _arena;
        private readonly IReadOnlyList<Event>? _nodes;
        private int _index;

        internal Enumerator(AstArena? arena, IReadOnlyList<Event>? nodes)
        {
            _arena = arena;
            _nodes = nodes;
            _index = -1;
        }

        public bool MoveNext() => _nodes is not null && ++_index < _nodes.Count;

        public readonly EventRef Current => new(_arena, _nodes![_index]);
    }
}

/// <summary>The list of cron entries in a <c>schedule:</c> event.</summary>
public readonly struct ScheduleRefList
{
    private readonly AstArena? _arena;
    private readonly IReadOnlyList<ScheduleEntry>? _nodes;

    internal ScheduleRefList(AstArena? arena, IReadOnlyList<ScheduleEntry>? nodes)
    {
        _arena = arena;
        _nodes = nodes;
    }

    public bool HasValue => _nodes is not null;

    public int Count => _nodes?.Count ?? 0;

    public ScheduleEntryRef this[int index] => new(_arena, _nodes![index]);

    public Enumerator GetEnumerator() => new(_arena, _nodes);

    public struct Enumerator
    {
        private readonly AstArena? _arena;
        private readonly IReadOnlyList<ScheduleEntry>? _nodes;
        private int _index;

        internal Enumerator(AstArena? arena, IReadOnlyList<ScheduleEntry>? nodes)
        {
            _arena = arena;
            _nodes = nodes;
            _index = -1;
        }

        public bool MoveNext() => _nodes is not null && ++_index < _nodes.Count;

        public readonly ScheduleEntryRef Current => new(_arena, _nodes![_index]);
    }
}

/// <summary>A list of raw YAML values (matrix row values, array items).</summary>
public readonly struct RawYamlRefList
{
    private readonly AstArena? _arena;
    private readonly IReadOnlyList<RawYamlValue>? _nodes;

    internal RawYamlRefList(AstArena? arena, IReadOnlyList<RawYamlValue>? nodes)
    {
        _arena = arena;
        _nodes = nodes;
    }

    public bool HasValue => _nodes is not null;

    public int Count => _nodes?.Count ?? 0;

    public RawYamlRef this[int index] => new(_arena, _nodes![index]);

    public Enumerator GetEnumerator() => new(_arena, _nodes);

    public struct Enumerator
    {
        private readonly AstArena? _arena;
        private readonly IReadOnlyList<RawYamlValue>? _nodes;
        private int _index;

        internal Enumerator(AstArena? arena, IReadOnlyList<RawYamlValue>? nodes)
        {
            _arena = arena;
            _nodes = nodes;
            _index = -1;
        }

        public bool MoveNext() => _nodes is not null && ++_index < _nodes.Count;

        public readonly RawYamlRef Current => new(_arena, _nodes![_index]);
    }
}

/// <summary>A list of matrix <c>include:</c> / <c>exclude:</c> combination blocks.</summary>
public readonly struct CombinationsRefList
{
    private readonly AstArena? _arena;
    private readonly IReadOnlyList<MatrixCombinations>? _nodes;

    internal CombinationsRefList(AstArena? arena, IReadOnlyList<MatrixCombinations>? nodes)
    {
        _arena = arena;
        _nodes = nodes;
    }

    public bool HasValue => _nodes is not null;

    public int Count => _nodes?.Count ?? 0;

    public MatrixCombinationsRef this[int index] => new(_arena, _nodes![index]);

    public Enumerator GetEnumerator() => new(_arena, _nodes);

    public struct Enumerator
    {
        private readonly AstArena? _arena;
        private readonly IReadOnlyList<MatrixCombinations>? _nodes;
        private int _index;

        internal Enumerator(AstArena? arena, IReadOnlyList<MatrixCombinations>? nodes)
        {
            _arena = arena;
            _nodes = nodes;
            _index = -1;
        }

        public bool MoveNext() => _nodes is not null && ++_index < _nodes.Count;

        public readonly MatrixCombinationsRef Current => new(_arena, _nodes![_index]);
    }
}

/// <summary>The entries of a single matrix combination block (one map per matrix combination).</summary>
public readonly struct CombinationEntryRefList
{
    private readonly AstArena? _arena;
    private readonly IReadOnlyList<SliceMap<RawYamlValue>>? _nodes;

    internal CombinationEntryRefList(AstArena? arena, IReadOnlyList<SliceMap<RawYamlValue>>? nodes)
    {
        _arena = arena;
        _nodes = nodes;
    }

    public bool HasValue => _nodes is not null;

    public int Count => _nodes?.Count ?? 0;

    public RawYamlRefMap this[int index] => new(_arena, _nodes![index]);

    public Enumerator GetEnumerator() => new(_arena, _nodes);

    public struct Enumerator
    {
        private readonly AstArena? _arena;
        private readonly IReadOnlyList<SliceMap<RawYamlValue>>? _nodes;
        private int _index;

        internal Enumerator(AstArena? arena, IReadOnlyList<SliceMap<RawYamlValue>>? nodes)
        {
            _arena = arena;
            _nodes = nodes;
            _index = -1;
        }

        public bool MoveNext() => _nodes is not null && ++_index < _nodes.Count;

        public readonly RawYamlRefMap Current => new(_arena, _nodes![_index]);
    }
}

/// <summary>The list of inputs declared on a <c>workflow_call</c> event.</summary>
public readonly struct WorkflowCallEventInputRefList
{
    private readonly AstArena? _arena;
    private readonly IReadOnlyList<WorkflowCallEventInput>? _nodes;

    internal WorkflowCallEventInputRefList(AstArena? arena, IReadOnlyList<WorkflowCallEventInput>? nodes)
    {
        _arena = arena;
        _nodes = nodes;
    }

    public bool HasValue => _nodes is not null;

    public int Count => _nodes?.Count ?? 0;

    public WorkflowCallEventInputRef this[int index] => new(_arena, _nodes![index]);

    public Enumerator GetEnumerator() => new(_arena, _nodes);

    public struct Enumerator
    {
        private readonly AstArena? _arena;
        private readonly IReadOnlyList<WorkflowCallEventInput>? _nodes;
        private int _index;

        internal Enumerator(AstArena? arena, IReadOnlyList<WorkflowCallEventInput>? nodes)
        {
            _arena = arena;
            _nodes = nodes;
            _index = -1;
        }

        public bool MoveNext() => _nodes is not null && ++_index < _nodes.Count;

        public readonly WorkflowCallEventInputRef Current => new(_arena, _nodes![_index]);
    }
}
