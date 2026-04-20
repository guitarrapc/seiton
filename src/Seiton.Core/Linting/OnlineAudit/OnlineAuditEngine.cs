using System.Text;
using System.Text.RegularExpressions;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting.OnlineAudit;

public sealed class OnlineAuditEngine(
    IActionAdvisoryProvider? actionAdvisoryProvider,
    IActionRefResolver? actionRefResolver,
    NetworkConfig networkConfig)
{
    readonly IActionAdvisoryProvider? advisoryProvider = actionAdvisoryProvider;
    readonly IActionRefResolver? refResolver = actionRefResolver;
    readonly NetworkConfig networkConfig = networkConfig ?? new NetworkConfig();
    readonly KnownVulnerableActionsRule knownVulnerableActionsRule = new();
    readonly ImpostorCommitRule impostorCommitRule = new();
    readonly RefConfusionRule refConfusionRule = new();
    readonly StaleActionRefsRule staleActionRefsRule = new();
    readonly CompiledIgnoreActionEntry[] compiledIgnoreActions = [];

    public async Task<OnlineAuditResult> AuditAsync(
        LintResult lintResult,
        byte[] utf8Yaml,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(utf8Yaml);

        if (lintResult.Workflow is null)
        {
            return new OnlineAuditResult(lintResult.Diagnostics, AddedCount: 0, SkippedCount: 0, FailedCount: 0);
        }

        if (advisoryProvider is null && refResolver is null)
        {
            return new OnlineAuditResult(lintResult.Diagnostics, AddedCount: 0, SkippedCount: 0, FailedCount: 0);
        }

        var targets = CollectTargets(lintResult.Workflow, utf8Yaml, filePath);
        if (targets.Count == 0)
        {
            return new OnlineAuditResult(lintResult.Diagnostics, AddedCount: 0, SkippedCount: 0, FailedCount: 0);
        }

        var maxConcurrency = Math.Max(1, networkConfig.MaxConcurrency);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var outcomes = new AuditOutcome[targets.Count];
        var tasks = new Task[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            var index = i;
            tasks[index] = Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    outcomes[index] = await AuditTargetAsync(targets[index], cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken);
        }

        await Task.WhenAll(tasks);

        var diagnostics = new List<Diagnostic>(lintResult.Diagnostics.Length + targets.Count * 2);
        diagnostics.AddRange(lintResult.Diagnostics);
        var addedCount = 0;
        var skippedCount = 0;
        var failedCount = 0;
        for (var i = 0; i < outcomes.Length; i++)
        {
            var outcome = outcomes[i];
            diagnostics.AddRange(outcome.Diagnostics);
            addedCount += outcome.Diagnostics.Length;
            if (outcome.Skipped)
            {
                skippedCount++;
            }

            if (outcome.Failed)
            {
                failedCount++;
            }
        }

        return new OnlineAuditResult(diagnostics.ToArray(), addedCount, skippedCount, failedCount);
    }

    async Task<AuditOutcome> AuditTargetAsync(ActionAuditTarget target, CancellationToken cancellationToken)
    {
        if (ShouldIgnore(target))
        {
            return new AuditOutcome([], Skipped: true, Failed: false);
        }

        var timeout = networkConfig.TimeoutSeconds > 0
            ? TimeSpan.FromSeconds(networkConfig.TimeoutSeconds)
            : Timeout.InfiniteTimeSpan;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            cts.CancelAfter(timeout);
        }

        try
        {
            ActionAdvisory? advisory = null;
            if (advisoryProvider is not null)
            {
                advisory = await advisoryProvider.GetAdvisoryAsync(target.Owner, target.Repo, target.Reference, cts.Token);
            }

            ActionRefResolution? resolution = null;
            if (refResolver is not null)
            {
                resolution = await refResolver.ResolveAsync(target.Owner, target.Repo, target.Reference, cts.Token);
            }

            var diagnostics = new List<Diagnostic>(4);
            var knownVulnerable = knownVulnerableActionsRule.Evaluate(target, advisory);
            if (knownVulnerable.HasValue)
            {
                diagnostics.Add(knownVulnerable.Value);
            }

            if (resolution.HasValue)
            {
                var impostorCommit = impostorCommitRule.Evaluate(target, resolution.Value);
                if (impostorCommit.HasValue)
                {
                    diagnostics.Add(impostorCommit.Value);
                }

                var refConfusion = refConfusionRule.Evaluate(target, resolution.Value);
                if (refConfusion.HasValue)
                {
                    diagnostics.Add(refConfusion.Value);
                }

                var staleActionRef = staleActionRefsRule.Evaluate(target, resolution.Value);
                if (staleActionRef.HasValue)
                {
                    diagnostics.Add(staleActionRef.Value);
                }
            }

            return new AuditOutcome(diagnostics.ToArray(), Skipped: false, Failed: false);
        }
        catch when (networkConfig.OnError == NetworkErrorMode.Skip)
        {
            return new AuditOutcome([], Skipped: false, Failed: true);
        }
    }

    bool ShouldIgnore(ActionAuditTarget target)
    {
        var name = target.Owner + "/" + target.Repo;
        for (var i = 0; i < compiledIgnoreActions.Length; i++)
        {
            var entry = compiledIgnoreActions[i];
            if (entry.NameRegex.IsMatch(name) && entry.RefRegex.IsMatch(target.Reference))
            {
                return true;
            }
        }

        return false;
    }

    static List<ActionAuditTarget> CollectTargets(Workflow workflow, byte[] utf8Yaml, string filePath)
    {
        var result = new List<ActionAuditTarget>();
        var jobs = workflow.Jobs;
        if (jobs is null || jobs.Count == 0)
        {
            return result;
        }

        foreach (var pair in jobs)
        {
            var job = pair.Value;

            var workflowCall = job.WorkflowCall;
            if (workflowCall is not null)
            {
                TryAddTarget(result, workflowCall.Uses, utf8Yaml, filePath);
            }

            var steps = job.Steps;
            if (steps is null || steps.Count == 0)
            {
                continue;
            }

            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].Exec is ExecAction action)
                {
                    TryAddTarget(result, action.Uses, utf8Yaml, filePath);
                }
            }
        }

        return result;
    }

    static void TryAddTarget(List<ActionAuditTarget> targets, StringNode usesNode, byte[] utf8Yaml, string filePath)
    {
        var usesText = Encoding.UTF8.GetString(usesNode.Value.AsSpan(utf8Yaml));
        if (string.IsNullOrWhiteSpace(usesText)
            || usesText.StartsWith("./", StringComparison.Ordinal)
            || usesText.StartsWith("docker://", StringComparison.OrdinalIgnoreCase)
            || !TryParseActionReference(usesText, out var owner, out var repo, out var reference))
        {
            return;
        }

        targets.Add(new ActionAuditTarget(usesText, owner, repo, reference, usesNode.Range, filePath));
    }
    static CompiledIgnoreActionEntry[] CompileIgnoreActions(IReadOnlyList<IgnoreActionEntry> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var compiled = new CompiledIgnoreActionEntry[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            compiled[i] = new CompiledIgnoreActionEntry(
                new Regex(entries[i].NamePattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
                new Regex(entries[i].RefPattern, RegexOptions.CultureInvariant));
        }

        return compiled;
    }

    readonly record struct AuditOutcome(Diagnostic[] Diagnostics, bool Skipped, bool Failed);
    readonly record struct CompiledIgnoreActionEntry(Regex NameRegex, Regex RefRegex);
}

public readonly record struct ActionAuditTarget(
    string UsesText,
    string Owner,
    string Repo,
    string Reference,
    TextRange Location,
    string FilePath)
{
    public bool IsCommitSha => IsFullCommitSha(Reference);
}

public readonly record struct OnlineAuditResult(
    Diagnostic[] Diagnostics,
    int AddedCount,
    int SkippedCount,
    int FailedCount);
