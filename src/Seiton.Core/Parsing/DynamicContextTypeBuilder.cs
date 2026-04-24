using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Parsing;

/// <summary>
/// Builds per-job/workflow dynamic context type overrides for steps, matrix, needs, and inputs contexts.
/// These override the static BuiltinContextTypes entries to enable per-job property-level validation.
/// </summary>
internal static class DynamicContextTypeBuilder
{
    private static readonly ObjectExprType s_looseDynamic = ExprType.Object(dynamicPropertyType: ExprType.Any);

    // Static byte[] keys to avoid per-job allocation of "steps"u8.ToArray() etc.
    internal static readonly byte[] StepsKeyUtf8 = "steps"u8.ToArray();
    internal static readonly byte[] MatrixKeyUtf8 = "matrix"u8.ToArray();
    internal static readonly byte[] NeedsKeyUtf8 = "needs"u8.ToArray();
    internal static readonly byte[] InputsKeyUtf8 = "inputs"u8.ToArray();
    internal static readonly byte[] SecretsKeyUtf8 = "secrets"u8.ToArray();

    // Static Utf8String keys reused across all needs entries
    private static readonly Utf8String s_resultKey = new("result"u8);
    private static readonly Utf8String s_outputsKey = new("outputs"u8);

    private static readonly ObjectExprType s_stepEntryType = ExprType.Object(
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
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildStepsOverride(
        IReadOnlyList<Step>? steps,
        AstArena arena,
        byte[] utf8Yaml)
    {
        if (steps is null || steps.Count == 0)
        {
            return (StepsKeyUtf8, s_looseDynamic);
        }

        var props = new Dictionary<Utf8String, ExprType>();
        for (var i = 0; i < steps.Count; i++)
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

            props[idSlice.ToUtf8StringZeroCopy(utf8Yaml)] = s_stepEntryType;
        }

        return props.Count == 0
            ? (StepsKeyUtf8, s_looseDynamic)
            : (StepsKeyUtf8, ExprType.Object(props, strict: true));
    }

    /// <summary>
    /// Builds the matrix context type override for a job.
    /// Returns a strict object keyed by matrix row names, or a loose object when no rows are declared.
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildMatrixOverride(Matrix? matrix, AstArena? arena = null, byte[]? utf8Yaml = null)
    {
        if (matrix is null || matrix.Expression.HasValue || utf8Yaml is null)
        {
            return (MatrixKeyUtf8, s_looseDynamic);
        }

        var rows = matrix.Rows;
        var include = matrix.Include;

        // No rows and no include: loose dynamic
        if ((rows is null || rows.Value.Count == 0) && (include is null || include.Count == 0))
        {
            return (MatrixKeyUtf8, s_looseDynamic);
        }

        var estimatedCapacity = (rows is null ? 0 : rows.Value.Count) + 4; // extra room for include-only keys
        var props = new Dictionary<Utf8String, ExprType>(estimatedCapacity);

        // Add keys from main axes
        if (rows is { Count: > 0 })
        {
            foreach (var row in rows)
            {
                props[row.Key.ToUtf8StringZeroCopy(utf8Yaml)] = ExprType.Any;
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
            ? (MatrixKeyUtf8, s_looseDynamic)
            : (MatrixKeyUtf8, ExprType.Object(props, strict: true));
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
            return (NeedsKeyUtf8, s_looseDynamic);
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
                    { s_resultKey, ExprType.String },
                    { s_outputsKey, outputsType },
                },
                strict: true);

            props[needSlice.ToUtf8StringZeroCopy(utf8Yaml!)] = needsEntryType;
        }

        return props.Count == 0
            ? (NeedsKeyUtf8, s_looseDynamic)
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

        return (InputsKeyUtf8, s_looseDynamic);
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

        return (SecretsKeyUtf8, s_looseDynamic);
    }
}
