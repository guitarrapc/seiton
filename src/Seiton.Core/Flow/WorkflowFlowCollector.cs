using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Flow;

/// <summary>
/// Walks a parsed workflow AST and materializes the flow DTO. All string handles
/// are resolved while the owning <see cref="ParseResult"/>/arena is still alive,
/// so the returned <see cref="WorkflowFlow"/> is safe to use after disposal.
/// </summary>
public static class WorkflowFlowCollector
{
    /// <summary>Collects the flow DTO, or <c>null</c> when the document is not a workflow.</summary>
    public static WorkflowFlow? Collect(ParseResult result, string filePath)
        => Collect(result.Workflow, filePath);

    /// <summary>Collects the flow DTO from a live workflow ref, or <c>null</c> when absent.</summary>
    public static WorkflowFlow? Collect(WorkflowRef workflow, string filePath)
    {
        if (!workflow.HasValue)
        {
            return null;
        }

        var events = workflow.On;
        var on = events.Count == 0 ? [] : new string[events.Count];
        for (var i = 0; i < on.Length; i++)
        {
            on[i] = events[i].EventName.Decode();
        }

        var jobMap = workflow.Jobs;
        var jobs = jobMap.Count == 0 ? [] : new FlowJob[jobMap.Count];
        var jobIndex = 0;
        foreach (var (key, job) in jobMap)
        {
            jobs[jobIndex++] = CollectJob(key, job);
        }

        return new WorkflowFlow
        {
            File = filePath,
            Name = NullIfEmpty(workflow.Name),
            On = on,
            Jobs = jobs,
        };
    }

    private static FlowJob CollectJob(KeyRef key, JobRef job)
    {
        var workflowCall = job.WorkflowCall;
        var isReusable = workflowCall.HasValue;

        return new FlowJob
        {
            Id = key.Decode(),
            Name = NullIfEmpty(job.Name),
            Kind = isReusable ? FlowJobKind.Reusable : FlowJobKind.Job,
            If = NullIfEmpty(job.If),
            Needs = DecodeList(job.Needs),
            RunsOn = CollectRunsOn(job.RunsOn),
            Uses = isReusable ? NullIfEmpty(workflowCall.Uses) : null,
            Strategy = CollectStrategy(job.Strategy),
            Steps = CollectSteps(job.Steps),
        };
    }

    private static string[] CollectRunsOn(RunnerRef runner)
    {
        if (!runner.HasValue)
        {
            return [];
        }

        var labels = DecodeList(runner.Labels);
        if (labels.Length > 0)
        {
            return labels;
        }

        if (runner.LabelsExpr.HasText)
        {
            return [runner.LabelsExpr.Decode()];
        }

        if (runner.Group.HasText)
        {
            return [runner.Group.Decode()];
        }

        return [];
    }

    private static FlowStrategy? CollectStrategy(StrategyRef strategy)
    {
        if (!strategy.HasValue)
        {
            return null;
        }

        var matrix = strategy.Matrix;
        if (!matrix.HasValue)
        {
            return new FlowStrategy
            {
                HasMatrix = false,
                MatrixKeys = [],
                MatrixIsExpression = false,
            };
        }

        if (matrix.Expression.HasText)
        {
            return new FlowStrategy
            {
                HasMatrix = true,
                MatrixKeys = [],
                MatrixIsExpression = true,
            };
        }

        var rows = matrix.Rows;
        var keys = rows.Count == 0 ? [] : new string[rows.Count];
        var keyIndex = 0;
        foreach (var (rowKey, _) in rows)
        {
            keys[keyIndex++] = rowKey.Decode();
        }

        return new FlowStrategy
        {
            HasMatrix = true,
            MatrixKeys = keys,
            MatrixIsExpression = false,
        };
    }

    private static FlowStep[] CollectSteps(StepRefList steps)
    {
        if (steps.Count == 0)
        {
            return [];
        }

        var result = new FlowStep[steps.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = CollectStep(steps[i]);
        }

        return result;
    }

    private static FlowStep CollectStep(StepRef step)
    {
        var exec = step.Exec;
        var kind = exec.Kind switch
        {
            StepExecKind.Run => FlowStepKind.Run,
            StepExecKind.Action => FlowStepKind.Uses,
            StepExecKind.Parallel => FlowStepKind.Parallel,
            StepExecKind.Wait => FlowStepKind.Wait,
            StepExecKind.WaitAll => FlowStepKind.WaitAll,
            StepExecKind.Cancel => FlowStepKind.Cancel,
            _ => FlowStepKind.Unknown,
        };

        return new FlowStep
        {
            Kind = kind,
            Id = NullIfEmpty(step.Id),
            Name = NullIfEmpty(step.Name),
            If = NullIfEmpty(step.If),
            Run = kind == FlowStepKind.Run ? NullIfEmpty(exec.AsRun().Run) : null,
            Uses = kind == FlowStepKind.Uses ? NullIfEmpty(exec.AsAction().Uses) : null,
            WaitTargets = kind == FlowStepKind.Wait ? DecodeList(exec.AsWait().Targets) : [],
            CancelTarget = kind == FlowStepKind.Cancel ? NullIfEmpty(exec.AsCancel().Target) : null,
            Steps = kind == FlowStepKind.Parallel ? CollectSteps(exec.AsParallel().Steps) : [],
        };
    }

    private static string[] DecodeList(StringRefList list)
    {
        if (list.Count == 0)
        {
            return [];
        }

        var items = new string[list.Count];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = list[i].Decode();
        }

        return items;
    }

    private static string? NullIfEmpty(StringRef value) => value.HasText ? value.Decode() : null;
}
