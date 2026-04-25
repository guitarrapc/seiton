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
                props[row.Key.ToUtf8StringZeroCopy(utf8Yaml)] = InferMatrixRowType(row.Value, utf8Yaml);
            }
        }

        // Also add keys that appear only in include: entries (e.g. 'npm' added via include)
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
                        // Don't overwrite existing axes; include-only keys get Any type
                        if (!props.ContainsKey(key))
                        {
                            props[key] = ExprType.Any;
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
    /// Infers the type of a matrix row from its values.
    /// When all values are objects with the same key set, returns a strict object type.
    /// When all values are arrays, returns an array type.
    /// Otherwise returns Any.
    /// </summary>
    private static ExprType InferMatrixRowType(MatrixRow row, byte[] utf8Yaml)
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
            return ExprType.EmptyArray;
        }

        if (allScalars)
        {
            return ExprType.String;
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
                    mergedProps[pair.Key.ToUtf8StringZeroCopy(utf8Yaml)] = InferRawValueType(pair.Value, utf8Yaml);
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
                        mergedProps[key] = InferRawValueType(pair.Value, utf8Yaml);
                    }
                }
            }
        }

        return mergedProps is { Count: > 0 }
            ? ExprType.Object(mergedProps, strict: true)
            : ExprType.Any;
    }

    private static ExprType InferRawValueType(RawYamlValue value, byte[] utf8Yaml)
    {
        return value switch
        {
            RawYamlString => ExprType.Any,
            RawYamlArray => ExprType.Any,
            RawYamlObject obj => InferRawObjectType(obj, utf8Yaml),
            _ => ExprType.Any,
        };
    }

    private static ObjectExprType InferRawObjectType(RawYamlObject obj, byte[] utf8Yaml)
    {
        var props = new Dictionary<Utf8String, ExprType>(obj.Properties.Count);
        foreach (var pair in obj.Properties)
        {
            props[pair.Key.ToUtf8StringZeroCopy(utf8Yaml)] = InferRawValueType(pair.Value, utf8Yaml);
        }

        return ExprType.Object(props, strict: true);
    }

    /// <summary>
    /// Builds the needs context type override for a job.
    /// Returns a strict object keyed by depended-on job IDs, or a loose object when needs is empty.
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildNeedsOverride(
        StringNodeId[]? needs,
        SliceMap<Job> allJobs,
        AstArena arena,
        byte[]? utf8Yaml)
    {
        if (needs is null || needs.Length == 0)
        {
            // Job has no needs: — return strict empty so any `needs.X` is flagged as undefined
            return (NeedsKeyUtf8, ExprType.Object(strict: true));
        }

        var props = new Dictionary<Utf8String, ExprType>(needs.Length);
        for (var i = 0; i < needs.Length; i++)
        {
            var needSlice = arena.GetStringSlice(needs[i]);
            var needIdBytes = needSlice.AsSpan(utf8Yaml);
            if (needIdBytes.IsEmpty)
            {
                continue;
            }

            var outputsType = FindJobOutputsType(needIdBytes, allJobs, utf8Yaml);

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

    private static ExprType FindJobOutputsType(
        ReadOnlySpan<byte> jobIdBytes,
        SliceMap<Job> allJobs,
        byte[]? utf8Yaml)
    {
        if (utf8Yaml is not null)
        {
            foreach (var pair in allJobs)
            {
                if (EqualsAsciiIgnoreCase(pair.Key.AsSpan(utf8Yaml), jobIdBytes))
                {
                    return BuildJobOutputsType(pair.Value, utf8Yaml);
                }
            }
        }

        return ExprType.Object(dynamicPropertyType: ExprType.String);
    }

    private static ObjectExprType BuildJobOutputsType(Job job, byte[]? utf8Yaml)
    {
        if (job.Outputs is not { Count: > 0 } outputs || utf8Yaml is null)
        {
            return ExprType.Object(dynamicPropertyType: ExprType.String);
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
            if (ev is WorkflowCallEvent callEvent
                && callEvent.Inputs is { Count: > 0 } callInputs)
            {
                return (InputsKeyUtf8, BuildWorkflowCallInputsType(callInputs));
            }

            if (ev is WorkflowDispatchEvent dispatchEvent
                && dispatchEvent.Inputs is { Count: > 0 } dispatchInputs
                && utf8Yaml is not null)
            {
                return (InputsKeyUtf8, BuildWorkflowDispatchInputsType(dispatchInputs, utf8Yaml));
            }
        }

        return (InputsKeyUtf8, looseDynamic);
    }

    private static ObjectExprType BuildWorkflowCallInputsType(IReadOnlyList<WorkflowCallEventInput> inputs)
    {
        var props = new Dictionary<Utf8String, ExprType>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
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
            if (ev is WorkflowCallEvent { Secrets: { Count: > 0 } secrets } && utf8Yaml is not null)
            {
                var props = new Dictionary<Utf8String, ExprType>(secrets.Count);
                foreach (var pair in secrets)
                {
                    props[pair.Key.ToUtf8StringZeroCopy(utf8Yaml)] = ExprType.String;
                }

                return (SecretsKeyUtf8, ExprType.Object(props, strict: true));
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
            var outputsType = BuildJobOutputsType(pair.Value, utf8Yaml);
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
}
