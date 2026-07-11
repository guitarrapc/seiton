namespace Seiton.Core.Parsing.Ast;

// List facades. `HasValue` distinguishes "key absent in YAML" (default) from
// "present but empty" (Count == 0 with HasValue). Enumeration and Count are
// always safe on default instances.

/// <summary>A list of string scalars (e.g. <c>needs</c>, filter values, labels).</summary>
public readonly struct StringRefList
{
    private readonly AstArena? _arena;
    private readonly StringIdRange _range;

    internal StringRefList(AstArena? arena, StringIdRange range)
    {
        _arena = arena;
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public StringRef this[int index] => new(_arena, _arena!.GetStringIdAt(_range, index));

    public Enumerator GetEnumerator() => new(_arena, _range);

    public struct Enumerator
    {
        private readonly AstArena? _arena;
        private readonly StringIdRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, StringIdRange range)
        {
            _arena = arena;
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly StringRef Current => new(_arena, _arena!.GetStringIdAt(_range, _index));
    }
}

/// <summary>A list of steps (job steps, parallel children, composite action steps).</summary>
public readonly struct StepRefList
{
    private readonly AstArena? _arena;
    private readonly StepIdRange _range;

    internal StepRefList(AstArena? arena, StepIdRange range)
    {
        _arena = arena;
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public StepRef this[int index] => new(_arena, _arena!.GetStepIdAt(_range, index));

    public Enumerator GetEnumerator() => new(_arena, _range);

    public struct Enumerator
    {
        private readonly AstArena? _arena;
        private readonly StepIdRange _range;
        private int _index;

        internal Enumerator(AstArena? arena, StepIdRange range)
        {
            _arena = arena;
            _range = range;
            _index = -1;
        }

        public bool MoveNext() => _arena is not null && ++_index < _range.Count;

        public readonly StepRef Current => new(_arena, _arena!.GetStepIdAt(_range, _index));
    }
}

/// <summary>The list of trigger events in the <c>on:</c> section.</summary>
public readonly struct EventRefList
{
    private readonly AstArena? _arena;
    private readonly NodeRange _range;

    internal EventRefList(AstArena? arena, NodeRange range)
    {
        _arena = arena;
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public EventRef this[int index] => new(_arena, _range.First + index);

    public Enumerator GetEnumerator() => new(_arena, _range);

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

        public readonly EventRef Current => new(_arena, _range.First + _index);
    }
}

/// <summary>The list of cron entries in a <c>schedule:</c> event.</summary>
public readonly struct ScheduleRefList
{
    private readonly AstArena? _arena;
    private readonly NodeRange _range;

    internal ScheduleRefList(AstArena? arena, NodeRange range)
    {
        _arena = arena;
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public ScheduleEntryRef this[int index] => new(_arena, in _arena!.GetScheduleEntryAt(_range, index));

    public Enumerator GetEnumerator() => new(_arena, _range);

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

        public readonly ScheduleEntryRef Current => new(_arena, in _arena!.GetScheduleEntryAt(_range, _index));
    }
}

/// <summary>A list of raw YAML values (matrix row values, array items).</summary>
public readonly struct RawYamlRefList
{
    private readonly AstArena? _arena;
    private readonly NodeRange _range;

    internal RawYamlRefList(AstArena? arena, NodeRange range)
    {
        _arena = arena;
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public RawYamlRef this[int index] => new(_arena, _arena!.GetRawYamlIdAt(_range, index));

    public Enumerator GetEnumerator() => new(_arena, _range);

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

        public readonly RawYamlRef Current => new(_arena, _arena!.GetRawYamlIdAt(_range, _index));
    }
}

/// <summary>A list of matrix <c>include:</c> / <c>exclude:</c> combination blocks.</summary>
public readonly struct CombinationsRefList
{
    private readonly AstArena? _arena;
    private readonly NodeRange _range;

    internal CombinationsRefList(AstArena? arena, NodeRange range)
    {
        _arena = arena;
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public MatrixCombinationsRef this[int index] => new(_arena, in _arena!.GetMatrixCombinationsAt(_range, index));

    public Enumerator GetEnumerator() => new(_arena, _range);

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

        public readonly MatrixCombinationsRef Current => new(_arena, in _arena!.GetMatrixCombinationsAt(_range, _index));
    }
}

/// <summary>The entries of a single matrix combination block (one map per matrix combination).</summary>
public readonly struct CombinationEntryRefList
{
    private readonly AstArena? _arena;
    private readonly NodeRange _range;

    internal CombinationEntryRefList(AstArena? arena, NodeRange range)
    {
        _arena = arena;
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public RawYamlRefMap this[int index] => new(_arena, _arena!.GetCombinationEntryAt(_range, index));

    public Enumerator GetEnumerator() => new(_arena, _range);

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

        public readonly RawYamlRefMap Current => new(_arena, _arena!.GetCombinationEntryAt(_range, _index));
    }
}

/// <summary>The list of inputs declared on a <c>workflow_call</c> event.</summary>
public readonly struct WorkflowCallEventInputRefList
{
    private readonly AstArena? _arena;
    private readonly NodeRange _range;

    internal WorkflowCallEventInputRefList(AstArena? arena, NodeRange range)
    {
        _arena = arena;
        _range = range;
    }

    public bool HasValue => _arena is not null && _range.HasValue;

    public int Count => _range.Count;

    public WorkflowCallEventInputRef this[int index] => new(_arena, in _arena!.GetWorkflowCallEventInputAt(_range, index));

    public Enumerator GetEnumerator() => new(_arena, _range);

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

        public readonly WorkflowCallEventInputRef Current => new(_arena, in _arena!.GetWorkflowCallEventInputAt(_range, _index));
    }
}
