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
        var sb = new StringBuilder(1024);
        sb.Append("flowchart LR\n");
        var wrap = workflows.Length > 1;
        for (var w = 0; w < workflows.Length; w++)
        {
            WriteWorkflow(sb, workflows[w], wrap ? $"w{w}" : string.Empty, wrap);
        }

        var text = sb.ToString();
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var span = output.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(text, span);
        output.Advance(written);
    }

    private static void WriteWorkflow(StringBuilder sb, WorkflowFlow workflow, string prefix, bool wrap)
    {
        sb.Append("  %% ").Append(workflow.File);
        if (workflow.Name is not null)
        {
            sb.Append(" — ").Append(workflow.Name);
        }

        sb.Append('\n');

        if (wrap)
        {
            var label = workflow.Name is null ? workflow.File : $"{workflow.File} — {workflow.Name}";
            sb.Append("  subgraph ").Append(prefix).Append("[\"").Append(Escape(label)).Append("\"]\n");
            sb.Append("    direction LR\n");
        }

        var jobIndexById = new Dictionary<string, int>(workflow.Jobs.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < workflow.Jobs.Length; i++)
        {
            jobIndexById.TryAdd(workflow.Jobs[i].Id, i);
        }

        for (var i = 0; i < workflow.Jobs.Length; i++)
        {
            var job = workflow.Jobs[i];
            var jobNodeId = $"{prefix}j{i}";
            if (job.Kind == FlowJobKind.Reusable)
            {
                sb.Append("  ").Append(jobNodeId).Append("[[\"").Append(Escape($"{job.Id} — uses: {job.Uses}")).Append("\"]]\n");
                continue;
            }

            sb.Append("  subgraph ").Append(jobNodeId).Append("[\"").Append(Escape(JobLabel(job))).Append("\"]\n");
            sb.Append("    direction TB\n");

            var counter = 0;
            var anchors = new List<string>(job.Steps.Length);
            WriteSteps(sb, job.Steps, jobNodeId, ref counter, anchors, "    ");
            for (var a = 1; a < anchors.Count; a++)
            {
                sb.Append("    ").Append(anchors[a - 1]).Append(" --> ").Append(anchors[a]).Append('\n');
            }

            sb.Append("  end\n");
        }

        for (var i = 0; i < workflow.Jobs.Length; i++)
        {
            // Transitively reduced edges keep the diagram readable, matching GitHub's graph.
            foreach (var need in workflow.Jobs[i].ReducedNeeds)
            {
                if (jobIndexById.TryGetValue(need, out var dep))
                {
                    sb.Append("  ").Append(prefix).Append('j').Append(dep).Append(" --> ").Append(prefix).Append('j').Append(i).Append('\n');
                }
            }
        }

        if (wrap)
        {
            sb.Append("  end\n");
        }
    }

    private static void WriteSteps(StringBuilder sb, FlowStep[] steps, string jobNodeId, ref int counter, List<string>? anchors, string indent)
    {
        foreach (var step in steps)
        {
            if (step.Kind == FlowStepKind.Parallel)
            {
                var groupId = $"{jobNodeId}g{counter++}";
                anchors?.Add(groupId);
                sb.Append(indent).Append("subgraph ").Append(groupId).Append("[\"parallel\"]\n");
                sb.Append(indent).Append("  direction TB\n");
                // Children run simultaneously, so they are intentionally not chained.
                WriteSteps(sb, step.Steps, jobNodeId, ref counter, anchors: null, indent + "  ");
                sb.Append(indent).Append("end\n");
                continue;
            }

            var nodeId = $"{jobNodeId}n{counter++}";
            anchors?.Add(nodeId);
            sb.Append(indent).Append(nodeId).Append("[\"").Append(Escape(StepLabel(step))).Append("\"]\n");
        }
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
    private static string Escape(string text)
    {
        var newline = text.IndexOf('\n');
        if (newline >= 0)
        {
            text = text[..newline];
        }

        text = text.Replace('"', '\'').TrimEnd('\r').Trim();
        if (text.Length > MaxLabelLength)
        {
            text = text[..(MaxLabelLength - 1)] + "…";
        }

        return text;
    }
}
