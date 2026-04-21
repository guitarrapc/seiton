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
        byte[] utf8Yaml)
    {
        var stepsKey = "steps"u8.ToArray();
        if (steps is null || steps.Count == 0)
        {
            return (stepsKey, s_looseDynamic);
        }

        var props = new Dictionary<Utf8String, ExprType>();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Id is null)
            {
                continue;
            }

            var idBytes = step.Id.Value.AsSpan(utf8Yaml);
            if (idBytes.IsEmpty)
            {
                continue;
            }

            props[new Utf8String(idBytes)] = s_stepEntryType;
        }

        return props.Count == 0
            ? (stepsKey, s_looseDynamic)
            : (stepsKey, ExprType.Object(props, strict: true));
    }

    /// <summary>
    /// Builds the matrix context type override for a job.
    /// Returns a strict object keyed by matrix row names, or a loose object when no rows are declared.
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildMatrixOverride(Matrix? matrix)
    {
        var matrixKey = "matrix"u8.ToArray();

        if (matrix is null || matrix.Expression is not null || matrix.Rows is not { Count: > 0 } rows)
        {
            return (matrixKey, s_looseDynamic);
        }

        var props = new Dictionary<Utf8String, ExprType>(rows.Count);
        foreach (var row in rows)
        {
            props[row.Key] = ExprType.Any;
        }

        return (matrixKey, ExprType.Object(props, strict: true));
    }

    /// <summary>
    /// Builds the needs context type override for a job.
    /// Returns a strict object keyed by depended-on job IDs, or a loose object when needs is empty.
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildNeedsOverride(
        IReadOnlyList<StringNode>? needs,
        IReadOnlyDictionary<Utf8String, Job> allJobs,
        byte[] utf8Yaml)
    {
        var needsKey = "needs"u8.ToArray();
        if (needs is null || needs.Count == 0)
        {
            return (needsKey, s_looseDynamic);
        }

        var props = new Dictionary<Utf8String, ExprType>(needs.Count);
        for (var i = 0; i < needs.Count; i++)
        {
            var needIdBytes = needs[i].Value.AsSpan(utf8Yaml);
            if (needIdBytes.IsEmpty)
            {
                continue;
            }

            var outputsType = FindJobOutputsType(needIdBytes, allJobs);
            var needsEntryType = ExprType.Object(
                new Dictionary<Utf8String, ExprType>
                {
                    { new Utf8String("result"u8), ExprType.String },
                    { new Utf8String("outputs"u8), outputsType },
                },
                strict: true);

            props[new Utf8String(needIdBytes)] = needsEntryType;
        }

        return props.Count == 0
            ? (needsKey, s_looseDynamic)
            : (needsKey, ExprType.Object(props, strict: true));
    }

    private static ExprType FindJobOutputsType(
        ReadOnlySpan<byte> jobIdBytes,
        IReadOnlyDictionary<Utf8String, Job> allJobs)
    {
        foreach (var pair in allJobs)
        {
            if (EqualsAsciiIgnoreCase(pair.Key.Span, jobIdBytes))
            {
                return BuildJobOutputsType(pair.Value);
            }
        }

        return ExprType.Object(dynamicPropertyType: ExprType.String);
    }

    private static ObjectExprType BuildJobOutputsType(Job job)
    {
        if (job.Outputs is not { Count: > 0 } outputs)
        {
            return ExprType.Object(dynamicPropertyType: ExprType.String);
        }

        var props = new Dictionary<Utf8String, ExprType>(outputs.Count);
        foreach (var pair in outputs)
        {
            props[pair.Key] = ExprType.String;
        }

        return ExprType.Object(props, strict: true);
    }

    /// <summary>
    /// Builds the inputs context type override for a workflow.
    /// Returns a strict object keyed by input names when workflow_call or workflow_dispatch inputs are defined.
    /// </summary>
    internal static (byte[] NameUtf8, ExprType Type) BuildInputsOverride(IReadOnlyList<Event> on)
    {
        var inputsKey = "inputs"u8.ToArray();
        for (var i = 0; i < on.Count; i++)
        {
            var ev = on[i];
            if (ev is WorkflowCallEvent callEvent
                && callEvent.Inputs is { Count: > 0 } callInputs)
            {
                return (inputsKey, BuildWorkflowCallInputsType(callInputs));
            }

            if (ev is WorkflowDispatchEvent dispatchEvent
                && dispatchEvent.Inputs is { Count: > 0 } dispatchInputs)
            {
                return (inputsKey, BuildWorkflowDispatchInputsType(dispatchInputs));
            }
        }

        return (inputsKey, s_looseDynamic);
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
        IReadOnlyDictionary<Utf8String, DispatchInput> inputs)
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
            props[pair.Key] = type;
        }

        return ExprType.Object(props, strict: true);
    }
}
