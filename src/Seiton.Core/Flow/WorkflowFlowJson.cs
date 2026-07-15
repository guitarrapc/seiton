using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Seiton.Core.Flow;

/// <summary>
/// Hand-written UTF-8 serializer for the flow-json contract. Manual writing keeps
/// the output identical between the NativeAOT CLI and the trimmed WASM Playground
/// without registering DTOs in a JsonSerializerContext.
/// </summary>
public static partial class WorkflowFlowJson
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

    /// <summary>Writes a single workflow without allocating a one-element array.</summary>
    public static void Write(IBufferWriter<byte> output, WorkflowFlow workflow)
    {
        using var writer = new Utf8JsonWriter(output, WriterOptions);
        WriteDocument(writer, workflow);
        writer.Flush();
    }

    /// <summary>Writes an empty <c>workflows</c> array.</summary>
    public static void WriteEmpty(IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output, WriterOptions);
        WriteDocument(writer, workflow: null);
        writer.Flush();
    }

    /// <summary>Writes one flow document into an active JSON object or array value position.</summary>
    public static void WriteDocument(Utf8JsonWriter writer, WorkflowFlow? workflow)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version"u8, Version);
        writer.WriteStartArray("workflows"u8);
        if (workflow is { } flow)
        {
            WriteWorkflow(writer, flow);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
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

        if (workflow.Schedules.Length > 0)
        {
            writer.WriteStartArray("schedules"u8);
            foreach (var schedule in workflow.Schedules)
            {
                writer.WriteStartObject();
                writer.WriteString("cron"u8, schedule.Cron);
                if (schedule.TimeZone is not null)
                {
                    writer.WriteString("timezone"u8, schedule.TimeZone);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (workflow.Concurrency is { } concurrency)
        {
            writer.WriteStartObject("concurrency"u8);
            if (concurrency.Group is not null)
            {
                writer.WriteString("group"u8, concurrency.Group);
            }

            writer.WriteBoolean("cancelInProgress"u8, concurrency.CancelInProgress);
            if (concurrency.Queue is not null)
            {
                writer.WriteString("queue"u8, concurrency.Queue);
            }

            writer.WriteEndObject();
        }

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
        if (job.Line > 0)
        {
            writer.WriteNumber("line"u8, job.Line);
            writer.WriteNumber("endLine"u8, job.EndLine);
        }

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

        writer.WriteStartArray("reducedNeeds"u8);
        foreach (var need in job.ReducedNeeds)
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

        if (job.TimeoutMinutes is { } jobTimeout)
        {
            writer.WriteNumber("timeoutMinutes"u8, jobTimeout);
        }

        if (job.Permissions is not null)
        {
            writer.WriteStartArray("permissions"u8);
            foreach (var permission in job.Permissions)
            {
                writer.WriteStringValue(permission);
            }

            writer.WriteEndArray();
        }

        if (job.Environment is not null)
        {
            writer.WriteString("environment"u8, job.Environment);
        }

        if (job.Strategy is { } strategy)
        {
            writer.WriteStartObject("strategy"u8);
            writer.WriteBoolean("hasMatrix"u8, strategy.HasMatrix);
            writer.WriteStartArray("matrixKeys"u8);
            foreach (var key in strategy.MatrixKeys)
            {
                writer.WriteStringValue(key);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("matrixIsExpression"u8, strategy.MatrixIsExpression);
            writer.WriteStartArray("combinations"u8);
            foreach (var combination in strategy.Combinations)
            {
                writer.WriteStartObject();
                foreach (var pair in combination)
                {
                    writer.WriteString(pair.Key, pair.Value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
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
        if (step.Line > 0)
        {
            writer.WriteNumber("line"u8, step.Line);
            writer.WriteNumber("endLine"u8, step.EndLine);
        }

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

        if (step.Background)
        {
            writer.WriteBoolean("background"u8, true);
        }

        if (step.BackgroundOutcome is { } outcome)
        {
            writer.WriteString("backgroundOutcome"u8, outcome switch
            {
                FlowBackgroundOutcome.Awaited => "awaited"u8,
                FlowBackgroundOutcome.Cancelled => "cancelled"u8,
                _ => "unawaited"u8,
            });
        }

        if (step.TimeoutMinutes is { } stepTimeout)
        {
            writer.WriteNumber("timeoutMinutes"u8, stepTimeout);
        }

        if (step.ContinueOnError)
        {
            writer.WriteBoolean("continueOnError"u8, true);
        }

        if (step.Run is not null)
        {
            writer.WriteString("run"u8, step.Run);
        }

        if (step.WorkingDirectory is not null)
        {
            writer.WriteString("workingDirectory"u8, step.WorkingDirectory);
        }

        if (step.Uses is not null)
        {
            writer.WriteString("uses"u8, step.Uses);
        }

        if (step.With is not null)
        {
            writer.WriteStartObject("with"u8);
            foreach (var pair in step.With)
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
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
