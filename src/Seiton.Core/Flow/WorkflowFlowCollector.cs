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
        List<FlowSchedule>? schedules = null;
        for (var i = 0; i < on.Length; i++)
        {
            var evt = events[i];
            on[i] = evt.EventName.Decode();
            if (evt.Kind != EventKind.Scheduled)
            {
                continue;
            }

            foreach (var entry in evt.AsScheduled().Schedules)
            {
                schedules ??= [];
                schedules.Add(new FlowSchedule
                {
                    Cron = entry.Cron.Decode(),
                    TimeZone = NullIfEmpty(entry.Timezone),
                });
            }
        }

        var jobMap = workflow.Jobs;
        var jobCount = jobMap.Count;
        var jobs = jobCount == 0 ? [] : new FlowJob[jobCount];

        // First pass: ids and needs, so the transitive reduction can see the whole DAG
        // before the (init-only) FlowJob instances are constructed.
        var ids = new string[jobCount];
        var needs = new string[jobCount][];
        for (var i = 0; i < jobCount; i++)
        {
            var entry = jobMap.GetAt(i);
            ids[i] = entry.Key.Decode();
            needs[i] = DecodeList(entry.Value.Needs);
        }

        var reducedNeeds = ComputeReducedNeeds(ids, needs);
        for (var i = 0; i < jobCount; i++)
        {
            var entry = jobMap.GetAt(i);
            jobs[i] = CollectJob(entry.Key, entry.Value, ids[i], needs[i], reducedNeeds[i]);
        }

        return new WorkflowFlow
        {
            File = filePath,
            Name = NullIfEmpty(workflow.Name),
            On = on,
            Schedules = schedules is null ? [] : schedules.ToArray(),
            Concurrency = CollectConcurrency(workflow.Concurrency),
            Jobs = jobs,
        };
    }

    private static FlowConcurrency? CollectConcurrency(ConcurrencyRef concurrency)
    {
        if (!concurrency.HasValue)
        {
            return null;
        }

        return new FlowConcurrency
        {
            Group = NullIfEmpty(concurrency.Group),
            CancelInProgress = concurrency.CancelInProgress.HasValue && concurrency.CancelInProgress.Value,
            Queue = NullIfEmpty(concurrency.Queue),
        };
    }

    /// <summary>
    /// Removes needs edges implied by another dependency's transitive chain,
    /// matching how GitHub renders its workflow graph. Full <c>needs</c> stays
    /// the semantic contract; cycles are tolerated via partial memoization.
    /// </summary>
    private static string[][] ComputeReducedNeeds(string[] ids, string[][] needs)
    {
        var indexById = new Dictionary<string, int>(ids.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ids.Length; i++)
        {
            indexById.TryAdd(ids[i], i);
        }

        // ancestors[i] = every job index reachable upstream from i. Pre-assigning the
        // set before recursing makes cyclic needs terminate (with a partial result).
        var ancestors = new HashSet<int>?[ids.Length];

        HashSet<int> AncestorsOf(int index)
        {
            if (ancestors[index] is { } cached)
            {
                return cached;
            }

            var set = new HashSet<int>();
            ancestors[index] = set;
            foreach (var dep in needs[index])
            {
                if (!indexById.TryGetValue(dep, out var depIndex))
                {
                    continue;
                }

                if (set.Add(depIndex))
                {
                    set.UnionWith(AncestorsOf(depIndex));
                }
            }

            return set;
        }

        var reduced = new string[ids.Length][];
        for (var i = 0; i < ids.Length; i++)
        {
            var deps = needs[i];
            if (deps.Length < 2)
            {
                reduced[i] = deps;
                continue;
            }

            var kept = new List<string>(deps.Length);
            foreach (var dep in deps)
            {
                var redundant = false;
                if (indexById.TryGetValue(dep, out var depIndex))
                {
                    foreach (var other in deps)
                    {
                        if (!ReferenceEquals(other, dep)
                            && indexById.TryGetValue(other, out var otherIndex)
                            && otherIndex != depIndex
                            && AncestorsOf(otherIndex).Contains(depIndex))
                        {
                            redundant = true;
                            break;
                        }
                    }
                }

                if (!redundant)
                {
                    kept.Add(dep);
                }
            }

            reduced[i] = kept.Count == deps.Length ? deps : kept.ToArray();
        }

        return reduced;
    }

    private static FlowJob CollectJob(KeyRef key, JobRef job, string id, string[] needs, string[] reducedNeeds)
    {
        var workflowCall = job.WorkflowCall;
        var isReusable = workflowCall.HasValue;
        var range = job.Range;

        return new FlowJob
        {
            Line = range.StartLine,
            EndLine = range.EndLine,
            Id = id,
            Name = NullIfEmpty(job.Name),
            Kind = isReusable ? FlowJobKind.Reusable : FlowJobKind.Job,
            If = NullIfEmpty(job.If),
            Needs = needs,
            ReducedNeeds = reducedNeeds,
            RunsOn = CollectRunsOn(job.RunsOn),
            Uses = isReusable ? NullIfEmpty(workflowCall.Uses) : null,
            Strategy = CollectStrategy(job.Strategy),
            TimeoutMinutes = CollectTimeout(job.TimeoutMinutes),
            Permissions = CollectPermissions(job.Permissions),
            Environment = NullIfEmpty(job.Environment.Name),
            Steps = CollectSteps(job.Steps),
        };
    }

    private static double? CollectTimeout(FloatRef timeout)
        => timeout.HasValue && !timeout.Expression.HasText ? timeout.Value : null;

    private static string[]? CollectPermissions(PermissionsRef permissions)
    {
        if (!permissions.HasValue)
        {
            return null;
        }

        if (permissions.All.HasText)
        {
            return [permissions.All.Decode()];
        }

        var scopes = permissions.Scopes;
        var entries = scopes.Count == 0 ? [] : new string[scopes.Count];
        for (var i = 0; i < entries.Length; i++)
        {
            var scope = scopes.GetAt(i);
            entries[i] = $"{scope.Key.Decode()}: {scope.Value.Value.Decode()}";
        }

        return entries;
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

    private static FlowStep[] CollectSteps(StepRefList steps, bool topLevel = true)
    {
        if (steps.Count == 0)
        {
            return [];
        }

        var result = new FlowStep[steps.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = CollectStep(steps[i], topLevel ? ComputeBackgroundOutcome(steps, i) : null);
        }

        return result;
    }

    /// <summary>
    /// Scans the job's later top-level steps to determine how a background step is joined:
    /// a matching <c>wait</c> or any <c>wait-all</c> awaits it, a matching <c>cancel</c> cuts it.
    /// Returns <c>null</c> for non-background steps.
    /// </summary>
    private static FlowBackgroundOutcome? ComputeBackgroundOutcome(StepRefList steps, int index)
    {
        var step = steps[index];
        if (!step.Background.HasValue || !step.Background.Value)
        {
            return null;
        }

        var id = step.Id;
        for (var i = index + 1; i < steps.Count; i++)
        {
            var exec = steps[i].Exec;
            switch (exec.Kind)
            {
                case StepExecKind.WaitAll:
                    return FlowBackgroundOutcome.Awaited;
                case StepExecKind.Wait when id.HasText:
                    foreach (var target in exec.AsWait().Targets)
                    {
                        if (target.ValueEquals(id.Value))
                        {
                            return FlowBackgroundOutcome.Awaited;
                        }
                    }

                    break;
                case StepExecKind.Cancel when id.HasText:
                    if (exec.AsCancel().Target.ValueEquals(id.Value))
                    {
                        return FlowBackgroundOutcome.Cancelled;
                    }

                    break;
            }
        }

        return FlowBackgroundOutcome.Unawaited;
    }

    private static FlowStep CollectStep(StepRef step, FlowBackgroundOutcome? backgroundOutcome)
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
            BackgroundOutcome = backgroundOutcome,
            TimeoutMinutes = CollectTimeout(step.TimeoutMinutes),
            ContinueOnError = step.ContinueOnError.HasValue && step.ContinueOnError.Value,
            Run = kind == FlowStepKind.Run ? NullIfEmpty(exec.AsRun().Run) : null,
            WorkingDirectory = kind == FlowStepKind.Run ? NullIfEmpty(exec.AsRun().WorkingDirectory) : null,
            Uses = kind == FlowStepKind.Uses ? NullIfEmpty(exec.AsAction().Uses) : null,
            With = kind == FlowStepKind.Uses ? CollectWith(exec.AsAction().Inputs) : null,
            WaitTargets = kind == FlowStepKind.Wait ? DecodeList(exec.AsWait().Targets) : [],
            CancelTarget = kind == FlowStepKind.Cancel ? NullIfEmpty(exec.AsCancel().Target) : null,
            Steps = kind == FlowStepKind.Parallel ? CollectSteps(exec.AsParallel().Steps, topLevel: false) : [],
        };
    }

    private static KeyValuePair<string, string>[]? CollectWith(ActionInputRefMap inputs)
    {
        if (inputs.Count == 0)
        {
            return null;
        }

        var pairs = new KeyValuePair<string, string>[inputs.Count];
        var i = 0;
        foreach (var (key, value) in inputs)
        {
            pairs[i++] = new(key.Decode(), value.Decode());
        }

        return pairs;
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
