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
        var range = job.Range;

        return new FlowJob
        {
            Line = range.StartLine,
            EndLine = range.EndLine,
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

    /// <summary>Combination cap matching the GitHub Actions matrix limit.</summary>
    private const int MaxMatrixCombinations = 256;

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
            Combinations = ExpandMatrix(matrix),
        };
    }

    /// <summary>
    /// Static matrix expansion approximating GitHub semantics: cross product of the
    /// dimension rows, then <c>exclude</c> entries remove subset matches, then
    /// <c>include</c> entries extend matching combinations (or append when nothing matches).
    /// Any dynamic <c>${{ }}</c> dimension or block makes the matrix non-expandable.
    /// </summary>
    private static KeyValuePair<string, string>[][] ExpandMatrix(MatrixRef matrix)
    {
        var dimKeys = new List<string>();
        var combos = new List<List<KeyValuePair<string, string>>>();
        long total = 1;

        foreach (var (rowKey, row) in matrix.Rows)
        {
            if (row.Expression.HasText)
            {
                return [];
            }

            var values = row.Values;
            if (values.Count == 0)
            {
                return [];
            }

            total *= values.Count;
            if (total > MaxMatrixCombinations)
            {
                return [];
            }

            var key = rowKey.Decode();
            dimKeys.Add(key);

            if (combos.Count == 0)
            {
                foreach (var value in values)
                {
                    combos.Add([new(key, StringifyRawYaml(value))]);
                }

                continue;
            }

            var next = new List<List<KeyValuePair<string, string>>>(combos.Count * values.Count);
            foreach (var combo in combos)
            {
                foreach (var value in values)
                {
                    var extended = new List<KeyValuePair<string, string>>(combo.Count + 1);
                    extended.AddRange(combo);
                    extended.Add(new(key, StringifyRawYaml(value)));
                    next.Add(extended);
                }
            }

            combos = next;
        }

        foreach (var block in matrix.Exclude)
        {
            if (block.Expression.HasText)
            {
                return [];
            }

            foreach (var entry in block.Entries)
            {
                var pairs = DecodeCombinationEntry(entry);
                combos.RemoveAll(combo => MatchesSubset(combo, pairs));
            }
        }

        foreach (var block in matrix.Include)
        {
            if (block.Expression.HasText)
            {
                return [];
            }

            foreach (var entry in block.Entries)
            {
                var pairs = DecodeCombinationEntry(entry);

                // Include-only matrix (no dimension rows): every entry is its own combination —
                // there is no base product for "applies to all" merging to make sense.
                if (dimKeys.Count == 0)
                {
                    combos.Add(new List<KeyValuePair<string, string>>(pairs));
                    if (combos.Count > MaxMatrixCombinations)
                    {
                        return [];
                    }

                    continue;
                }

                var dimPairs = new List<KeyValuePair<string, string>>();
                var extraPairs = new List<KeyValuePair<string, string>>();
                foreach (var pair in pairs)
                {
                    if (ContainsKey(dimKeys, pair.Key))
                    {
                        dimPairs.Add(pair);
                    }
                    else
                    {
                        extraPairs.Add(pair);
                    }
                }

                var matched = false;
                foreach (var combo in combos)
                {
                    if (MatchesSubset(combo, dimPairs))
                    {
                        matched = true;
                        foreach (var extra in extraPairs)
                        {
                            SetOrAdd(combo, extra);
                        }
                    }
                }

                if (!matched)
                {
                    combos.Add(new List<KeyValuePair<string, string>>(pairs));
                    if (combos.Count > MaxMatrixCombinations)
                    {
                        return [];
                    }
                }
            }
        }

        if (combos.Count == 0)
        {
            return [];
        }

        var result = new KeyValuePair<string, string>[combos.Count][];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = combos[i].ToArray();
        }

        return result;
    }

    private static List<KeyValuePair<string, string>> DecodeCombinationEntry(RawYamlRefMap entry)
    {
        var pairs = new List<KeyValuePair<string, string>>(entry.Count);
        foreach (var (key, value) in entry)
        {
            pairs.Add(new(key.Decode(), StringifyRawYaml(value)));
        }

        return pairs;
    }

    private static bool MatchesSubset(List<KeyValuePair<string, string>> combo, List<KeyValuePair<string, string>> subset)
    {
        foreach (var pair in subset)
        {
            var found = false;
            foreach (var existing in combo)
            {
                if (string.Equals(existing.Key, pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    found = string.Equals(existing.Value, pair.Value, StringComparison.Ordinal);
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static void SetOrAdd(List<KeyValuePair<string, string>> combo, KeyValuePair<string, string> pair)
    {
        for (var i = 0; i < combo.Count; i++)
        {
            if (string.Equals(combo[i].Key, pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                combo[i] = pair;
                return;
            }
        }

        combo.Add(pair);
    }

    private static bool ContainsKey(List<string> keys, string key)
    {
        foreach (var existing in keys)
        {
            if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string StringifyRawYaml(RawYamlRef value)
    {
        switch (value.Kind)
        {
            case RawYamlKind.String:
                return value.Scalar.Decode();
            case RawYamlKind.Array:
            {
                var sb = new System.Text.StringBuilder("[");
                var first = true;
                foreach (var item in value.Items)
                {
                    if (!first)
                    {
                        sb.Append(", ");
                    }

                    first = false;
                    sb.Append(StringifyRawYaml(item));
                }

                return sb.Append(']').ToString();
            }

            case RawYamlKind.Object:
            {
                var sb = new System.Text.StringBuilder("{");
                var first = true;
                foreach (var (key, item) in value.Properties)
                {
                    if (!first)
                    {
                        sb.Append(", ");
                    }

                    first = false;
                    sb.Append(key.Decode()).Append(": ").Append(StringifyRawYaml(item));
                }

                return sb.Append('}').ToString();
            }

            default:
                return string.Empty;
        }
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

        var range = step.Range;

        return new FlowStep
        {
            Line = range.StartLine,
            EndLine = range.EndLine,
            Kind = kind,
            Id = NullIfEmpty(step.Id),
            Name = NullIfEmpty(step.Name),
            If = NullIfEmpty(step.If),
            Background = step.Background.HasValue && step.Background.Value,
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
