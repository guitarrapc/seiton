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

    /// <summary>Serializes workflows to Mermaid text; multiple workflows emit blank-line-separated diagrams.</summary>
    public static string Serialize(ReadOnlySpan<WorkflowFlow> workflows)
    {
        var sb = new StringBuilder(1024);
        var first = true;
        foreach (var workflow in workflows)
        {
            if (!first)
            {
                sb.Append('\n');
            }

            first = false;
            WriteWorkflow(sb, workflow);
        }

        return sb.ToString();
    }

    private static void WriteWorkflow(StringBuilder sb, WorkflowFlow workflow)
    {
        sb.Append("flowchart LR\n");
        sb.Append("  %% ").Append(workflow.File);
        if (workflow.Name is not null)
        {
            sb.Append(" — ").Append(workflow.Name);
        }

        sb.Append('\n');

        var jobIndexById = new Dictionary<string, int>(workflow.Jobs.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < workflow.Jobs.Length; i++)
        {
            jobIndexById.TryAdd(workflow.Jobs[i].Id, i);
        }

        for (var i = 0; i < workflow.Jobs.Length; i++)
        {
            var job = workflow.Jobs[i];
            if (job.Kind == FlowJobKind.Reusable)
            {
                sb.Append("  j").Append(i).Append("[[\"").Append(Escape($"{job.Id} — uses: {job.Uses}")).Append("\"]]\n");
                continue;
            }

            sb.Append("  subgraph j").Append(i).Append("[\"").Append(Escape(JobLabel(job))).Append("\"]\n");
            sb.Append("    direction TB\n");

            var counter = 0;
            var anchors = new List<string>(job.Steps.Length);
            WriteSteps(sb, job.Steps, i, ref counter, anchors, "    ");
            for (var a = 1; a < anchors.Count; a++)
            {
                sb.Append("    ").Append(anchors[a - 1]).Append(" --> ").Append(anchors[a]).Append('\n');
            }

            sb.Append("  end\n");
        }

        for (var i = 0; i < workflow.Jobs.Length; i++)
        {
            foreach (var need in workflow.Jobs[i].Needs)
            {
                if (jobIndexById.TryGetValue(need, out var dep))
                {
                    sb.Append("  j").Append(dep).Append(" --> j").Append(i).Append('\n');
                }
            }
        }
    }

    private static void WriteSteps(StringBuilder sb, FlowStep[] steps, int jobIndex, ref int counter, List<string>? anchors, string indent)
    {
        foreach (var step in steps)
        {
            if (step.Kind == FlowStepKind.Parallel)
            {
                var groupId = $"j{jobIndex}g{counter++}";
                anchors?.Add(groupId);
                sb.Append(indent).Append("subgraph ").Append(groupId).Append("[\"parallel\"]\n");
                sb.Append(indent).Append("  direction TB\n");
                // Children run simultaneously, so they are intentionally not chained.
                WriteSteps(sb, step.Steps, jobIndex, ref counter, anchors: null, indent + "  ");
                sb.Append(indent).Append("end\n");
                continue;
            }

            var nodeId = $"j{jobIndex}n{counter++}";
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
