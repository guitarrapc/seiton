using System.Buffers;
using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Flow;

public static partial class WorkflowFlowMermaid
{
    /// <summary>Writes a single workflow directly from a live UTF-8 AST.</summary>
    public static void Write(IBufferWriter<byte> output, WorkflowRef workflow, string filePath)
    {
        var writer = new FlowUtf8Writer(output);
        writer.WriteLiteral("flowchart LR\n"u8);
        if (workflow.HasValue)
        {
            WriteAstWorkflowWithGraph(writer, workflow, filePath);
        }
    }

    private static void WriteAstWorkflowWithGraph(
        FlowUtf8Writer writer,
        WorkflowRef workflow,
        string filePath)
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
        FlowUtf8Writer writer,
        WorkflowRef workflow,
        string filePath,
        ReadOnlySpan<ulong> ancestors,
        int wordCount)
    {
        writer.WriteAscii("  %% ");
        writer.WriteUtf8(filePath);
        if (workflow.Name.HasText)
        {
            writer.WriteUtf8(" — ");
            writer.WriteUtf8Bytes(workflow.Name.Value);
        }
        writer.WriteNewLine();

        var jobs = workflow.Jobs;
        FlowAnchor[]? anchorRent = null;
        var anchorCapacity = 0;
        try
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                var job = jobs.GetAt(i).Value;
                var neededAnchors = Math.Max(8, job.Steps.Count * 2);
                if (anchorRent is null || anchorCapacity < neededAnchors)
                {
                    if (anchorRent is not null)
                    {
                        ArrayPool<FlowAnchor>.Shared.Return(anchorRent);
                    }

                    anchorRent = ArrayPool<FlowAnchor>.Shared.Rent(neededAnchors);
                    anchorCapacity = neededAnchors;
                }

                WriteAstJob(writer, jobs.GetAt(i), i, anchorRent, out var anchorCount);
                WriteStepChain(writer, string.Empty, i, anchorRent, anchorCount);
            }

            for (var i = 0; i < jobs.Count; i++)
            {
                var needs = jobs.GetAt(i).Value.Needs;
                for (var n = 0; n < needs.Count; n++)
                {
                    if (WorkflowFlowGraph.IsRedundantNeed(jobs, needs, n, ancestors, wordCount))
                    {
                        continue;
                    }

                    var dependencyIndex = WorkflowFlowGraph.FindJobIndex(jobs, needs[n].Value);
                    if (dependencyIndex < 0)
                    {
                        continue;
                    }

                    writer.WriteAscii("  ");
                    WriteJobNodeId(writer, string.Empty, dependencyIndex);
                    writer.WriteAscii(" --> ");
                    WriteJobNodeId(writer, string.Empty, i);
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
    }

    private static void WriteAstJob(
        FlowUtf8Writer writer,
        JobRefMap.Entry entry,
        int jobIndex,
        FlowAnchor[] anchors,
        out int anchorCount)
    {
        var job = entry.Value;
        var workflowCall = job.WorkflowCall;
        if (workflowCall.HasValue)
        {
            writer.WriteAscii("  ");
            WriteJobNodeId(writer, string.Empty, jobIndex);
            writer.WriteAscii("[[\"");
            WriteAstEscaped(writer, entry.Key.Bytes);
            writer.WriteUtf8(" — uses: ");
            WriteAstEscaped(writer, workflowCall.Uses.Value);
            writer.WriteAscii("\"]]\n");
            anchorCount = 0;
            return;
        }

        writer.WriteAscii("  subgraph ");
        WriteJobNodeId(writer, string.Empty, jobIndex);
        writer.WriteAscii("[\"");
        WriteAstJobLabel(writer, entry.Key, job);
        writer.WriteAscii("\"]\n");
        writer.WriteAscii("    direction TB\n");

        var counter = 0;
        anchorCount = 0;
        WriteAstSteps(writer, job.Steps, jobIndex, ref counter, anchors, ref anchorCount, "    ");
        writer.WriteAscii("  end\n");
    }

    private static void WriteAstSteps(
        FlowUtf8Writer writer,
        StepRefList steps,
        int jobIndex,
        ref int counter,
        FlowAnchor[]? anchors,
        ref int anchorCount,
        string indent)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Exec.Kind == StepExecKind.Parallel)
            {
                writer.WriteAscii(indent);
                writer.WriteAscii("subgraph ");
                WriteGroupNodeId(writer, string.Empty, jobIndex, counter);
                writer.WriteAscii("[\"parallel\"]\n");
                writer.WriteAscii(indent);
                writer.WriteAscii("  direction TB\n");
                if (anchors is not null)
                {
                    anchors[anchorCount++] = new FlowAnchor(counter, isGroup: true);
                }

                counter++;
                WriteAstSteps(
                    writer,
                    step.Exec.AsParallel().Steps,
                    jobIndex,
                    ref counter,
                    anchors: null,
                    ref anchorCount,
                    indent + "  ");
                writer.WriteAscii(indent);
                writer.WriteAscii("end\n");
                continue;
            }

            writer.WriteAscii(indent);
            WriteStepNodeId(writer, string.Empty, jobIndex, counter);
            writer.WriteAscii("[\"");
            WriteAstStepLabel(writer, step);
            writer.WriteAscii("\"]\n");
            if (anchors is not null)
            {
                anchors[anchorCount++] = new FlowAnchor(counter, isGroup: false);
            }

            counter++;
        }
    }

    private static void WriteAstJobLabel(FlowUtf8Writer writer, KeyRef key, JobRef job)
    {
        Span<char> label = stackalloc char[MaxLabelLength];
        var length = 0;
        var lastNonTrim = 0;
        var stopped = false;
        AppendAstLabelPart(key.Bytes, label, ref length, ref lastNonTrim, ref stopped);

        var matrix = job.Strategy.Matrix;
        if (matrix.HasValue)
        {
            AppendLabelPart(" (matrix: ", label, ref length, ref lastNonTrim, ref stopped);
            if (matrix.Expression.HasText)
            {
                AppendLabelPart("dynamic", label, ref length, ref lastNonTrim, ref stopped);
            }
            else
            {
                var rowIndex = 0;
                foreach (var (rowKey, _) in matrix.Rows)
                {
                    if (rowIndex++ > 0)
                    {
                        AppendLabelPart(" × ", label, ref length, ref lastNonTrim, ref stopped);
                    }
                    AppendAstLabelPart(rowKey.Bytes, label, ref length, ref lastNonTrim, ref stopped);
                }
            }
            AppendLabelPart(")", label, ref length, ref lastNonTrim, ref stopped);
        }

        if (job.If.HasText)
        {
            AppendLabelPart(" (if)", label, ref length, ref lastNonTrim, ref stopped);
        }

        WriteEscapedLabel(writer, label, lastNonTrim);
    }

    private static void WriteAstStepLabel(FlowUtf8Writer writer, StepRef step)
    {
        Span<char> label = stackalloc char[MaxLabelLength];
        var length = 0;
        var lastNonTrim = 0;
        var stopped = false;
        var exec = step.Exec;

        switch (exec.Kind)
        {
            case StepExecKind.Run:
                AppendLabelPart("run: ", label, ref length, ref lastNonTrim, ref stopped);
                AppendAstLabelPart(
                    FirstText(step.Name, step.Id, exec.AsRun().Run).Value,
                    label,
                    ref length,
                    ref lastNonTrim,
                    ref stopped);
                break;
            case StepExecKind.Action:
                AppendLabelPart("uses: ", label, ref length, ref lastNonTrim, ref stopped);
                AppendAstLabelPart(
                    FirstText(step.Name, exec.AsAction().Uses).Value,
                    label,
                    ref length,
                    ref lastNonTrim,
                    ref stopped);
                break;
            case StepExecKind.Wait:
                AppendLabelPart("wait: ", label, ref length, ref lastNonTrim, ref stopped);
                var targets = exec.AsWait().Targets;
                for (var i = 0; i < targets.Count; i++)
                {
                    if (i > 0)
                    {
                        AppendLabelPart(", ", label, ref length, ref lastNonTrim, ref stopped);
                    }
                    AppendAstLabelPart(targets[i].Value, label, ref length, ref lastNonTrim, ref stopped);
                }
                break;
            case StepExecKind.WaitAll:
                AppendLabelPart("wait-all", label, ref length, ref lastNonTrim, ref stopped);
                break;
            case StepExecKind.Cancel:
                AppendLabelPart("cancel: ", label, ref length, ref lastNonTrim, ref stopped);
                AppendAstLabelPart(
                    exec.AsCancel().Target.Value,
                    label,
                    ref length,
                    ref lastNonTrim,
                    ref stopped);
                break;
            default:
                var fallback = FirstText(step.Name, step.Id);
                if (fallback.HasText)
                {
                    AppendAstLabelPart(fallback.Value, label, ref length, ref lastNonTrim, ref stopped);
                }
                else
                {
                    AppendLabelPart("step", label, ref length, ref lastNonTrim, ref stopped);
                }
                break;
        }

        if (step.If.HasText)
        {
            AppendLabelPart(" (if)", label, ref length, ref lastNonTrim, ref stopped);
        }

        WriteEscapedLabel(writer, label, lastNonTrim);
    }

    private static StringRef FirstText(StringRef first, StringRef second)
        => first.HasText ? first : second;

    private static StringRef FirstText(StringRef first, StringRef second, StringRef third)
        => first.HasText ? first : second.HasText ? second : third;

    private static void WriteAstEscaped(FlowUtf8Writer writer, ReadOnlySpan<byte> utf8)
    {
        Span<char> label = stackalloc char[MaxLabelLength];
        var length = 0;
        var lastNonTrim = 0;
        var stopped = false;
        AppendAstLabelPart(utf8, label, ref length, ref lastNonTrim, ref stopped);
        WriteEscapedLabel(writer, label, lastNonTrim);
    }

    private static void AppendAstLabelPart(
        ReadOnlySpan<byte> part,
        Span<char> label,
        ref int length,
        ref int lastNonTrim,
        ref bool stopped)
    {
        if (stopped)
        {
            return;
        }

        Span<char> runeChars = stackalloc char[2];
        while (!part.IsEmpty)
        {
            if (part[0] <= 0x7F)
            {
                var c = (char)part[0];
                part = part[1..];
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
                continue;
            }

            var status = Rune.DecodeFromUtf8(part, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                consumed = 1;
            }
            part = part[consumed..];
            var charCount = rune.EncodeToUtf16(runeChars);
            for (var i = 0; i < charCount; i++)
            {
                if (length < label.Length)
                {
                    label[length] = runeChars[i];
                }
                length++;
                lastNonTrim = length;
            }
        }
    }
}
