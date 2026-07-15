using System.Buffers;
using System.Text;

namespace Seiton.Core.Flow;

/// <summary>
/// Renders the flow DTO as a Mermaid <c>flowchart</c> for pasting into GitHub
/// Markdown (PRs, issues, docs). Jobs become subgraphs with chained step nodes,
/// <c>needs</c> edges connect jobs, parallel boundaries become nested subgraphs
/// with unchained children, and reusable-workflow jobs are subroutine nodes.
/// </summary>
public static partial class WorkflowFlowMermaid
{
    private const int MaxLabelLength = 64;

    private readonly struct FlowAnchor(int counter, bool isGroup)
    {
        public int Counter { get; } = counter;
        public bool IsGroup { get; } = isGroup;
    }

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

        FlowAnchor[]? anchorRent = null;
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
                        ArrayPool<FlowAnchor>.Shared.Return(anchorRent);
                    }

                    anchorRent = ArrayPool<FlowAnchor>.Shared.Rent(neededAnchors);
                    anchorCapacity = neededAnchors;
                }

                WriteJob(writer, job, prefix, i, anchorRent, out var anchorCount);
                WriteStepChain(writer, prefix, i, anchorRent, anchorCount);
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
                ArrayPool<FlowAnchor>.Shared.Return(anchorRent);
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
        FlowAnchor[] anchorRent,
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
        WriteJobLabel(writer, job);
        writer.WriteAscii("\"]\n");
        writer.WriteAscii("    direction TB\n");

        var counter = 0;
        anchorCount = 0;
        WriteSteps(writer, job.Steps, prefix, jobIndex, ref counter, anchorRent, ref anchorCount, "    ");
        writer.WriteAscii("  end\n");
    }

    private static void WriteStepChain(FlowUtf8Writer writer, string prefix, int jobIndex, FlowAnchor[] anchors, int anchorCount)
    {
        for (var a = 1; a < anchorCount; a++)
        {
            writer.WriteAscii("    ");
            WriteAnchorId(writer, prefix, jobIndex, anchors[a - 1]);
            writer.WriteAscii(" --> ");
            WriteAnchorId(writer, prefix, jobIndex, anchors[a]);
            writer.WriteNewLine();
        }
    }

    private static void WriteSteps(
        FlowUtf8Writer writer,
        FlowStep[] steps,
        string prefix,
        int jobIndex,
        ref int counter,
        FlowAnchor[]? anchors,
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
                if (anchors is not null)
                {
                    anchors[anchorCount++] = new FlowAnchor(counter, isGroup: true);
                }

                counter++;
                WriteSteps(writer, step.Steps, prefix, jobIndex, ref counter, anchors: null, ref anchorCount, indent + "  ");
                writer.WriteAscii(indent);
                writer.WriteAscii("end\n");
                continue;
            }

            writer.WriteAscii(indent);
            WriteStepNodeId(writer, prefix, jobIndex, counter);
            writer.WriteAscii("[\"");
            WriteStepLabel(writer, step);
            writer.WriteAscii("\"]\n");
            if (anchors is not null)
            {
                anchors[anchorCount++] = new FlowAnchor(counter, isGroup: false);
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

    private static void WriteAnchorId(FlowUtf8Writer writer, string prefix, int jobIndex, FlowAnchor anchor)
    {
        if (anchor.IsGroup)
        {
            WriteGroupNodeId(writer, prefix, jobIndex, anchor.Counter);
        }
        else
        {
            WriteStepNodeId(writer, prefix, jobIndex, anchor.Counter);
        }
    }

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

    private static void WriteJobLabel(FlowUtf8Writer writer, FlowJob job)
    {
        Span<char> label = stackalloc char[MaxLabelLength];
        var length = 0;
        var lastNonTrim = 0;
        var stopped = false;
        AppendLabelPart(job.Id, label, ref length, ref lastNonTrim, ref stopped);

        if (job.Strategy is { HasMatrix: true } strategy)
        {
            AppendLabelPart(" (matrix: ", label, ref length, ref lastNonTrim, ref stopped);
            if (strategy.MatrixIsExpression)
            {
                AppendLabelPart("dynamic", label, ref length, ref lastNonTrim, ref stopped);
            }
            else
            {
                for (var i = 0; i < strategy.MatrixKeys.Length; i++)
                {
                    if (i > 0)
                    {
                        AppendLabelPart(" × ", label, ref length, ref lastNonTrim, ref stopped);
                    }
                    AppendLabelPart(strategy.MatrixKeys[i], label, ref length, ref lastNonTrim, ref stopped);
                }
            }
            AppendLabelPart(")", label, ref length, ref lastNonTrim, ref stopped);
        }

        if (job.If is not null)
        {
            AppendLabelPart(" (if)", label, ref length, ref lastNonTrim, ref stopped);
        }

        WriteEscapedLabel(writer, label, lastNonTrim);
    }

    private static void WriteStepLabel(FlowUtf8Writer writer, FlowStep step)
    {
        Span<char> label = stackalloc char[MaxLabelLength];
        var length = 0;
        var lastNonTrim = 0;
        var stopped = false;

        switch (step.Kind)
        {
            case FlowStepKind.Run:
                AppendLabelPart("run: ", label, ref length, ref lastNonTrim, ref stopped);
                AppendLabelPart(step.Name ?? step.Id ?? step.Run ?? string.Empty, label, ref length, ref lastNonTrim, ref stopped);
                break;
            case FlowStepKind.Uses:
                AppendLabelPart("uses: ", label, ref length, ref lastNonTrim, ref stopped);
                AppendLabelPart(step.Name ?? step.Uses ?? string.Empty, label, ref length, ref lastNonTrim, ref stopped);
                break;
            case FlowStepKind.Wait:
                AppendLabelPart("wait: ", label, ref length, ref lastNonTrim, ref stopped);
                for (var i = 0; i < step.WaitTargets.Length; i++)
                {
                    if (i > 0)
                    {
                        AppendLabelPart(", ", label, ref length, ref lastNonTrim, ref stopped);
                    }
                    AppendLabelPart(step.WaitTargets[i], label, ref length, ref lastNonTrim, ref stopped);
                }
                break;
            case FlowStepKind.WaitAll:
                AppendLabelPart("wait-all", label, ref length, ref lastNonTrim, ref stopped);
                break;
            case FlowStepKind.Cancel:
                AppendLabelPart("cancel: ", label, ref length, ref lastNonTrim, ref stopped);
                AppendLabelPart(step.CancelTarget ?? string.Empty, label, ref length, ref lastNonTrim, ref stopped);
                break;
            default:
                AppendLabelPart(step.Name ?? step.Id ?? "step", label, ref length, ref lastNonTrim, ref stopped);
                break;
        }

        if (step.If is not null)
        {
            AppendLabelPart(" (if)", label, ref length, ref lastNonTrim, ref stopped);
        }

        WriteEscapedLabel(writer, label, lastNonTrim);
    }

    private static void AppendLabelPart(
        ReadOnlySpan<char> part,
        Span<char> label,
        ref int length,
        ref int lastNonTrim,
        ref bool stopped)
    {
        if (stopped)
        {
            return;
        }

        for (var i = 0; i < part.Length; i++)
        {
            var c = part[i];
            if (c == '\n')
            {
                stopped = true;
                return;
            }

            if (length < label.Length)
            {
                label[length] = c;
            }
            length++;

            if (c != '\r' && c != ' ')
            {
                lastNonTrim = length;
            }
        }
    }

    private static void WriteEscapedLabel(FlowUtf8Writer writer, ReadOnlySpan<char> label, int length)
    {
        WriteEscapedChars(writer, label[..Math.Min(length, label.Length)]);
        if (length > label.Length)
        {
            writer.WriteUtf8("…");
        }
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
        WriteEscapedChars(writer, span[..length]);
        if (span.Length > MaxLabelLength)
        {
            writer.WriteUtf8("…");
        }
    }

    private static void WriteEscapedChars(FlowUtf8Writer writer, ReadOnlySpan<char> span)
    {
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i] == '"' ? '\'' : span[i];
            if (c <= 0x7F)
            {
                writer.WriteAscii(c);
                continue;
            }

            var status = Rune.DecodeFromUtf16(span[i..], out var rune, out var charsConsumed);
            if (status != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                charsConsumed = 1;
            }

            i += charsConsumed - 1;
            var written = rune.EncodeToUtf8(utf8);
            writer.WriteLiteral(utf8[..written]);
        }
    }
}
