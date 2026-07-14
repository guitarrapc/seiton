using System.Buffers;
using System.Text;

namespace Seiton.Core.Flow;

/// <summary>
/// Renders the flow DTO as a Mermaid <c>flowchart</c> for pasting into GitHub
/// Markdown (PRs, issues, docs). Jobs become subgraphs with chained step nodes,
/// <c>needs</c> edges connect jobs, parallel boundaries become nested subgraphs
/// with unchained children, and reusable-workflow jobs are subroutine nodes.
/// </summary>
public static class WorkflowFlowMermaid
{
    private const int MaxLabelLength = 64;

    /// <summary>
    /// Serializes workflows to Mermaid text. The output is always exactly one
    /// <c>flowchart</c> diagram — a second <c>flowchart</c> keyword inside one Mermaid
    /// code block is a parse error — so multiple workflows become wrapper subgraphs
    /// (<c>w0</c>, <c>w1</c>, …) whose node ids are prefixed to avoid collisions.
    /// </summary>
    public static string Serialize(ReadOnlySpan<WorkflowFlow> workflows)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        Write(buffer, workflows);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Writes Mermaid flowchart UTF-8 bytes without an intermediate <see cref="string"/>.</summary>
    public static void Write(IBufferWriter<byte> output, ReadOnlySpan<WorkflowFlow> workflows)
    {
        var writer = new FlowUtf8Writer(output);
        writer.WriteLiteral("flowchart LR\n"u8);
        var wrap = workflows.Length > 1;
        for (var w = 0; w < workflows.Length; w++)
        {
            WriteWorkflow(writer, workflows[w], wrap ? $"w{w}" : string.Empty, wrap);
        }
    }

    /// <summary>Writes a single workflow without allocating a one-element <see cref="WorkflowFlow"/> array.</summary>
    public static void Write(IBufferWriter<byte> output, WorkflowFlow workflow)
    {
        var writer = new FlowUtf8Writer(output);
        writer.WriteLiteral("flowchart LR\n"u8);
        WriteWorkflow(writer, workflow, string.Empty, wrap: false);
    }

    /// <summary>Writes an empty flowchart when no workflow is available.</summary>
    public static void WriteEmpty(IBufferWriter<byte> output)
    {
        var writer = new FlowUtf8Writer(output);
        writer.WriteLiteral("flowchart LR\n"u8);
    }

    private static void WriteWorkflow(FlowUtf8Writer writer, WorkflowFlow workflow, string prefix, bool wrap)
    {
        writer.WriteAscii("  %% ");
        writer.WriteUtf8(workflow.File);
        if (workflow.Name is not null)
        {
            writer.WriteUtf8(" — ");
            writer.WriteUtf8(workflow.Name);
        }

        writer.WriteNewLine();

        if (wrap)
        {
            writer.WriteAscii("  subgraph ");
            writer.WriteUtf8(prefix);
            writer.WriteAscii("[\"");
            if (workflow.Name is null)
            {
                WriteEscaped(writer, workflow.File);
            }
            else
            {
                WriteEscaped(writer, workflow.File);
                writer.WriteUtf8(" — ");
                WriteEscaped(writer, workflow.Name);
            }

            writer.WriteAscii("\"]\n");
            writer.WriteAscii("    direction LR\n");
        }

        string[]? anchorRent = null;
        var anchorCapacity = 0;
        try
        {
            for (var i = 0; i < workflow.Jobs.Length; i++)
            {
                var job = workflow.Jobs[i];
                var neededAnchors = Math.Max(8, job.Steps.Length * 2);
                if (anchorRent is null || anchorCapacity < neededAnchors)
                {
                    if (anchorRent is not null)
                    {
                        ArrayPool<string>.Shared.Return(anchorRent);
                    }

                    anchorRent = ArrayPool<string>.Shared.Rent(neededAnchors);
                    anchorCapacity = neededAnchors;
                }

                WriteJob(writer, job, prefix, i, anchorRent, out var anchorCount);
                WriteStepChain(writer, anchorRent, anchorCount);
            }

            for (var i = 0; i < workflow.Jobs.Length; i++)
            {
                var reducedNeeds = workflow.Jobs[i].ReducedNeeds;
                for (var n = 0; n < reducedNeeds.Length; n++)
                {
                    var dep = FindJobIndex(workflow.Jobs, reducedNeeds[n]);
                    if (dep < 0)
                    {
                        continue;
                    }

                    writer.WriteAscii("  ");
                    WriteJobNodeId(writer, prefix, dep);
                    writer.WriteAscii(" --> ");
                    WriteJobNodeId(writer, prefix, i);
                    writer.WriteNewLine();
                }
            }
        }
        finally
        {
            if (anchorRent is not null)
            {
                ArrayPool<string>.Shared.Return(anchorRent);
            }
        }

        if (wrap)
        {
            writer.WriteAscii("  end\n");
        }
    }

    private static void WriteJob(
        FlowUtf8Writer writer,
        FlowJob job,
        string prefix,
        int jobIndex,
        string[] anchorRent,
        out int anchorCount)
    {
        if (job.Kind == FlowJobKind.Reusable)
        {
            writer.WriteAscii("  ");
            WriteJobNodeId(writer, prefix, jobIndex);
            writer.WriteAscii("[[\"");
            WriteEscaped(writer, job.Id);
            writer.WriteUtf8(" — uses: ");
            WriteEscaped(writer, job.Uses ?? string.Empty);
            writer.WriteAscii("\"]]\n");
            anchorCount = 0;
            return;
        }

        writer.WriteAscii("  subgraph ");
        WriteJobNodeId(writer, prefix, jobIndex);
        writer.WriteAscii("[\"");
        WriteEscaped(writer, JobLabel(job));
        writer.WriteAscii("\"]\n");
        writer.WriteAscii("    direction TB\n");

        var counter = 0;
        anchorCount = 0;
        WriteSteps(writer, job.Steps, prefix, jobIndex, ref counter, anchorRent, ref anchorCount, "    ");
        writer.WriteAscii("  end\n");
    }

    private static void WriteStepChain(FlowUtf8Writer writer, string[] anchors, int anchorCount)
    {
        for (var a = 1; a < anchorCount; a++)
        {
            writer.WriteAscii("    ");
            writer.WriteUtf8(anchors[a - 1]);
            writer.WriteAscii(" --> ");
            writer.WriteUtf8(anchors[a]);
            writer.WriteNewLine();
        }
    }

    private static void WriteSteps(
        FlowUtf8Writer writer,
        FlowStep[] steps,
        string prefix,
        int jobIndex,
        ref int counter,
        string[]? anchors,
        ref int anchorCount,
        string indent)
    {
        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            if (step.Kind == FlowStepKind.Parallel)
            {
                writer.WriteAscii(indent);
                writer.WriteAscii("subgraph ");
                WriteGroupNodeId(writer, prefix, jobIndex, counter);
                writer.WriteAscii("[\"parallel\"]\n");
                writer.WriteAscii(indent);
                writer.WriteAscii("  direction TB\n");
                var groupId = CaptureGroupNodeId(prefix, jobIndex, counter);
                counter++;
                if (anchors is not null)
                {
                    anchors[anchorCount++] = groupId;
                }

                WriteSteps(writer, step.Steps, prefix, jobIndex, ref counter, anchors: null, ref anchorCount, indent + "  ");
                writer.WriteAscii(indent);
                writer.WriteAscii("end\n");
                continue;
            }

            writer.WriteAscii(indent);
            WriteStepNodeId(writer, prefix, jobIndex, counter);
            writer.WriteAscii("[\"");
            WriteEscaped(writer, StepLabel(step));
            writer.WriteAscii("\"]\n");
            if (anchors is not null)
            {
                anchors[anchorCount++] = CaptureStepNodeId(prefix, jobIndex, counter);
            }

            counter++;
        }
    }

    private static void WriteJobNodeId(FlowUtf8Writer writer, string prefix, int jobIndex)
    {
        writer.WriteUtf8(prefix);
        writer.WriteAscii('j');
        writer.WriteInt(jobIndex);
    }

    private static void WriteGroupNodeId(FlowUtf8Writer writer, string prefix, int jobIndex, int counter)
    {
        WriteJobNodeId(writer, prefix, jobIndex);
        writer.WriteAscii('g');
        writer.WriteInt(counter);
    }

    private static void WriteStepNodeId(FlowUtf8Writer writer, string prefix, int jobIndex, int counter)
    {
        WriteJobNodeId(writer, prefix, jobIndex);
        writer.WriteAscii('n');
        writer.WriteInt(counter);
    }

    private static string CaptureGroupNodeId(string prefix, int jobIndex, int counter)
        => $"{prefix}j{jobIndex}g{counter}";

    private static string CaptureStepNodeId(string prefix, int jobIndex, int counter)
        => $"{prefix}j{jobIndex}n{counter}";

    private static int FindJobIndex(FlowJob[] jobs, string need)
    {
        for (var i = 0; i < jobs.Length; i++)
        {
            if (string.Equals(jobs[i].Id, need, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string JobLabel(FlowJob job)
    {
        var label = job.Id;
        if (job.Strategy is { HasMatrix: true } strategy)
        {
            label += strategy.MatrixIsExpression
                ? " (matrix: dynamic)"
                : $" (matrix: {string.Join(" × ", strategy.MatrixKeys)})";
        }

        if (job.If is not null)
        {
            label += " (if)";
        }

        return label;
    }

    private static string StepLabel(FlowStep step)
    {
        var label = step.Kind switch
        {
            FlowStepKind.Run => $"run: {step.Name ?? step.Id ?? step.Run ?? string.Empty}",
            FlowStepKind.Uses => $"uses: {step.Name ?? step.Uses ?? string.Empty}",
            FlowStepKind.Wait => $"wait: {string.Join(", ", step.WaitTargets)}",
            FlowStepKind.WaitAll => "wait-all",
            FlowStepKind.Cancel => $"cancel: {step.CancelTarget}",
            _ => step.Name ?? step.Id ?? "step",
        };

        if (step.If is not null)
        {
            label += " (if)";
        }

        return label;
    }

    /// <summary>First line only, quotes replaced, truncated — keeps Mermaid labels parseable.</summary>
    private static void WriteEscaped(FlowUtf8Writer writer, string text)
    {
        var newline = text.IndexOf('\n');
        var span = newline >= 0 ? text.AsSpan(0, newline) : text.AsSpan();
        var end = span.Length;
        while (end > 0 && (span[end - 1] == '\r' || span[end - 1] == ' '))
        {
            end--;
        }

        span = span[..end];
        var length = Math.Min(span.Length, MaxLabelLength);
        Span<char> ch = stackalloc char[1];
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < length; i++)
        {
            var c = span[i] == '"' ? '\'' : span[i];
            if (c <= 0x7F)
            {
                writer.WriteAscii(c);
                continue;
            }

            ch[0] = c;
            var written = Encoding.UTF8.GetBytes(ch, utf8);
            writer.WriteLiteral(utf8[..written]);
        }

        if (span.Length > MaxLabelLength)
        {
            writer.WriteUtf8("…");
        }
    }
}
