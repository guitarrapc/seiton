using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Flow;

public static partial class WorkflowFlowJson
{
    private const int PermissionStackByteLimit = 512;
    private const int MaxMatrixCombinations = 256;
    private const int RawYamlStackByteLimit = 512;

    private readonly record struct AstMatrixPair(KeyRef Key, RawYamlRef Value);

    private readonly record struct AstMatrixCombination(int Start, int Count);

    private struct AstMatrixBuffer<T>
    {
        private T[]? _items;

        internal AstMatrixBuffer(int initialCapacity)
        {
            _items = ArrayPool<T>.Shared.Rent(Math.Max(1, initialCapacity));
            Count = 0;
        }

        internal int Count { get; private set; }

        internal readonly ReadOnlySpan<T> AsSpan() => _items.AsSpan(0, Count);

        internal readonly T GetAt(int index) => _items![index];

        internal int Add(T item)
        {
            EnsureAdditional(1);
            _items![Count] = item;
            return Count++;
        }

        internal void AddRange(ReadOnlySpan<T> items)
        {
            EnsureAdditional(items.Length);
            items.CopyTo(_items.AsSpan(Count));
            Count += items.Length;
        }

        internal void Replace(int index, T item) => _items![index] = item;

        internal void Truncate(int count) => Count = count;

        internal void EnsureAdditional(int additionalCount)
        {
            var required = checked(Count + additionalCount);
            if (_items is not null && required <= _items.Length)
            {
                return;
            }

            var old = _items;
            var newLength = old is null ? Math.Max(4, required) : Math.Max(required, old.Length * 2);
            _items = ArrayPool<T>.Shared.Rent(newLength);
            if (old is not null)
            {
                old.AsSpan(0, Count).CopyTo(_items);
                ArrayPool<T>.Shared.Return(
                    old,
                    clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            }
        }

        internal void Dispose()
        {
            if (_items is null)
            {
                return;
            }

            ArrayPool<T>.Shared.Return(
                _items,
                clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            _items = null;
            Count = 0;
        }
    }

    /// <summary>Writes one flow document directly from a live UTF-8 AST.</summary>
    public static void Write(IBufferWriter<byte> output, WorkflowRef workflow, string filePath)
    {
        using var writer = new Utf8JsonWriter(output, WriterOptions);
        WriteDocument(writer, workflow, filePath);
        writer.Flush();
    }

    /// <summary>Writes one flow document from a live AST into an active JSON value position.</summary>
    public static void WriteDocument(Utf8JsonWriter writer, WorkflowRef workflow, string filePath)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version"u8, Version);
        writer.WriteStartArray("workflows"u8);
        if (workflow.HasValue)
        {
            WriteAstWorkflowWithGraph(writer, workflow, filePath);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteAstWorkflowWithGraph(Utf8JsonWriter writer, WorkflowRef workflow, string filePath)
    {
        var jobs = workflow.Jobs;
        var wordCount = WorkflowFlowGraph.GetWordCount(jobs.Count);
        var ancestorLength = WorkflowFlowGraph.GetAncestorLength(jobs.Count, wordCount);
        if (ancestorLength <= WorkflowFlowGraph.StackElementLimit
            && jobs.Count <= WorkflowFlowGraph.StackElementLimit)
        {
            Span<ulong> ancestors = stackalloc ulong[ancestorLength];
            Span<byte> initialized = stackalloc byte[jobs.Count];
            WorkflowFlowGraph.BuildAncestors(jobs, ancestors, initialized, wordCount);
            WriteAstWorkflow(writer, workflow, filePath, ancestors, wordCount);
            return;
        }

        var ancestorRent = ArrayPool<ulong>.Shared.Rent(ancestorLength);
        var initializedRent = ArrayPool<byte>.Shared.Rent(jobs.Count);
        try
        {
            var ancestors = ancestorRent.AsSpan(0, ancestorLength);
            var initialized = initializedRent.AsSpan(0, jobs.Count);
            WorkflowFlowGraph.BuildAncestors(jobs, ancestors, initialized, wordCount);
            WriteAstWorkflow(writer, workflow, filePath, ancestors, wordCount);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(ancestorRent);
            ArrayPool<byte>.Shared.Return(initializedRent);
        }
    }

    private static void WriteAstWorkflow(
        Utf8JsonWriter writer,
        WorkflowRef workflow,
        string filePath,
        ReadOnlySpan<ulong> ancestors,
        int wordCount)
    {
        writer.WriteStartObject();
        writer.WriteString("file"u8, filePath);
        if (workflow.Name.HasText)
        {
            writer.WriteString("name"u8, workflow.Name.Value);
        }

        var events = workflow.On;
        writer.WriteStartArray("on"u8);
        for (var i = 0; i < events.Count; i++)
        {
            writer.WriteStringValue(events[i].EventName.Value);
        }
        writer.WriteEndArray();

        var scheduleCount = 0;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Kind == EventKind.Scheduled)
            {
                scheduleCount += events[i].AsScheduled().Schedules.Count;
            }
        }

        if (scheduleCount > 0)
        {
            writer.WriteStartArray("schedules"u8);
            for (var i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.Kind != EventKind.Scheduled)
                {
                    continue;
                }

                foreach (var schedule in evt.AsScheduled().Schedules)
                {
                    writer.WriteStartObject();
                    writer.WriteString("cron"u8, schedule.Cron.Value);
                    if (schedule.Timezone.HasText)
                    {
                        writer.WriteString("timezone"u8, schedule.Timezone.Value);
                    }
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
        }

        var concurrency = workflow.Concurrency;
        if (concurrency.HasValue)
        {
            writer.WriteStartObject("concurrency"u8);
            if (concurrency.Group.HasText)
            {
                writer.WriteString("group"u8, concurrency.Group.Value);
            }
            writer.WriteBoolean(
                "cancelInProgress"u8,
                concurrency.CancelInProgress.HasValue && concurrency.CancelInProgress.Value);
            if (concurrency.Queue.HasText)
            {
                writer.WriteString("queue"u8, concurrency.Queue.Value);
            }
            writer.WriteEndObject();
        }

        var jobs = workflow.Jobs;
        writer.WriteStartArray("jobs"u8);
        for (var i = 0; i < jobs.Count; i++)
        {
            var entry = jobs.GetAt(i);
            WriteAstJob(writer, jobs, entry.Key, entry.Value, ancestors, wordCount);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteAstJob(
        Utf8JsonWriter writer,
        JobRefMap jobs,
        KeyRef key,
        JobRef job,
        ReadOnlySpan<ulong> ancestors,
        int wordCount)
    {
        writer.WriteStartObject();
        writer.WriteString("id"u8, key.Bytes);
        if (job.Name.HasText)
        {
            writer.WriteString("name"u8, job.Name.Value);
        }

        var workflowCall = job.WorkflowCall;
        writer.WriteString("kind"u8, workflowCall.HasValue ? "reusable"u8 : "job"u8);
        var range = job.Range;
        if (range.StartLine > 0)
        {
            writer.WriteNumber("line"u8, range.StartLine);
            writer.WriteNumber("endLine"u8, range.EndLine);
        }

        if (job.If.HasText)
        {
            writer.WriteString("if"u8, job.If.Value);
        }

        var needs = job.Needs;
        writer.WriteStartArray("needs"u8);
        for (var i = 0; i < needs.Count; i++)
        {
            writer.WriteStringValue(needs[i].Value);
        }
        writer.WriteEndArray();

        writer.WriteStartArray("reducedNeeds"u8);
        for (var i = 0; i < needs.Count; i++)
        {
            if (!WorkflowFlowGraph.IsRedundantNeed(jobs, needs, i, ancestors, wordCount))
            {
                writer.WriteStringValue(needs[i].Value);
            }
        }
        writer.WriteEndArray();

        writer.WriteStartArray("runsOn"u8);
        var runner = job.RunsOn;
        if (runner.Labels.Count > 0)
        {
            for (var i = 0; i < runner.Labels.Count; i++)
            {
                writer.WriteStringValue(runner.Labels[i].Value);
            }
        }
        else if (runner.LabelsExpr.HasText)
        {
            writer.WriteStringValue(runner.LabelsExpr.Value);
        }
        else if (runner.Group.HasText)
        {
            writer.WriteStringValue(runner.Group.Value);
        }
        writer.WriteEndArray();

        if (workflowCall.HasValue && workflowCall.Uses.HasText)
        {
            writer.WriteString("uses"u8, workflowCall.Uses.Value);
        }

        if (job.TimeoutMinutes.HasValue && !job.TimeoutMinutes.Expression.HasText)
        {
            writer.WriteNumber("timeoutMinutes"u8, job.TimeoutMinutes.Value);
        }

        WriteAstPermissions(writer, job.Permissions);

        if (job.Environment.Name.HasText)
        {
            writer.WriteString("environment"u8, job.Environment.Name.Value);
        }

        if (job.Strategy.HasValue)
        {
            WriteAstStrategy(writer, job.Strategy);
        }

        var steps = job.Steps;
        writer.WriteStartArray("steps"u8);
        for (var i = 0; i < steps.Count; i++)
        {
            WriteAstStep(writer, steps[i], ComputeAstBackgroundOutcome(steps, i));
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static FlowBackgroundOutcome? ComputeAstBackgroundOutcome(StepRefList steps, int index)
    {
        var step = steps[index];
        if (!step.Background.HasValue || !step.Background.Value)
        {
            return null;
        }

        var id = step.Id;
        for (var i = index + 1; i < steps.Count; i++)
        {
            var exec = steps[i].Exec;
            switch (exec.Kind)
            {
                case StepExecKind.WaitAll:
                    return FlowBackgroundOutcome.Awaited;
                case StepExecKind.Wait when id.HasText:
                    foreach (var target in exec.AsWait().Targets)
                    {
                        if (target.ValueEquals(id.Value))
                        {
                            return FlowBackgroundOutcome.Awaited;
                        }
                    }

                    break;
                case StepExecKind.Cancel when id.HasText:
                    if (exec.AsCancel().Target.ValueEquals(id.Value))
                    {
                        return FlowBackgroundOutcome.Cancelled;
                    }

                    break;
            }
        }

        return FlowBackgroundOutcome.Unawaited;
    }

    private static void WriteAstPermissions(Utf8JsonWriter writer, PermissionsRef permissions)
    {
        if (!permissions.HasValue)
        {
            return;
        }

        writer.WriteStartArray("permissions"u8);
        if (permissions.All.HasText)
        {
            writer.WriteStringValue(permissions.All.Value);
        }
        else
        {
            var scopes = permissions.Scopes;
            for (var i = 0; i < scopes.Count; i++)
            {
                var scope = scopes.GetAt(i);
                WriteAstPermission(writer, scope.Key.Bytes, scope.Value.Value.Value);
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteAstPermission(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value)
    {
        var length = key.Length + 2 + value.Length;
        if (length <= PermissionStackByteLimit)
        {
            Span<byte> buffer = stackalloc byte[PermissionStackByteLimit];
            key.CopyTo(buffer);
            buffer[key.Length] = (byte)':';
            buffer[key.Length + 1] = (byte)' ';
            value.CopyTo(buffer[(key.Length + 2)..]);
            writer.WriteStringValue(buffer[..length]);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var buffer = rented.AsSpan(0, length);
            key.CopyTo(buffer);
            buffer[key.Length] = (byte)':';
            buffer[key.Length + 1] = (byte)' ';
            value.CopyTo(buffer[(key.Length + 2)..]);
            writer.WriteStringValue(buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void WriteAstStrategy(Utf8JsonWriter writer, StrategyRef strategy)
    {
        var matrix = strategy.Matrix;
        writer.WriteStartObject("strategy"u8);
        writer.WriteBoolean("hasMatrix"u8, matrix.HasValue);
        writer.WriteStartArray("matrixKeys"u8);
        if (matrix.HasValue && !matrix.Expression.HasText)
        {
            foreach (var (key, _) in matrix.Rows)
            {
                writer.WriteStringValue(key.Bytes);
            }
        }
        writer.WriteEndArray();
        writer.WriteBoolean("matrixIsExpression"u8, matrix.HasValue && matrix.Expression.HasText);
        writer.WriteStartArray("combinations"u8);
        if (matrix.HasValue && !matrix.Expression.HasText)
        {
            WriteAstMatrixCombinations(writer, matrix);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteAstMatrixCombinations(Utf8JsonWriter writer, MatrixRef matrix)
    {
        var pairs = new AstMatrixBuffer<AstMatrixPair>(16);
        var combinations = new AstMatrixBuffer<AstMatrixCombination>(4);
        try
        {
            if (!TryExpandAstMatrix(matrix, ref pairs, ref combinations))
            {
                return;
            }

            var allPairs = pairs.AsSpan();
            for (var i = 0; i < combinations.Count; i++)
            {
                var combination = combinations.GetAt(i);
                writer.WriteStartObject();
                var combinationPairs = allPairs.Slice(combination.Start, combination.Count);
                for (var pairIndex = 0; pairIndex < combinationPairs.Length; pairIndex++)
                {
                    var pair = combinationPairs[pairIndex];
                    writer.WritePropertyName(pair.Key.Bytes);
                    WriteAstRawYamlStringValue(writer, pair.Value);
                }
                writer.WriteEndObject();
            }
        }
        finally
        {
            pairs.Dispose();
            combinations.Dispose();
        }
    }

    internal static bool TryGetAstMatrixCombinationCount(MatrixRef matrix, out int count)
    {
        if (matrix.Expression.HasText)
        {
            count = 0;
            return false;
        }

        if (matrix.Rows.Count == 0)
        {
            count = 0;
            foreach (var block in matrix.Exclude)
            {
                if (block.Expression.HasText)
                {
                    return false;
                }
            }

            foreach (var block in matrix.Include)
            {
                if (block.Expression.HasText)
                {
                    count = 0;
                    return false;
                }

                count += block.Entries.Count;
                if (count > MaxMatrixCombinations)
                {
                    count = 0;
                    return false;
                }
            }

            return count > 0;
        }

        if (matrix.Exclude.Count == 0 && matrix.Include.Count == 0)
        {
            count = 1;
            foreach (var (_, row) in matrix.Rows)
            {
                if (row.Expression.HasText || row.Values.Count == 0)
                {
                    count = 0;
                    return false;
                }

                if (row.Values.Count > MaxMatrixCombinations / count)
                {
                    count = 0;
                    return false;
                }
                count *= row.Values.Count;
            }

            return true;
        }

        var pairs = new AstMatrixBuffer<AstMatrixPair>(16);
        var combinations = new AstMatrixBuffer<AstMatrixCombination>(4);
        try
        {
            if (!TryExpandAstMatrix(matrix, ref pairs, ref combinations))
            {
                count = 0;
                return false;
            }

            count = combinations.Count;
            return true;
        }
        finally
        {
            pairs.Dispose();
            combinations.Dispose();
        }
    }

    private static bool TryExpandAstMatrix(
        MatrixRef matrix,
        ref AstMatrixBuffer<AstMatrixPair> pairs,
        ref AstMatrixBuffer<AstMatrixCombination> combinations)
    {
        long total = 1;
        foreach (var (key, row) in matrix.Rows)
        {
            if (row.Expression.HasText || row.Values.Count == 0)
            {
                return false;
            }

            total *= row.Values.Count;
            if (total > MaxMatrixCombinations)
            {
                return false;
            }

            if (combinations.Count == 0)
            {
                for (var i = 0; i < row.Values.Count; i++)
                {
                    var start = pairs.Add(new AstMatrixPair(key, row.Values[i]));
                    combinations.Add(new AstMatrixCombination(start, 1));
                }
                continue;
            }

            var nextPairs = new AstMatrixBuffer<AstMatrixPair>(pairs.Count * row.Values.Count);
            var nextCombinations = new AstMatrixBuffer<AstMatrixCombination>(
                combinations.Count * row.Values.Count);
            try
            {
                var existingPairs = pairs.AsSpan();
                for (var comboIndex = 0; comboIndex < combinations.Count; comboIndex++)
                {
                    var combination = combinations.GetAt(comboIndex);
                    var source = existingPairs.Slice(combination.Start, combination.Count);
                    for (var valueIndex = 0; valueIndex < row.Values.Count; valueIndex++)
                    {
                        var start = nextPairs.Count;
                        nextPairs.AddRange(source);
                        nextPairs.Add(new AstMatrixPair(key, row.Values[valueIndex]));
                        nextCombinations.Add(new AstMatrixCombination(start, combination.Count + 1));
                    }
                }

                pairs.Dispose();
                combinations.Dispose();
                pairs = nextPairs;
                combinations = nextCombinations;
                nextPairs = default;
                nextCombinations = default;
            }
            finally
            {
                nextPairs.Dispose();
                nextCombinations.Dispose();
            }
        }

        foreach (var block in matrix.Exclude)
        {
            if (block.Expression.HasText)
            {
                return false;
            }

            foreach (var entry in block.Entries)
            {
                var writeIndex = 0;
                var allPairs = pairs.AsSpan();
                for (var comboIndex = 0; comboIndex < combinations.Count; comboIndex++)
                {
                    var combination = combinations.GetAt(comboIndex);
                    if (!AstMatrixMatchesSubset(
                            allPairs.Slice(combination.Start, combination.Count),
                            entry))
                    {
                        combinations.Replace(writeIndex++, combination);
                    }
                }
                combinations.Truncate(writeIndex);
            }
        }

        foreach (var block in matrix.Include)
        {
            if (block.Expression.HasText)
            {
                return false;
            }

            foreach (var entry in block.Entries)
            {
                if (matrix.Rows.Count == 0)
                {
                    AddAstMatrixEntry(ref pairs, ref combinations, entry);
                    if (combinations.Count > MaxMatrixCombinations)
                    {
                        return false;
                    }
                    continue;
                }

                var matched = false;
                for (var comboIndex = 0; comboIndex < combinations.Count; comboIndex++)
                {
                    var combination = combinations.GetAt(comboIndex);
                    var combinationPairs = pairs.AsSpan().Slice(combination.Start, combination.Count);
                    if (!AstMatrixMatchesDimensions(combinationPairs, entry, matrix.Rows))
                    {
                        continue;
                    }

                    matched = true;
                    ApplyAstMatrixExtras(ref pairs, ref combinations, comboIndex, entry, matrix.Rows);
                }

                if (!matched)
                {
                    AddAstMatrixEntry(ref pairs, ref combinations, entry);
                    if (combinations.Count > MaxMatrixCombinations)
                    {
                        return false;
                    }
                }
            }
        }

        return combinations.Count > 0;
    }

    private static void AddAstMatrixEntry(
        ref AstMatrixBuffer<AstMatrixPair> pairs,
        ref AstMatrixBuffer<AstMatrixCombination> combinations,
        RawYamlRefMap entry)
    {
        var start = pairs.Count;
        foreach (var (key, value) in entry)
        {
            pairs.Add(new AstMatrixPair(key, value));
        }
        combinations.Add(new AstMatrixCombination(start, entry.Count));
    }

    private static bool AstMatrixMatchesSubset(
        ReadOnlySpan<AstMatrixPair> combination,
        RawYamlRefMap subset)
    {
        foreach (var (key, value) in subset)
        {
            var found = false;
            for (var i = 0; i < combination.Length; i++)
            {
                if (!SpanHelpers.EqualsAsciiIgnoreCase(combination[i].Key.Bytes, key.Bytes))
                {
                    continue;
                }

                found = AstRawYamlRenderedEquals(combination[i].Value, value);
                break;
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AstMatrixMatchesDimensions(
        ReadOnlySpan<AstMatrixPair> combination,
        RawYamlRefMap entry,
        MatrixRowRefMap rows)
    {
        foreach (var (key, value) in entry)
        {
            if (!rows.ContainsKey(key.Bytes))
            {
                continue;
            }

            var found = false;
            for (var i = 0; i < combination.Length; i++)
            {
                if (!SpanHelpers.EqualsAsciiIgnoreCase(combination[i].Key.Bytes, key.Bytes))
                {
                    continue;
                }

                found = AstRawYamlRenderedEquals(combination[i].Value, value);
                break;
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyAstMatrixExtras(
        ref AstMatrixBuffer<AstMatrixPair> pairs,
        ref AstMatrixBuffer<AstMatrixCombination> combinations,
        int combinationIndex,
        RawYamlRefMap entry,
        MatrixRowRefMap rows)
    {
        var combination = combinations.GetAt(combinationIndex);
        pairs.EnsureAdditional(combination.Count + entry.Count);
        var source = pairs.AsSpan().Slice(combination.Start, combination.Count);
        var start = pairs.Count;
        pairs.AddRange(source);
        var count = combination.Count;

        foreach (var (key, value) in entry)
        {
            if (rows.ContainsKey(key.Bytes))
            {
                continue;
            }

            var replacementIndex = -1;
            var current = pairs.AsSpan().Slice(start, count);
            for (var i = 0; i < current.Length; i++)
            {
                if (SpanHelpers.EqualsAsciiIgnoreCase(current[i].Key.Bytes, key.Bytes))
                {
                    replacementIndex = start + i;
                    break;
                }
            }

            if (replacementIndex >= 0)
            {
                pairs.Replace(replacementIndex, new AstMatrixPair(key, value));
            }
            else
            {
                pairs.Add(new AstMatrixPair(key, value));
                count++;
            }
        }

        combinations.Replace(combinationIndex, new AstMatrixCombination(start, count));
    }

    private static bool AstRawYamlRenderedEquals(RawYamlRef left, RawYamlRef right)
    {
        if (left.Kind == RawYamlKind.String && right.Kind == RawYamlKind.String)
        {
            return left.Scalar.Value.SequenceEqual(right.Scalar.Value);
        }

        var leftLength = GetAstRawYamlByteCount(left);
        var rightLength = GetAstRawYamlByteCount(right);
        if (leftLength != rightLength)
        {
            return false;
        }

        if (leftLength <= RawYamlStackByteLimit)
        {
            Span<byte> leftBuffer = stackalloc byte[RawYamlStackByteLimit];
            Span<byte> rightBuffer = stackalloc byte[RawYamlStackByteLimit];
            var leftOffset = 0;
            var rightOffset = 0;
            WriteAstRawYaml(left, leftBuffer, ref leftOffset);
            WriteAstRawYaml(right, rightBuffer, ref rightOffset);
            return leftBuffer[..leftLength].SequenceEqual(rightBuffer[..rightLength]);
        }

        var leftRent = ArrayPool<byte>.Shared.Rent(leftLength);
        var rightRent = ArrayPool<byte>.Shared.Rent(rightLength);
        try
        {
            var leftOffset = 0;
            var rightOffset = 0;
            WriteAstRawYaml(left, leftRent.AsSpan(0, leftLength), ref leftOffset);
            WriteAstRawYaml(right, rightRent.AsSpan(0, rightLength), ref rightOffset);
            return leftRent.AsSpan(0, leftLength).SequenceEqual(rightRent.AsSpan(0, rightLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftRent);
            ArrayPool<byte>.Shared.Return(rightRent);
        }
    }

    private static void WriteAstRawYamlStringValue(Utf8JsonWriter writer, RawYamlRef value)
    {
        if (value.Kind == RawYamlKind.String)
        {
            writer.WriteStringValue(value.Scalar.Value);
            return;
        }

        var length = GetAstRawYamlByteCount(value);
        if (length <= RawYamlStackByteLimit)
        {
            Span<byte> buffer = stackalloc byte[RawYamlStackByteLimit];
            var offset = 0;
            WriteAstRawYaml(value, buffer, ref offset);
            writer.WriteStringValue(buffer[..length]);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var offset = 0;
            WriteAstRawYaml(value, rented.AsSpan(0, length), ref offset);
            writer.WriteStringValue(rented.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int GetAstRawYamlByteCount(RawYamlRef value)
    {
        switch (value.Kind)
        {
            case RawYamlKind.String:
                return value.Scalar.Value.Length;
            case RawYamlKind.Array:
                {
                    var length = 2;
                    var items = value.Items;
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (i > 0)
                        {
                            length = checked(length + 2);
                        }
                        length = checked(length + GetAstRawYamlByteCount(items[i]));
                    }
                    return length;
                }
            case RawYamlKind.Object:
                {
                    var length = 2;
                    var index = 0;
                    foreach (var (key, item) in value.Properties)
                    {
                        if (index++ > 0)
                        {
                            length = checked(length + 2);
                        }
                        length = checked(length + key.Bytes.Length + 2 + GetAstRawYamlByteCount(item));
                    }
                    return length;
                }
            default:
                return 0;
        }
    }

    private static void WriteAstRawYaml(RawYamlRef value, Span<byte> destination, ref int offset)
    {
        switch (value.Kind)
        {
            case RawYamlKind.String:
                value.Scalar.Value.CopyTo(destination[offset..]);
                offset += value.Scalar.Value.Length;
                break;
            case RawYamlKind.Array:
                {
                    destination[offset++] = (byte)'[';
                    var items = value.Items;
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (i > 0)
                        {
                            destination[offset++] = (byte)',';
                            destination[offset++] = (byte)' ';
                        }
                        WriteAstRawYaml(items[i], destination, ref offset);
                    }
                    destination[offset++] = (byte)']';
                    break;
                }
            case RawYamlKind.Object:
                {
                    destination[offset++] = (byte)'{';
                    var index = 0;
                    foreach (var (key, item) in value.Properties)
                    {
                        if (index++ > 0)
                        {
                            destination[offset++] = (byte)',';
                            destination[offset++] = (byte)' ';
                        }
                        key.Bytes.CopyTo(destination[offset..]);
                        offset += key.Bytes.Length;
                        destination[offset++] = (byte)':';
                        destination[offset++] = (byte)' ';
                        WriteAstRawYaml(item, destination, ref offset);
                    }
                    destination[offset++] = (byte)'}';
                    break;
                }
        }
    }

    private static void WriteAstStep(
        Utf8JsonWriter writer,
        StepRef step,
        FlowBackgroundOutcome? backgroundOutcome)
    {
        var exec = step.Exec;
        var kind = exec.Kind switch
        {
            StepExecKind.Run => FlowStepKind.Run,
            StepExecKind.Action => FlowStepKind.Uses,
            StepExecKind.Parallel => FlowStepKind.Parallel,
            StepExecKind.Wait => FlowStepKind.Wait,
            StepExecKind.WaitAll => FlowStepKind.WaitAll,
            StepExecKind.Cancel => FlowStepKind.Cancel,
            _ => FlowStepKind.Unknown,
        };

        writer.WriteStartObject();
        writer.WriteString("kind"u8, KindNameUtf8(kind));
        var range = step.Range;
        if (range.StartLine > 0)
        {
            writer.WriteNumber("line"u8, range.StartLine);
            writer.WriteNumber("endLine"u8, range.EndLine);
        }

        WriteAstOptionalString(writer, "id"u8, step.Id);
        WriteAstOptionalString(writer, "name"u8, step.Name);
        WriteAstOptionalString(writer, "if"u8, step.If);

        if (step.Background.HasValue && step.Background.Value)
        {
            writer.WriteBoolean("background"u8, true);
        }

        if (backgroundOutcome is { } outcome)
        {
            writer.WriteString("backgroundOutcome"u8, outcome switch
            {
                FlowBackgroundOutcome.Awaited => "awaited"u8,
                FlowBackgroundOutcome.Cancelled => "cancelled"u8,
                _ => "unawaited"u8,
            });
        }

        if (step.TimeoutMinutes.HasValue && !step.TimeoutMinutes.Expression.HasText)
        {
            writer.WriteNumber("timeoutMinutes"u8, step.TimeoutMinutes.Value);
        }

        if (step.ContinueOnError.HasValue && step.ContinueOnError.Value)
        {
            writer.WriteBoolean("continueOnError"u8, true);
        }

        if (kind == FlowStepKind.Run)
        {
            var run = exec.AsRun();
            WriteAstOptionalString(writer, "run"u8, run.Run);
            WriteAstOptionalString(writer, "workingDirectory"u8, run.WorkingDirectory);
        }
        else if (kind == FlowStepKind.Uses)
        {
            var action = exec.AsAction();
            WriteAstOptionalString(writer, "uses"u8, action.Uses);
            if (action.Inputs.Count > 0)
            {
                writer.WriteStartObject("with"u8);
                foreach (var (key, value) in action.Inputs)
                {
                    writer.WriteString(key.Bytes, value.Value);
                }
                writer.WriteEndObject();
            }
        }

        if (kind == FlowStepKind.Wait)
        {
            writer.WriteStartArray("targets"u8);
            var targets = exec.AsWait().Targets;
            for (var i = 0; i < targets.Count; i++)
            {
                writer.WriteStringValue(targets[i].Value);
            }
            writer.WriteEndArray();
        }

        if (kind == FlowStepKind.Cancel)
        {
            WriteAstOptionalString(writer, "target"u8, exec.AsCancel().Target);
        }

        if (kind == FlowStepKind.Parallel)
        {
            writer.WriteStartArray("steps"u8);
            var children = exec.AsParallel().Steps;
            for (var i = 0; i < children.Count; i++)
            {
                WriteAstStep(writer, children[i], backgroundOutcome: null);
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteAstOptionalString(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> propertyName,
        StringRef value)
    {
        if (value.HasText)
        {
            writer.WriteString(propertyName, value.Value);
        }
    }
}
