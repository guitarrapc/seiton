using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Seiton.Core.Flow;

/// <summary>
/// Hand-written UTF-8 serializer for the flow-json contract. Manual writing keeps
/// the output identical between the NativeAOT CLI and the trimmed WASM Playground
/// without registering DTOs in a JsonSerializerContext.
/// </summary>
public static class WorkflowFlowJson
{
    /// <summary>The flow-json contract version emitted as the top-level <c>version</c> property.</summary>
    public const int Version = 1;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = true,
    };

    /// <summary>Writes the flow-json document for the given workflows.</summary>
    public static void Write(IBufferWriter<byte> output, ReadOnlySpan<WorkflowFlow> workflows)
    {
        using var writer = new Utf8JsonWriter(output, WriterOptions);
        writer.WriteStartObject();
        writer.WriteNumber("version"u8, Version);
        writer.WriteStartArray("workflows"u8);
        foreach (var workflow in workflows)
        {
            WriteWorkflow(writer, workflow);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>Serializes a single workflow to a flow-json string (test/interop convenience).</summary>
    public static string Serialize(WorkflowFlow workflow) => Serialize([workflow]);

    /// <summary>Serializes workflows to a flow-json string (test/interop convenience).</summary>
    public static string Serialize(ReadOnlySpan<WorkflowFlow> workflows)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        Write(buffer, workflows);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteWorkflow(Utf8JsonWriter writer, WorkflowFlow workflow)
    {
        writer.WriteStartObject();
        writer.WriteString("file"u8, workflow.File);
        if (workflow.Name is not null)
        {
            writer.WriteString("name"u8, workflow.Name);
        }

        writer.WriteStartArray("on"u8);
        foreach (var eventName in workflow.On)
        {
            writer.WriteStringValue(eventName);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("jobs"u8);
        foreach (var job in workflow.Jobs)
        {
            WriteJob(writer, job);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteJob(Utf8JsonWriter writer, FlowJob job)
    {
        writer.WriteStartObject();
        writer.WriteString("id"u8, job.Id);
        if (job.Name is not null)
        {
            writer.WriteString("name"u8, job.Name);
        }

        writer.WriteString("kind"u8, job.Kind == FlowJobKind.Reusable ? "reusable"u8 : "job"u8);
        if (job.If is not null)
        {
            writer.WriteString("if"u8, job.If);
        }

        writer.WriteStartArray("needs"u8);
        foreach (var need in job.Needs)
        {
            writer.WriteStringValue(need);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("runsOn"u8);
        foreach (var label in job.RunsOn)
        {
            writer.WriteStringValue(label);
        }

        writer.WriteEndArray();

        if (job.Uses is not null)
        {
            writer.WriteString("uses"u8, job.Uses);
        }

        if (job.Strategy is not null)
        {
            writer.WriteStartObject("strategy"u8);
            writer.WriteBoolean("hasMatrix"u8, job.Strategy.HasMatrix);
            writer.WriteStartArray("matrixKeys"u8);
            foreach (var key in job.Strategy.MatrixKeys)
            {
                writer.WriteStringValue(key);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("matrixIsExpression"u8, job.Strategy.MatrixIsExpression);
            writer.WriteEndObject();
        }

        writer.WriteStartArray("steps"u8);
        foreach (var step in job.Steps)
        {
            WriteStep(writer, step);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteStep(Utf8JsonWriter writer, FlowStep step)
    {
        writer.WriteStartObject();
        writer.WriteString("kind"u8, KindNameUtf8(step.Kind));
        if (step.Id is not null)
        {
            writer.WriteString("id"u8, step.Id);
        }

        if (step.Name is not null)
        {
            writer.WriteString("name"u8, step.Name);
        }

        if (step.If is not null)
        {
            writer.WriteString("if"u8, step.If);
        }

        if (step.Run is not null)
        {
            writer.WriteString("run"u8, step.Run);
        }

        if (step.Uses is not null)
        {
            writer.WriteString("uses"u8, step.Uses);
        }

        if (step.Kind == FlowStepKind.Wait)
        {
            writer.WriteStartArray("targets"u8);
            foreach (var target in step.WaitTargets)
            {
                writer.WriteStringValue(target);
            }

            writer.WriteEndArray();
        }

        if (step.CancelTarget is not null)
        {
            writer.WriteString("target"u8, step.CancelTarget);
        }

        if (step.Kind == FlowStepKind.Parallel)
        {
            writer.WriteStartArray("steps"u8);
            foreach (var child in step.Steps)
            {
                WriteStep(writer, child);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static ReadOnlySpan<byte> KindNameUtf8(FlowStepKind kind) => kind switch
    {
        FlowStepKind.Run => "run"u8,
        FlowStepKind.Uses => "uses"u8,
        FlowStepKind.Parallel => "parallel"u8,
        FlowStepKind.Wait => "wait"u8,
        FlowStepKind.WaitAll => "wait-all"u8,
        FlowStepKind.Cancel => "cancel"u8,
        _ => "unknown"u8,
    };
}
