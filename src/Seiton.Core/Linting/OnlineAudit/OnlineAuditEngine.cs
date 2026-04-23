using System.Text;
using System.Text.RegularExpressions;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting.OnlineAudit;

/// <summary>
/// Performs post-traversal async resolution for <see cref="IOnlineRule"/> instances
/// whose targets were collected during <see cref="WorkflowVisitor"/> traversal.
/// </summary>
public sealed class OnlineAuditEngine(
    IActionAdvisoryProvider? actionAdvisoryProvider,
    IActionRefResolver? actionRefResolver,
    NetworkConfig networkConfig)
{
    private readonly IActionAdvisoryProvider? advisoryProvider = actionAdvisoryProvider;
    private readonly IActionRefResolver? refResolver = actionRefResolver;
    private readonly NetworkConfig networkConfig = networkConfig ?? new NetworkConfig();
    private readonly CompiledIgnoreActionEntry[] compiledIgnoreActions = [];

    /// <summary>
    /// Resolve targets collected by <paramref name="onlineRules"/> during visitor traversal,
    /// evaluate each rule, and return aggregated diagnostics.
    /// </summary>
    public async Task<OnlineAuditResult> AuditAsync(
        LintResult lintResult,
        IReadOnlyList<IOnlineRule> onlineRules,
        CancellationToken cancellationToken = default)
    {
        if (onlineRules.Count == 0)
        {
            return new OnlineAuditResult(lintResult.Diagnostics, AddedCount: 0, SkippedCount: 0, FailedCount: 0);
        }

        if (advisoryProvider is null && refResolver is null)
        {
            return new OnlineAuditResult(lintResult.Diagnostics, AddedCount: 0, SkippedCount: 0, FailedCount: 0);
        }

        // Collect unique targets from all online rules (deduplicate by UsesText)
        var targets = CollectUniqueTargets(onlineRules);
        if (targets.Count == 0)
        {
            return new OnlineAuditResult(lintResult.Diagnostics, AddedCount: 0, SkippedCount: 0, FailedCount: 0);
        }

        // Resolve all unique targets with concurrency control
        var maxConcurrency = Math.Max(1, networkConfig.MaxConcurrency);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var resolutions = new TargetResolution[targets.Count];
        var tasks = new Task[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            var index = i;
            tasks[index] = Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    resolutions[index] = await ResolveTargetAsync(targets[index], cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken);
        }

        await Task.WhenAll(tasks);

        // Build resolution lookup for evaluation
        var resolutionLookup = new Dictionary<string, TargetResolution>(targets.Count, StringComparer.Ordinal);
        var skippedCount = 0;
        var failedCount = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            resolutionLookup[targets[i].UsesText] = resolutions[i];
            if (resolutions[i].Skipped)
            {
                skippedCount++;
            }

            if (resolutions[i].Failed)
            {
                failedCount++;
            }
        }

        // Evaluate all online rules with resolved data
        for (var i = 0; i < onlineRules.Count; i++)
        {
            var rule = onlineRules[i];
            var collected = rule.CollectedTargets;
            for (var j = 0; j < collected.Count; j++)
            {
                var target = collected[j];
                if (resolutionLookup.TryGetValue(target.UsesText, out var resolution)
                    && !resolution.Failed && !resolution.Skipped)
                {
                    rule.EvaluateTarget(target, resolution.Advisory, resolution.RefResolution);
                }
            }
        }

        // Collect diagnostics from all online rules
        var diagnostics = new List<Diagnostic>(lintResult.Diagnostics.Length + targets.Count * 2);
        diagnostics.AddRange(lintResult.Diagnostics);
        var addedCount = 0;
        for (var i = 0; i < onlineRules.Count; i++)
        {
            var ruleDiags = onlineRules[i].GetDiagnostics();
            for (var j = 0; j < ruleDiags.Count; j++)
            {
                diagnostics.Add(ruleDiags[j]);
                addedCount++;
            }
        }

        return new OnlineAuditResult(diagnostics.ToArray(), addedCount, skippedCount, failedCount);
    }

    private static List<ActionAuditTarget> CollectUniqueTargets(IReadOnlyList<IOnlineRule> onlineRules)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targets = new List<ActionAuditTarget>();
        for (var i = 0; i < onlineRules.Count; i++)
        {
            var collected = onlineRules[i].CollectedTargets;
            for (var j = 0; j < collected.Count; j++)
            {
                if (seen.Add(collected[j].UsesText))
                {
                    targets.Add(collected[j]);
                }
            }
        }

        return targets;
    }

    private async Task<TargetResolution> ResolveTargetAsync(ActionAuditTarget target, CancellationToken cancellationToken)
    {
        if (ShouldIgnore(target))
        {
            return new TargetResolution(null, null, Skipped: true, Failed: false);
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

            return new TargetResolution(advisory, resolution, Skipped: false, Failed: false);
        }
        catch when (networkConfig.OnError == NetworkErrorMode.Skip)
        {
            return new TargetResolution(null, null, Skipped: false, Failed: true);
        }
    }

    private bool ShouldIgnore(ActionAuditTarget target)
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

    private static CompiledIgnoreActionEntry[] CompileIgnoreActions(IReadOnlyList<IgnoreActionEntry> entries)
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

    private readonly record struct TargetResolution(ActionAdvisory? Advisory, ActionRefResolution? RefResolution, bool Skipped, bool Failed);
    private readonly record struct CompiledIgnoreActionEntry(Regex NameRegex, Regex RefRegex);
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
