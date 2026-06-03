using System.Buffers.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Parsing;

/// <summary>
/// Builds per-job/workflow dynamic context type overrides for steps, matrix, needs, and inputs contexts.
/// These override the static BuiltinContextTypes entries to enable per-job property-level validation.
/// </summary>
internal static class DynamicContextTypeBuilder
{
    private static readonly ObjectExprType looseDynamic = ExprType.Object(dynamicPropertyType: ExprType.Any);

    // Static byte[] keys to avoid per-job allocation of "steps"u8.ToArray() etc.
    internal static readonly byte[] StepsKeyUtf8 = "steps"u8.ToArray();
    internal static readonly byte[] MatrixKeyUtf8 = "matrix"u8.ToArray();
    internal static readonly byte[] NeedsKeyUtf8 = "needs"u8.ToArray();
    internal static readonly byte[] InputsKeyUtf8 = "inputs"u8.ToArray();
    internal static readonly byte[] SecretsKeyUtf8 = "secrets"u8.ToArray();
    internal static readonly byte[] GithubKeyUtf8 = "github"u8.ToArray();

    // Static Utf8String keys reused across all needs entries
    private static readonly Utf8String resultKey = new("result"u8);
    private static readonly Utf8String outputsKey = new("outputs"u8);

    private static readonly ObjectExprType stepEntryType = ExprType.Object(
        new Dictionary<Utf8String, ExprType>
        {
            { new Utf8String("outcome"u8), ExprType.String },
            { new Utf8String("conclusion"u8), ExprType.String },
            { new Utf8String("outputs"u8), ExprType.Object(dynamicPropertyType: ExprType.String) },
        },
        strict: true);

    /// <summary>
    /// Builds the steps context type override for a job.
    /// Returns a strict object keyed by step IDs, or a loose object when no steps have IDs.
    /// When <paramref name="maxStepIndex"/> is non-negative, only steps with index &lt; maxStepIndex are included
    /// (for detecting forward references to steps defined later).
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildStepsOverride(
        IReadOnlyList<Step>? steps,
        AstArena arena,
        byte[] utf8Yaml,
        int maxStepIndex = -1,
        Func<ReadOnlyMemory<byte>, string[]?>? localActionOutputResolver = null)
    {
        if (steps is null || steps.Count == 0)
        {
            return (StepsKeyUtf8, looseDynamic);
        }

        var props = new Dictionary<Utf8String, ExprType>();
        return BuildStepsOverrideCore(props, steps, arena, utf8Yaml, maxStepIndex, localActionOutputResolver);
    }

    /// <summary>
    /// Same as <see cref="BuildStepsOverride"/> but reuses <paramref name="reusableProps"/> to avoid
    /// per-call dictionary allocation. The dictionary is cleared before use.
    /// Caller must not access the previous <see cref="ObjectExprType"/> that referenced this dictionary
    /// after this call (it will see the new content).
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildStepsOverrideInto(
        Dictionary<Utf8String, ExprType> reusableProps,
        IReadOnlyList<Step>? steps,
        AstArena arena,
        byte[] utf8Yaml,
        int maxStepIndex,
        Func<ReadOnlyMemory<byte>, string[]?>? localActionOutputResolver = null)
    {
        reusableProps.Clear();
        if (steps is null || steps.Count == 0)
        {
            return (StepsKeyUtf8, looseDynamic);
        }
        return BuildStepsOverrideCore(reusableProps, steps, arena, utf8Yaml, maxStepIndex, localActionOutputResolver);
    }

    private static (byte[] NameUtf8, ExprType Type) BuildStepsOverrideCore(
        Dictionary<Utf8String, ExprType> props,
        IReadOnlyList<Step> steps,
        AstArena arena,
        byte[] utf8Yaml,
        int maxStepIndex,
        Func<ReadOnlyMemory<byte>, string[]?>? localActionOutputResolver)
    {
        var limit = maxStepIndex >= 0 ? Math.Min(maxStepIndex, steps.Count) : steps.Count;
        for (var i = 0; i < limit; i++)
        {
            var step = steps[i];
            if (!step.Id.HasValue)
            {
                continue;
            }

            var idSlice = arena.GetStringSlice(step.Id);
            if (idSlice.IsEmpty)
            {
                continue;
            }

            props[idSlice.ToUtf8StringZeroCopy(utf8Yaml)] = BuildStepEntryType(step, arena, utf8Yaml, localActionOutputResolver);
        }

        // When maxStepIndex >= 0, we're doing incremental building (step ordering validation).
        // Return strict type even when empty so forward references are flagged.
        // When maxStepIndex < 0 (default), fall back to loose if no step IDs found.
        return props.Count == 0
            ? (StepsKeyUtf8, maxStepIndex >= 0 ? ExprType.Object(props, strict: true) : looseDynamic)
            : (StepsKeyUtf8, ExprType.Object(props, strict: true));
    }

    /// <summary>
    /// Builds the step entry type. For popular actions with known outputs, returns a strict outputs type.
    /// For local actions, uses the optional resolver to get output names from action metadata.
    /// </summary>
    private static ObjectExprType BuildStepEntryType(
        Step step,
        AstArena arena,
        byte[] utf8Yaml,
        Func<ReadOnlyMemory<byte>, string[]?>? localActionOutputResolver = null)
    {
        if (step.Exec is ExecAction action)
        {
            var usesValue = arena.GetStringValue(action.Uses);
            if (PopularActions.TryGet(usesValue, out var spec))
            {
                var outputNames = spec.GetOutputNames();
                if (outputNames.Length > 0)
                {
                    return BuildStrictStepEntryType(outputNames);
                }
            }

            // Try local action output resolution
            if (localActionOutputResolver is not null && usesValue.Length > 0)
            {
                var usesMemory = utf8Yaml.AsMemory(arena.GetStringSlice(action.Uses).Offset, arena.GetStringSlice(action.Uses).Length);
                var outputNames = localActionOutputResolver(usesMemory);
                if (outputNames is { Length: > 0 })
                {
                    return BuildStrictStepEntryType(outputNames);
                }

                if (outputNames is { Length: 0 })
                {
                    // Action exists but has no outputs — use default strict step type
                    return stepEntryType;
                }
            }
        }

        return stepEntryType;
    }

    private static ObjectExprType BuildStrictStepEntryType(ReadOnlySpan<byte[]> outputNames)
    {
        var outputProps = new Dictionary<Utf8String, ExprType>(outputNames.Length);
        for (var j = 0; j < outputNames.Length; j++)
        {
            outputProps[new Utf8String(outputNames[j])] = ExprType.String;
        }

        return ExprType.Object(
            new Dictionary<Utf8String, ExprType>
            {
                { new Utf8String("outcome"u8), ExprType.String },
                { new Utf8String("conclusion"u8), ExprType.String },
                { outputsKey, ExprType.Object(outputProps, strict: true) },
            },
            strict: true);
    }

    private static ObjectExprType BuildStrictStepEntryType(string[] outputNames)
    {
        var outputProps = new Dictionary<Utf8String, ExprType>(outputNames.Length);
        for (var j = 0; j < outputNames.Length; j++)
        {
            outputProps[new Utf8String(System.Text.Encoding.UTF8.GetBytes(outputNames[j]))] = ExprType.String;
        }

        return ExprType.Object(
            new Dictionary<Utf8String, ExprType>
            {
                { new Utf8String("outcome"u8), ExprType.String },
                { new Utf8String("conclusion"u8), ExprType.String },
                { outputsKey, ExprType.Object(outputProps, strict: true) },
            },
            strict: true);
    }

    /// <summary>
    /// Builds the matrix context type override for a job.
    /// Returns a strict object keyed by matrix row names, or a loose object when no rows are declared.
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildMatrixOverride(Matrix? matrix, AstArena? arena = null, byte[]? utf8Yaml = null)
    {
        if (utf8Yaml is null)
        {
            return (MatrixKeyUtf8, looseDynamic);
        }

        if (matrix is null)
        {
            // Job has no matrix — return strict empty so any `matrix.X` is flagged
            return (MatrixKeyUtf8, ExprType.Object(strict: true));
        }

        if (matrix.Expression.HasValue)
        {
            return (MatrixKeyUtf8, looseDynamic);
        }

        var rows = matrix.Rows;
        var include = matrix.Include;

        // No rows and no include: loose dynamic
        if ((rows is null || rows.Value.Count == 0) && (include is null || include.Count == 0))
        {
            return (MatrixKeyUtf8, looseDynamic);
        }

        var estimatedCapacity = (rows is null ? 0 : rows.Value.Count) + 4; // extra room for include-only keys
        var props = new Dictionary<Utf8String, ExprType>(estimatedCapacity);

        // Add keys from main axes with inferred types
        if (rows is { Count: > 0 })
        {
            foreach (var row in rows)
            {
                props[row.Key.ToUtf8StringZeroCopy(utf8Yaml)] = InferMatrixRowType(row.Value, utf8Yaml, arena);
            }
        }

        // Also add keys that appear only in include: entries (e.g. 'npm' added via include)
        // Infer types from include values when possible (e.g. ${{ fromJSON('null') }} → NullExprType)
        if (include is not null)
        {
            for (var i = 0; i < include.Count; i++)
            {
                var combo = include[i];
                if (combo.Entries is null) continue;
                for (var j = 0; j < combo.Entries.Count; j++)
                {
                    var entry = combo.Entries[j];
                    foreach (var pair in entry)
                    {
                        var key = pair.Key.ToUtf8StringZeroCopy(utf8Yaml);
                        // YAML null keys (bare `null:`) are stored as zero-length slices.
                        // GitHub Actions treats them as the string "null".
                        if (key.Length == 0)
                        {
                            key = new Utf8String("null"u8);
                        }
                        // Don't overwrite existing axes; include-only keys get inferred type
                        if (!props.ContainsKey(key))
                        {
                            props[key] = InferIncludeValueType(pair.Value, utf8Yaml, arena);
                        }
                    }
                }
            }
        }

        return props.Count == 0
            ? (MatrixKeyUtf8, looseDynamic)
            : (MatrixKeyUtf8, ExprType.Object(props, strict: true));
    }

    /// <summary>
    /// Same as <see cref="BuildMatrixOverride"/> but reuses <paramref name="reusableProps"/> to avoid
    /// per-call dictionary allocation. The dictionary is cleared before use.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="ExprType"/> holds a reference to <paramref name="reusableProps"/>
    /// via <c>ExprType.Object(reusableProps, ...)</c>. Because the dictionary is cleared and
    /// repopulated on each call, callers must treat the returned type as ephemeral — valid only
    /// until the next call that reuses the same <paramref name="reusableProps"/> instance.
    /// </remarks>
    internal static (byte[] NameUtf8, ExprType Type) BuildMatrixOverrideInto(
        Dictionary<Utf8String, ExprType> reusableProps,
        Matrix? matrix, AstArena? arena = null, byte[]? utf8Yaml = null)
    {
        reusableProps.Clear();
        if (utf8Yaml is null)
        {
            return (MatrixKeyUtf8, looseDynamic);
        }

        if (matrix is null)
        {
            return (MatrixKeyUtf8, ExprType.Object(reusableProps, strict: true));
        }

        if (matrix.Expression.HasValue)
        {
            return (MatrixKeyUtf8, looseDynamic);
        }

        var rows = matrix.Rows;
        var include = matrix.Include;

        if ((rows is null || rows.Value.Count == 0) && (include is null || include.Count == 0))
        {
            return (MatrixKeyUtf8, looseDynamic);
        }

        if (rows is { Count: > 0 })
        {
            foreach (var row in rows)
            {
                reusableProps[row.Key.ToUtf8StringZeroCopy(utf8Yaml)] = InferMatrixRowType(row.Value, utf8Yaml, arena);
            }
        }

        if (include is not null)
        {
            for (var i = 0; i < include.Count; i++)
            {
                var combo = include[i];
                if (combo.Entries is null) continue;
                for (var j = 0; j < combo.Entries.Count; j++)
                {
                    var entry = combo.Entries[j];
                    foreach (var pair in entry)
                    {
                        var key = pair.Key.ToUtf8StringZeroCopy(utf8Yaml);
                        if (key.Length == 0)
                        {
                            key = new Utf8String("null"u8);
                        }
                        if (!reusableProps.ContainsKey(key))
                        {
                            reusableProps[key] = InferIncludeValueType(pair.Value, utf8Yaml, arena);
                        }
                    }
                }
            }
        }

        return reusableProps.Count == 0
            ? (MatrixKeyUtf8, looseDynamic)
            : (MatrixKeyUtf8, ExprType.Object(reusableProps, strict: true));
    }

    /// <summary>
    /// Infers the type of a matrix row from its values.
    /// When all values are objects with the same key set, returns a strict object type.
    /// When all values are arrays, returns an array type.
    /// Otherwise returns Any.
    /// </summary>
    private static ExprType InferMatrixRowType(MatrixRow row, byte[] utf8Yaml, AstArena? arena = null)
    {
        if (row.Expression.HasValue || row.Values is null || row.Values.Count == 0)
        {
            return ExprType.Any;
        }

        // Classify all values
        var allObjects = true;
        var allArrays = true;
        var allScalars = true;
        for (var i = 0; i < row.Values.Count; i++)
        {
            if (row.Values[i] is not RawYamlObject) allObjects = false;
            if (row.Values[i] is not RawYamlArray) allArrays = false;
            if (row.Values[i] is not RawYamlString) allScalars = false;
        }

        if (allArrays)
        {
            return InferArrayRowElementType(row, utf8Yaml, arena);
        }

        if (allScalars)
        {
            return InferScalarRowType(row, utf8Yaml, arena);
        }

        if (!allObjects)
        {
            return ExprType.Any;
        }

        // All values are objects — build merged property set
        Dictionary<Utf8String, ExprType>? mergedProps = null;
        for (var i = 0; i < row.Values.Count; i++)
        {
            var obj = (RawYamlObject)row.Values[i];

            if (mergedProps is null)
            {
                mergedProps = new Dictionary<Utf8String, ExprType>(obj.Properties.Count);
                foreach (var pair in obj.Properties)
                {
                    mergedProps[pair.Key.ToUtf8StringZeroCopy(utf8Yaml)] = InferRawValueType(pair.Value, utf8Yaml, arena);
                }
            }
            else
            {
                // Merge keys from subsequent objects
                foreach (var pair in obj.Properties)
                {
                    var key = pair.Key.ToUtf8StringZeroCopy(utf8Yaml);
                    if (!mergedProps.ContainsKey(key))
                    {
                        mergedProps[key] = InferRawValueType(pair.Value, utf8Yaml, arena);
                    }
                }
            }
        }

        return mergedProps is not null
            ? ExprType.Object(mergedProps, strict: true)
            : ExprType.Any;
    }

    private static ExprType InferRawValueType(RawYamlValue value, byte[] utf8Yaml, AstArena? arena = null)
    {
        return value switch
        {
            RawYamlString str when arena is not null && str.Value.HasValue => InferRawScalarType(str, arena),
            RawYamlArray arr when arena is not null && arr.Items.Count > 0 => ExprType.ArrayOf(InferRawValueType(arr.Items[0], utf8Yaml, arena)),
            RawYamlObject obj => InferRawObjectType(obj, utf8Yaml, arena),
            _ => ExprType.Any,
        };
    }

    private static ExprType InferRawScalarType(RawYamlString str, AstArena arena)
    {
        var bytes = arena.GetStringValue(str.Value);
        if (bytes.Length == 0) return ExprType.Any;
        if (bytes.SequenceEqual("true"u8) || bytes.SequenceEqual("false"u8))
            return ExprType.Bool;
        if (Utf8Parser.TryParse(bytes, out long _, out var ci) && ci == bytes.Length)
            return ExprType.Number;
        if (Utf8Parser.TryParse(bytes, out double _, out var cf) && cf == bytes.Length)
            return ExprType.Number;
        return ExprType.Any;
    }

    /// <summary>
    /// Infers the type of a matrix include value. For string values containing <c>${{ expr }}</c>,
    /// parses the expression and infers the return type (e.g. <c>fromJSON('null')</c> → NullExprType).
    /// Falls back to <see cref="ExprType.Any"/> when inference is not possible.
    /// </summary>
    private static ExprType InferIncludeValueType(RawYamlValue value, byte[] utf8Yaml, AstArena? arena)
    {
        if (value is RawYamlString str && arena is not null && str.Value.HasValue)
        {
            var scalar = arena.GetStringValue(str.Value);
            var exprType = TryInferExpressionType(scalar);
            if (exprType is not null)
            {
                return exprType;
            }
        }

        return value switch
        {
            RawYamlObject obj => InferRawObjectType(obj, utf8Yaml, arena),
            RawYamlArray arr when arr.Items.Count > 0 => ExprType.ArrayOf(InferRawValueType(arr.Items[0], utf8Yaml, arena)),
            _ => ExprType.Any,
        };
    }

    private static ObjectExprType InferRawObjectType(RawYamlObject obj, byte[] utf8Yaml, AstArena? arena = null)
    {
        var props = new Dictionary<Utf8String, ExprType>(obj.Properties.Count);
        foreach (var pair in obj.Properties)
        {
            props[pair.Key.ToUtf8StringZeroCopy(utf8Yaml)] = InferRawValueType(pair.Value, utf8Yaml, arena);
        }

        return ExprType.Object(props, strict: true);
    }

    /// <summary>
    /// Infers the array element type from array values in a matrix row.
    /// If all arrays have similar structure, infers a specific element type; otherwise falls back to Any.
    /// </summary>
    private static ArrayExprType InferArrayRowElementType(MatrixRow row, byte[] utf8Yaml, AstArena? arena = null)
    {
        // Look at the first array's element types to infer the element type
        ExprType? elementType = null;
        for (var i = 0; i < row.Values!.Count; i++)
        {
            if (row.Values[i] is not RawYamlArray arr || arr.Items is null || arr.Items.Count == 0)
            {
                continue;
            }

            // Use the first item's type as representative
            var firstItemType = InferRawValueType(arr.Items[0], utf8Yaml, arena);
            if (elementType is null)
            {
                elementType = firstItemType;
            }
            else if (elementType.GetType() != firstItemType.GetType())
            {
                // Conflicting element types across arrays — fall back to Any
                return ExprType.EmptyArray;
            }
        }

        return elementType is not null
            ? new ArrayExprType(elementType)
            : ExprType.EmptyArray;
    }

    /// <summary>
    /// Infers the type of a matrix row whose values are all scalars.
    /// If all scalars are pure <c>${{ expr }}</c> expressions with the same inferred type, uses that type.
    /// Otherwise returns String (default scalar type).
    /// </summary>
    private static ExprType InferScalarRowType(MatrixRow row, byte[] utf8Yaml, AstArena? arena)
    {
        ExprType? unifiedType = null;
        var allSameType = true;

        for (var i = 0; i < row.Values!.Count; i++)
        {
            var str = (RawYamlString)row.Values[i];
            if (!str.Value.HasValue || arena is null)
            {
                unifiedType ??= ExprType.String;
                continue;
            }

            var value = arena.GetStringValue(str.Value);

            var exprType = TryInferExpressionType(value);
            if (exprType is null)
            {
                // Not a pure expression — treat as string
                if (unifiedType is not null && unifiedType is not StringExprType)
                {
                    allSameType = false;
                }
                unifiedType ??= ExprType.String;
                continue;
            }

            if (unifiedType is null)
            {
                unifiedType = exprType;
            }
            else if (unifiedType.GetType() != exprType.GetType())
            {
                allSameType = false;
            }
        }

        if (!allSameType || unifiedType is null or AnyExprType)
        {
            return ExprType.Any;
        }

        return unifiedType;
    }

    /// <summary>
    /// If <paramref name="scalar"/> is a pure <c>${{ expr }}</c> expression, parses it and infers the type.
    /// Returns null if not a pure expression.
    /// </summary>
    private static ExprType? TryInferExpressionType(ReadOnlySpan<byte> scalar)
    {
        // Must be a pure expression: ${{ ... }}
        var trimmed = scalar.Trim((byte)' ');
        if (!trimmed.StartsWith("${{"u8) || !trimmed.EndsWith("}}"u8))
        {
            return null;
        }

        // Extract the expression body
        var body = trimmed[3..^2].Trim((byte)' ');
        if (body.IsEmpty)
        {
            return null;
        }

        var parseResult = ExpressionParser.Parse(body);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            return null;
        }

        return ExpressionSemanticAnalyzer.InferType(
            parseResult.RootNode,
            parseResult.Nodes,
            parseResult.Arguments,
            body);
    }

    /// <summary>
    /// Builds the needs context type override for a job.
    /// Returns a strict object keyed by depended-on job IDs, or a strict empty object when needs is empty
    /// (so any <c>needs.X</c> reference is flagged as undefined).
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildNeedsOverride(
        IReadOnlyList<StringNodeId>? needs,
        SliceMap<Job> allJobs,
        AstArena arena,
        byte[]? utf8Yaml,
        Func<ReadOnlyMemory<byte>, string[]?>? localReusableOutputResolver = null)
    {
        if (needs is null || needs.Count == 0)
        {
            // Job has no needs: — return strict empty so any `needs.X` is flagged as undefined
            return (NeedsKeyUtf8, ExprType.Object(strict: true));
        }

        var props = new Dictionary<Utf8String, ExprType>(needs.Count);
        for (var i = 0; i < needs.Count; i++)
        {
            var needSlice = arena.GetStringSlice(needs[i]);
            var needIdBytes = needSlice.AsSpan(utf8Yaml);
            if (needIdBytes.IsEmpty)
            {
                continue;
            }

            var outputsType = FindJobOutputsType(needIdBytes, allJobs, arena, utf8Yaml, localReusableOutputResolver);

            var needsEntryType = ExprType.Object(
                new Dictionary<Utf8String, ExprType>
                {
                    { resultKey, ExprType.String },
                    { outputsKey, outputsType },
                },
                strict: true);

            props[needSlice.ToUtf8StringZeroCopy(utf8Yaml!)] = needsEntryType;
        }

        return props.Count == 0
            ? (NeedsKeyUtf8, looseDynamic)
            : (NeedsKeyUtf8, ExprType.Object(props, strict: true));
    }

    /// <summary>
    /// Same as <see cref="BuildNeedsOverride"/> but reuses <paramref name="reusableProps"/> to avoid
    /// per-call dictionary allocation. The dictionary is cleared before use.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="ExprType"/> holds a reference to <paramref name="reusableProps"/>
    /// via <c>ExprType.Object(reusableProps, ...)</c>. Because the dictionary is cleared and
    /// repopulated on each call, callers must treat the returned type as ephemeral — valid only
    /// until the next call that reuses the same <paramref name="reusableProps"/> instance.
    /// </remarks>
    internal static (byte[] NameUtf8, ExprType Type) BuildNeedsOverrideInto(
        Dictionary<Utf8String, ExprType> reusableProps,
        IReadOnlyList<StringNodeId>? needs,
        SliceMap<Job> allJobs,
        AstArena arena,
        byte[]? utf8Yaml,
        Func<ReadOnlyMemory<byte>, string[]?>? localReusableOutputResolver = null)
    {
        reusableProps.Clear();
        if (needs is null || needs.Count == 0)
        {
            return (NeedsKeyUtf8, ExprType.Object(reusableProps, strict: true));
        }

        for (var i = 0; i < needs.Count; i++)
        {
            var needSlice = arena.GetStringSlice(needs[i]);
            var needIdBytes = needSlice.AsSpan(utf8Yaml);
            if (needIdBytes.IsEmpty)
            {
                continue;
            }

            var outputsType = FindJobOutputsType(needIdBytes, allJobs, arena, utf8Yaml, localReusableOutputResolver);

            var needsEntryType = ExprType.Object(
                new Dictionary<Utf8String, ExprType>
                {
                    { resultKey, ExprType.String },
                    { outputsKey, outputsType },
                },
                strict: true);

            reusableProps[needSlice.ToUtf8StringZeroCopy(utf8Yaml!)] = needsEntryType;
        }

        return reusableProps.Count == 0
            ? (NeedsKeyUtf8, looseDynamic)
            : (NeedsKeyUtf8, ExprType.Object(reusableProps, strict: true));
    }

    private static ExprType FindJobOutputsType(
        ReadOnlySpan<byte> jobIdBytes,
        SliceMap<Job> allJobs,
        AstArena arena,
        byte[]? utf8Yaml,
        Func<ReadOnlyMemory<byte>, string[]?>? localReusableOutputResolver = null)
    {
        if (utf8Yaml is not null)
        {
            foreach (var pair in allJobs)
            {
                if (EqualsAsciiIgnoreCase(pair.Key.AsSpan(utf8Yaml), jobIdBytes))
                {
                    return BuildJobOutputsType(pair.Value, arena, utf8Yaml, localReusableOutputResolver);
                }
            }
        }

        return ExprType.Object(dynamicPropertyType: ExprType.String);
    }

    private static ObjectExprType BuildJobOutputsType(Job job, AstArena? arena, byte[]? utf8Yaml, Func<ReadOnlyMemory<byte>, string[]?>? localReusableOutputResolver = null)
    {
        // Reusable workflow call jobs: outputs come from the called workflow's contract,
        // not from any local outputs: block (which is invalid on a uses: job).
        // Check WorkflowCall first so that an invalid local outputs: block doesn't shadow
        // the called workflow's outputs.
        if (job.WorkflowCall is not null)
        {
            // Try local resolution for local reusable workflow references
            if (localReusableOutputResolver is not null && arena is not null && utf8Yaml is not null && job.WorkflowCall.Uses.HasValue)
            {
                var usesSlice = arena.GetStringSlice(job.WorkflowCall.Uses);
                if (!usesSlice.IsEmpty)
                {
                    var usesMemory = utf8Yaml.AsMemory(usesSlice.Offset, usesSlice.Length);
                    var outputNames = localReusableOutputResolver(usesMemory);
                    if (outputNames is not null)
                    {
                        if (outputNames.Length == 0)
                        {
                            return ExprType.Object(strict: true);
                        }

                        var outputProps = new Dictionary<Utf8String, ExprType>(outputNames.Length);
                        for (var i = 0; i < outputNames.Length; i++)
                        {
                            var encoded = System.Text.Encoding.UTF8.GetBytes(outputNames[i]);
                            outputProps[new Utf8String(encoded.AsMemory())] = ExprType.String;
                        }

                        return ExprType.Object(outputProps, strict: true);
                    }
                }
            }

            // Remote or unresolvable: return loose type so needs.<job>.outputs.* is not flagged.
            return ExprType.Object(dynamicPropertyType: ExprType.String);
        }

        if (job.Outputs is not { Count: > 0 } outputs || utf8Yaml is null)
        {
            // Normal jobs with no outputs — return strict empty so that any outputs.X is flagged as undefined
            return ExprType.Object(strict: true);
        }

        var props = new Dictionary<Utf8String, ExprType>(outputs.Count);
        foreach (var pair in outputs)
        {
            props[pair.Key.ToUtf8StringZeroCopy(utf8Yaml)] = ExprType.String;
        }

        return ExprType.Object(props, strict: true);
    }

    /// <summary>
    /// Builds the inputs context type override for a workflow.
    /// Returns a strict object keyed by input names when workflow_call or workflow_dispatch inputs are defined.
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildInputsOverride(IReadOnlyList<Event> on, byte[]? utf8Yaml = null)
    {
        for (var i = 0; i < on.Count; i++)
        {
            var ev = on[i];
            if (ev is WorkflowCallEvent { Inputs: { Count: > 0 } callInputs })
            {
                return (InputsKeyUtf8, BuildWorkflowCallInputsType(callInputs));
            }

            if (ev is WorkflowDispatchEvent { Inputs: { Count: > 0 } dispatchInputs }
                && utf8Yaml is not null)
            {
                return (InputsKeyUtf8, BuildWorkflowDispatchInputsType(dispatchInputs, utf8Yaml));
            }
        }

        // No inputs defined — return strict empty so that any inputs.X is flagged as undefined.
        return (InputsKeyUtf8, ExprType.Object(strict: true));
    }

    private static ObjectExprType BuildWorkflowCallInputsType(IReadOnlyList<WorkflowCallEventInput> inputs)
    {
        return BuildWorkflowCallInputsTypeUpTo(inputs, inputs.Count);
    }

    /// <summary>
    /// Builds a strict inputs type including only inputs defined before the given index.
    /// Used for incremental validation of input default expressions.
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildWorkflowCallInputsOverrideUpTo(
        IReadOnlyList<WorkflowCallEventInput> inputs, int upToIndex)
    {
        return (InputsKeyUtf8, BuildWorkflowCallInputsTypeUpTo(inputs, upToIndex));
    }

    private static ObjectExprType BuildWorkflowCallInputsTypeUpTo(IReadOnlyList<WorkflowCallEventInput> inputs, int count)
    {
        if (count <= 0)
        {
            return ExprType.Object(strict: true);
        }

        var props = new Dictionary<Utf8String, ExprType>(count);
        for (var i = 0; i < count; i++)
        {
            var input = inputs[i];
            var type = input.Type switch
            {
                WorkflowCallInputType.Boolean => ExprType.Bool,
                WorkflowCallInputType.Number => ExprType.Number,
                WorkflowCallInputType.String => ExprType.String,
                _ => ExprType.String,
            };
            props[input.Id] = type;
        }

        return ExprType.Object(props, strict: true);
    }

    private static ObjectExprType BuildWorkflowDispatchInputsType(
        SliceMap<DispatchInput> inputs,
        byte[] utf8Yaml)
    {
        var props = new Dictionary<Utf8String, ExprType>(inputs.Count);
        foreach (var pair in inputs)
        {
            var type = pair.Value.Type switch
            {
                DispatchInputType.Boolean => ExprType.Bool,
                DispatchInputType.Number => ExprType.Number,
                _ => ExprType.String,
            };
            props[pair.Key.ToUtf8StringZeroCopy(utf8Yaml)] = type;
        }

        return ExprType.Object(props, strict: true);
    }

    /// <summary>
    /// Builds the secrets context type override for a workflow.
    /// Returns a strict object keyed by secret names when workflow_call secrets are defined.
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildSecretsOverride(IReadOnlyList<Event> on, byte[]? utf8Yaml = null)
    {
        for (var i = 0; i < on.Count; i++)
        {
            var ev = on[i];
            if (ev is WorkflowCallEvent { Secrets: not null } wce)
            {
                var secrets = wce.Secrets.Value;
                if (secrets.Count == 0)
                {
                    // Empty secrets: explicitly declared as empty → strict object with only GITHUB_TOKEN
                    return (SecretsKeyUtf8, ExprType.Object(new Dictionary<Utf8String, ExprType>
                    {
                        { new Utf8String("GITHUB_TOKEN"u8), ExprType.String },
                    }, strict: true));
                }

                if (utf8Yaml is not null)
                {
                    var props = new Dictionary<Utf8String, ExprType>(secrets.Count + 1);
                    props[new Utf8String("GITHUB_TOKEN"u8)] = ExprType.String;
                    foreach (var pair in secrets)
                    {
                        props[pair.Key.ToUtf8StringZeroCopy(utf8Yaml)] = ExprType.String;
                    }

                    return (SecretsKeyUtf8, ExprType.Object(props, strict: true));
                }
            }
        }

        return (SecretsKeyUtf8, looseDynamic);
    }

    internal static readonly byte[] JobsKeyUtf8 = "jobs"u8.ToArray();

    /// <summary>
    /// Builds the jobs context type override for workflow_call output validation.
    /// Structure: <c>jobs.&lt;job_id&gt;.result</c> (string) and <c>jobs.&lt;job_id&gt;.outputs.&lt;name&gt;</c>.
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildJobsOverride(
        SliceMap<Job> allJobs,
        byte[]? utf8Yaml)
    {
        if (allJobs.Count == 0 || utf8Yaml is null)
        {
            return (JobsKeyUtf8, looseDynamic);
        }

        var props = new Dictionary<Utf8String, ExprType>(allJobs.Count);
        foreach (var pair in allJobs)
        {
            var outputsType = BuildJobOutputsType(pair.Value, null, utf8Yaml);
            var jobEntryType = ExprType.Object(
                new Dictionary<Utf8String, ExprType>
                {
                    { resultKey, ExprType.String },
                    { outputsKey, outputsType },
                },
                strict: true);
            props[pair.Key.ToUtf8StringZeroCopy(utf8Yaml)] = jobEntryType;
        }

        return (JobsKeyUtf8, ExprType.Object(props, strict: true));
    }

    /// <summary>
    /// Builds a github context type override that narrows <c>github.event</c> to the payload type
    /// of the workflow's trigger event(s). When only one webhook event is declared, the event
    /// property is set to that event's payload type. Otherwise the default loose type is used.
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildGithubOverride(IReadOnlyList<Event> onEvents, AstArena arena, byte[]? utf8Yaml)
    {
        if (utf8Yaml is null)
        {
            return (GithubKeyUtf8, ContextTypes.BuiltinContextTypes[0].Type);
        }

        ObjectExprType? eventPayloadType = null;
        WorkflowDispatchEvent? dispatchEvent = null;

        // Resolve event payload type: use concrete type only when exactly one webhook event is declared
        var webhookCount = 0;
        for (var i = 0; i < onEvents.Count; i++)
        {
            if (onEvents[i] is WebhookEvent we && we.Hook.HasValue)
            {
                webhookCount++;
                var nameUtf8 = arena.GetStringValue(we.Hook);
                if (EventPayloadTypes.TryGetEventPayloadType(nameUtf8, out var payloadType))
                {
                    eventPayloadType = payloadType;
                }
            }
            else if (onEvents[i] is WorkflowDispatchEvent wde)
            {
                dispatchEvent = wde;
            }
        }

        // Multiple webhook events: can't narrow to a single event type
        if (webhookCount != 1)
        {
            eventPayloadType = null;
        }

        // workflow_dispatch: narrow github.event.inputs to declared input names (all string type in event payload)
        if (dispatchEvent is not null && webhookCount == 0)
        {
            if (!EventPayloadTypes.TryGetEventPayloadType("workflow_dispatch"u8, out var basePayloadType))
            {
                return (GithubKeyUtf8, ContextTypes.BuiltinContextTypes[0].Type);
            }

            eventPayloadType = NarrowDispatchInputs(basePayloadType, dispatchEvent, arena, utf8Yaml);
        }

        if (eventPayloadType is null)
        {
            return (GithubKeyUtf8, ContextTypes.BuiltinContextTypes[0].Type);
        }

        // Build a new github type with the narrowed event property
        var builtinGithub = (ObjectExprType)ContextTypes.BuiltinContextTypes[0].Type;
        var newProps = new Dictionary<Utf8String, ExprType>(builtinGithub.Properties!.Count);
        foreach (var kvp in builtinGithub.Properties!)
        {
            newProps[kvp.Key] = kvp.Value;
        }

        // Replace the event property with the narrowed type
        newProps[new Utf8String("event"u8)] = eventPayloadType;
        return (GithubKeyUtf8, ExprType.Object(newProps, strict: true));
    }

    /// <summary>
    /// Narrows workflow_dispatch event payload's <c>inputs</c> property to a strict object
    /// with declared input names, all typed as string (since event payloads deliver inputs as strings).
    /// </summary>
    private static ObjectExprType NarrowDispatchInputs(ObjectExprType basePayloadType, WorkflowDispatchEvent dispatch, AstArena arena, byte[] utf8Yaml)
    {
        if (dispatch.Inputs is not { Count: > 0 })
        {
            return basePayloadType;
        }

        // Build strict inputs object: all input values are string in the event payload
        var inputProps = new Dictionary<Utf8String, ExprType>(dispatch.Inputs.Value.Count);
        foreach (var pair in dispatch.Inputs.Value)
        {
            var inputName = arena.GetStringSlice(pair.Value.Name);
            var nameBytes = utf8Yaml.AsSpan(inputName.Offset, inputName.Length);
            inputProps[new Utf8String(nameBytes)] = ExprType.String;
        }

        var inputsType = ExprType.Object(inputProps, strict: true);

        // Clone the base payload type and replace inputs
        var newPayloadProps = new Dictionary<Utf8String, ExprType>(basePayloadType.Properties!.Count);
        foreach (var kvp in basePayloadType.Properties!)
        {
            newPayloadProps[kvp.Key] = kvp.Value;
        }
        newPayloadProps[new Utf8String("inputs"u8)] = inputsType;

        return ExprType.Object(newPayloadProps, dynamicPropertyType: ExprType.Any);
    }
}
