using Seiton.Cli;
using Seiton.Config;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using Seiton.Output;

namespace Seiton.Commands;

internal static class FixCommand
{
    public static async Task<int> RunAsync(
        string[] files,
        string? config,
        string stdinFilename,
        string[] ignore,
        string? minSeverity,
        OutputFormat format,
        bool oneline,
        ColorMode color,
        bool noColor,
        bool verbose,
        bool dryRun,
        bool check,
        bool enablePinNetwork,
        bool enableImageNetwork,
        bool includeActions,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        var outputWriter = output ?? Console.Out;
        var errorWriter = error ?? Console.Error;
        var resolvedFormat = CliConfigBridge.ResolveOutputFormat(format);
        var colorEnabled = CliConfigBridge.ResolveColorEnabled(color, noColor);

        // Resolve config
        string? configPath;
        try
        {
            configPath = CliConfigBridge.ResolveConfigPath(config);
        }
        catch (FileNotFoundException ex)
        {
            errorWriter.WriteLine(ex.Message);
            return ExitCode.FatalError;
        }

        var (lintConfig, configDiags) = CliConfigBridge.LoadConfig(configPath, enablePinNetwork, enableImageNetwork);
        if (CheckCommand.HasConfigErrors(configDiags, resolvedFormat, colorEnabled, oneline, errorWriter))
            return ExitCode.FatalError;

        var verboseLogger = VerboseLogger.Create(verbose, errorWriter);

        if (verbose)
        {
            lintConfig ??= new LintConfig();
            lintConfig.Verbose = true;
        }

        if (verboseLogger.IsEnabled)
        {
            verboseLogger.Log("config", configPath is not null ? Path.GetFullPath(configPath) : "(none, using defaults)");
        }

        // Resolve input files
        string[] resolvedFiles;
        try
        {
            resolvedFiles = InputDiscovery.ResolveFiles(files, includeActions, verboseLogger);
        }
        catch (FileNotFoundException ex)
        {
            errorWriter.WriteLine(ex.Message);
            return ExitCode.FatalError;
        }

        if (resolvedFiles.Length == 0 && !files.Contains("-"))
        {
            errorWriter.WriteLine(includeActions ? "no workflow/action files found" : "no workflow files found");
            return ExitCode.Success;
        }

        // Build pin remediation engine if network pin/image resolution is enabled.
        // CLI flags override config; config is used as fallback.
        // GitHub and OCI use separate HttpClients: GitHub bearer calls only follow same-origin redirects;
        // registry clients may rely on cross-origin redirects (e.g. auth challenges).
        PinRemediationEngine? pinRemediation = null;
        HttpClient? githubHttpClient = null;
        HttpClient? ociHttpClient = null;
        var effectivePinNetwork = enablePinNetwork || (lintConfig?.Fix.Pinning.EnableNetwork ?? false);
        var effectiveImageNetwork = enableImageNetwork || (lintConfig?.Fix.Images.EnableNetwork ?? false);

        if (verboseLogger.IsEnabled)
        {
            WriteEffectiveNetworkConfig(verboseLogger,
                enablePinNetwork, enableImageNetwork,
                lintConfig?.Fix.Pinning,
                lintConfig?.Fix.Images);
        }

        if (effectivePinNetwork || effectiveImageNetwork)
        {
            githubHttpClient = effectivePinNetwork ? GitHubApiHttpClientFactory.CreateForGitHubApi() : null;
            ociHttpClient = effectiveImageNetwork ? new HttpClient() : null;
            var networkConfig = lintConfig?.Network ?? new NetworkConfig();
            var pinningConfig = lintConfig?.Fix.Pinning ?? new FixPinningConfig();
            var imagesConfig = lintConfig?.Fix.Images ?? new FixImagesConfig();

            IActionShaResolver? shaResolver = effectivePinNetwork
                ? new GitHubActionShaResolver(githubHttpClient!, pinningConfig, networkConfig.GitHub)
                : null;
            IImageDigestResolver? imageResolver = effectiveImageNetwork
                ? new OciImageDigestResolver(ociHttpClient!, imagesConfig)
                : null;

            pinRemediation = new PinRemediationEngine(
                shaResolver,
                imageResolver,
                pinningConfig,
                imagesConfig,
                networkConfig);
        }

        try
        {
            var engine = new LintEngine();
            var allDiagnostics = new List<Diagnostic>();
            var hasPrintedDiff = false;
            var totalSuppressed = 0;
            Dictionary<string, int>? suppressedByRule = null;
            var workflowRuleSummaryLogged = false;
            var actionRuleSummaryLogged = false;
            var totalStart = verboseLogger.GetTimestamp();

            // Fix command always builds fixes; enable fix construction for all Check() calls.
            var fixEnabledLintConfig = new LintConfig
            {
                Rules = lintConfig?.Rules,
                Exclusions = lintConfig?.Exclusions,
                Fix = (lintConfig?.Fix ?? new FixConfig()) with { Enabled = true },
                Network = lintConfig?.Network ?? new NetworkConfig(),
                Verbose = lintConfig?.Verbose ?? false,
            };

            for (var i = 0; i < resolvedFiles.Length; i++)
            {
                var filePath = resolvedFiles[i];
                if (filePath == "-")
                {
                    errorWriter.WriteLine("fix: stdin not supported for fix command");
                    return ExitCode.InvalidOptions;
                }

                var utf8Yaml = File.ReadAllBytes(filePath);

                if (verboseLogger.IsEnabled)
                {
                    verboseLogger.Log($"fixing {filePath}...");
                }

                // Check the file. Copy diagnostics immediately so they remain valid
                // even after the owned lint result is disposed before async work.
                OwnedDiagnostics lintDiagnostics;
                var fileStart = verboseLogger.GetTimestamp();
                {
                    using var handle = engine.Check(utf8Yaml, filePath, fixEnabledLintConfig);
                    lintDiagnostics = handle.CopyDiagnostics();

                    if (verboseLogger.IsEnabled)
                    {
                        if (handle.DocumentKind != DocumentKind.Unknown
                            && !CheckCommand.HasLoggedRuleSummaryForKind(handle.DocumentKind, ref workflowRuleSummaryLogged, ref actionRuleSummaryLogged))
                        {
                            CheckCommand.WriteRuleSummary(verboseLogger, handle.ActiveRuleCount, handle.DisabledRuleCount, handle.DisabledRuleIds, handle.DocumentKind);
                            CheckCommand.MarkRuleSummaryLogged(handle.DocumentKind, ref workflowRuleSummaryLogged, ref actionRuleSummaryLogged);
                        }
                        var fileElapsed = verboseLogger.GetElapsedTime(fileStart);
                        CheckCommand.WriteFileTimingSummary(verboseLogger, filePath, handle.DocumentKind, fileElapsed, handle.DiagnosticCount, handle.SuppressionSummary.TotalSuppressed);

                        CheckCommand.AccumulateSuppression(handle.SuppressionSummary, ref totalSuppressed, ref suppressedByRule);
                    }
                }

                // Check whether any local diagnostic has a fix, or pin remediation might add one.
                var hasLocalFix = false;
                for (var j = 0; j < lintDiagnostics.Length; j++)
                {
                    if (lintDiagnostics[j].Fix != null)
                    {
                        hasLocalFix = true;
                        break;
                    }
                }

                // For --check mode: run pin remediation early to report fixable pin diagnostics.
                // For apply/dry-run: pin remediation runs after local fixes stabilize (Plan B).
                IReadOnlyList<Diagnostic> effectiveDiagnostics = lintDiagnostics;
                if (check && pinRemediation != null)
                {
                    var netStart = verboseLogger.GetTimestamp();
                    var remResult = await pinRemediation.RemediateAsync(lintDiagnostics, utf8Yaml);
                    effectiveDiagnostics = remResult.Diagnostics;
                    if (remResult.ResolvedCount > 0 && verboseLogger.IsEnabled)
                    {
                        var netElapsed = verboseLogger.GetElapsedTime(netStart);
                        verboseLogger.Log("network", $"resolved {remResult.ResolvedCount} pin(s) for {filePath} in {CheckCommand.FormatMilliseconds(netElapsed)} ms");
                    }
                }

                // Determine if there's any work to do.
                var hasAnyFix = hasLocalFix || (pinRemediation != null && HasPinFixableDiagnostics(lintDiagnostics));

                if (!hasAnyFix)
                {
                    allDiagnostics.AddRange(effectiveDiagnostics);
                    continue;
                }

                if (check)
                {
                    // --check: report fixable but don't apply
                    allDiagnostics.AddRange(effectiveDiagnostics);
                    continue;
                }

                if (dryRun)
                {
                    // --dry-run: compute fixed YAML via iterative conflict-safe apply, then diff.
                    byte[] dryRunYaml;
                    try
                    {
                        dryRunYaml = ApplyFixesIteratively(engine, utf8Yaml, filePath, fixEnabledLintConfig);
                        if (pinRemediation != null)
                        {
                            dryRunYaml = await ApplyPinRemediationAsync(pinRemediation, engine, dryRunYaml, filePath, fixEnabledLintConfig, verboseLogger);
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        WriteFixApplicationError(errorWriter, filePath, ex, verbose);
                        return ExitCode.FatalError;
                    }

                    if (!dryRunYaml.AsSpan().SequenceEqual(utf8Yaml))
                    {
                        var diff = FixEngine.BuildUnifiedDiffFromBytes(utf8Yaml, dryRunYaml, filePath);
                        if (diff.Length > 0)
                        {
                            outputWriter.Write(diff);
                            hasPrintedDiff = true;
                        }
                    }

                    allDiagnostics.AddRange(effectiveDiagnostics);
                    continue;
                }

                // Phase 1: Stabilize local fixes via conflict-aware iterative application.
                // Each pass re-lints the current YAML to get fresh offsets, avoiding conflicts.
                var currentYaml = utf8Yaml;
                var appliedFixes = 0;
                const int maxFixPasses = 8;

                try
                {
                    currentYaml = ApplyFixesIteratively(engine, currentYaml, filePath, fixEnabledLintConfig, maxFixPasses, ref appliedFixes);

                    // Phase 2: Pin remediation on stabilized YAML (案B).
                    // Local inserts are done, so pin edits target correct offsets.
                    if (pinRemediation != null)
                    {
                        currentYaml = await ApplyPinRemediationAsync(pinRemediation, engine, currentYaml, filePath, fixEnabledLintConfig, verboseLogger);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    WriteFixApplicationError(errorWriter, filePath, ex, verbose);
                    return ExitCode.FatalError;
                }

                // Final lint to collect remaining diagnostics.
                LintResult? currentHandle = null;
                try
                {
                    currentHandle = engine.Check(currentYaml, filePath, fixEnabledLintConfig);
                    File.WriteAllBytes(filePath, currentYaml);
                    allDiagnostics.AddRange(currentHandle.Diagnostics.AsSpan());
                }
                finally
                {
                    currentHandle?.Dispose();
                }

                if (verboseLogger.IsEnabled && appliedFixes > 0)
                {
                    verboseLogger.LogFile(filePath, $"applied {appliedFixes} fix(es)");
                }
            }

            // Apply ignore patterns
            if (ignore.Length > 0)
            {
                allDiagnostics.RemoveAll(d => DiagnosticsIgnoreFilter.IsMessageIgnored(ignore, d.Message));
            }

            // Apply min-severity filter
            if (minSeverity is not null)
            {
                var threshold = minSeverity.ToLowerInvariant() switch
                {
                    "error" => DiagnosticSeverity.Error,
                    "warning" => DiagnosticSeverity.Warning,
                    "info" => DiagnosticSeverity.Info,
                    _ => (DiagnosticSeverity?)null,
                };
                if (threshold is not null)
                    allDiagnostics.RemoveAll(d => d.Severity < threshold.Value);
            }

            // Output remaining diagnostics
            if (allDiagnostics.Count > 0)
            {
                if (hasPrintedDiff)
                    outputWriter.WriteLine();
                DiagnosticFormatter.Write(outputWriter, allDiagnostics, resolvedFormat, oneline, colorEnabled);
            }

            if (totalSuppressed > 0)
            {
                var suppressionCounts = suppressedByRule ?? EmptySuppressedByRule;
                CheckCommand.WriteSuppressionSummary(verboseLogger,
                    CheckCommand.CreateAggregatedSuppressionSummary(totalSuppressed, suppressionCounts));
            }

            CheckCommand.WriteSummary(errorWriter, allDiagnostics, resolvedFiles.Length, verbose, showExitHint: minSeverity is null);

            if (verboseLogger.IsEnabled)
                CheckCommand.WriteTotalTiming(verboseLogger, resolvedFiles.Length, verboseLogger.GetElapsedTime(totalStart), "fixed");

            // Hint about network flags when unfixed pin diagnostics remain
            if (allDiagnostics.Count > 0)
                CheckCommand.WriteNetworkFixHint(errorWriter, allDiagnostics, effectivePinNetwork, effectiveImageNetwork);

            var hasFixableAfterFilters = false;
            for (var i = 0; i < allDiagnostics.Count; i++)
            {
                if (allDiagnostics[i].Fix is null)
                    continue;

                hasFixableAfterFilters = true;
                break;
            }

            if (check && hasFixableAfterFilters)
                return ExitCode.LintIssuesFound;

            return CheckCommand.HasActionableDiagnostics(allDiagnostics) ? ExitCode.LintIssuesFound : ExitCode.Success;
        }
        finally
        {
            githubHttpClient?.Dispose();
            ociHttpClient?.Dispose();
        }
    }

    internal static void WriteEffectiveNetworkConfig(
        VerboseLogger logger,
        bool enablePinNetwork,
        bool enableImageNetwork,
        FixPinningConfig? pinningConfig,
        FixImagesConfig? imagesConfig)
    {
        var effectivePin = enablePinNetwork || (pinningConfig?.EnableNetwork ?? false);
        var pinSource = enablePinNetwork ? "--enable-pin-network" : pinningConfig?.HasEnableNetwork == true ? "config" : "default";
        logger.Log("config", $"fix.pinning.enable-network={(effectivePin ? "true" : "false")} (source: {pinSource})");

        var effectiveImage = enableImageNetwork || (imagesConfig?.EnableNetwork ?? false);
        var imageSource = enableImageNetwork ? "--enable-image-network" : imagesConfig?.HasEnableNetwork == true ? "config" : "default";
        logger.Log("config", $"fix.images.enable-network={(effectiveImage ? "true" : "false")} (source: {imageSource})");
    }

    private static void WriteFixApplicationError(TextWriter errorWriter, string filePath, InvalidOperationException ex, bool verbose)
    {
        errorWriter.WriteLine($"error: fix failed for {filePath}: {ex.Message}");
        errorWriter.WriteLine("hint: this may indicate conflicting lint rules or a bug in fix generation. Please report this issue.");
        if (verbose)
        {
            errorWriter.WriteLine($"detail: {ex.StackTrace}");
        }
    }

    private static readonly IReadOnlyDictionary<string, int> EmptySuppressedByRule =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// Applies local fixes iteratively: each pass re-lints to get fresh offsets,
    /// avoiding overlapping edit conflicts. Within each pass, selects a non-conflicting
    /// subset when multiple fixes target the same offset.
    /// </summary>
    private static byte[] ApplyFixesIteratively(
        LintEngine engine,
        byte[] currentYaml,
        string filePath,
        LintConfig fixEnabledLintConfig,
        int maxPasses,
        ref int appliedFixes)
    {
        for (var pass = 0; pass < maxPasses; pass++)
        {
            using var handle = engine.Check(currentYaml, filePath, fixEnabledLintConfig);
            if (!handle.HasFixableDiagnostics)
                break;

            var batch = SelectNonConflictingBatch(handle.FixableDiagnostics);
            var nextYaml = FixEngine.Apply(currentYaml, batch);
            if (nextYaml.AsSpan().SequenceEqual(currentYaml))
                break;

            appliedFixes += batch.Length;
            currentYaml = nextYaml;
        }

        return currentYaml;
    }

    /// <summary>
    /// Overload without appliedFixes counter (for dry-run).
    /// </summary>
    private static byte[] ApplyFixesIteratively(
        LintEngine engine,
        byte[] currentYaml,
        string filePath,
        LintConfig fixEnabledLintConfig,
        int maxPasses = 8)
    {
        var unused = 0;
        return ApplyFixesIteratively(engine, currentYaml, filePath, fixEnabledLintConfig, maxPasses, ref unused);
    }

    /// <summary>
    /// Applies pin remediation on stabilized YAML, then applies the resulting fixes.
    /// </summary>
    private static async Task<byte[]> ApplyPinRemediationAsync(
        PinRemediationEngine pinRemediation,
        LintEngine engine,
        byte[] currentYaml,
        string filePath,
        LintConfig fixEnabledLintConfig,
        VerboseLogger verboseLogger)
    {
        using var handle = engine.Check(currentYaml, filePath, fixEnabledLintConfig);
        var diagnostics = handle.CopyDiagnostics();

        var netStart = verboseLogger.GetTimestamp();
        var remResult = await pinRemediation.RemediateAsync(diagnostics, currentYaml);
        if (remResult.ResolvedCount > 0)
        {
            if (verboseLogger.IsEnabled)
            {
                var netElapsed = verboseLogger.GetElapsedTime(netStart);
                verboseLogger.Log("network", $"resolved {remResult.ResolvedCount} pin(s) for {filePath} in {CheckCommand.FormatMilliseconds(netElapsed)} ms");
            }

            // Apply pin fixes — these should not conflict since they target different offsets
            // (action refs are at distinct positions in the YAML).
            var pinYaml = FixEngine.Apply(currentYaml, remResult.Diagnostics);
            if (!pinYaml.AsSpan().SequenceEqual(currentYaml))
            {
                currentYaml = pinYaml;
            }
        }

        return currentYaml;
    }

    /// <summary>
    /// Selects a non-conflicting subset from fixable diagnostics. Diagnostics whose
    /// edits overlap or share the same offset with an already-selected edit are deferred
    /// to the next pass. Uses a greedy offset-ordered approach.
    /// </summary>
    private static Diagnostic[] SelectNonConflictingBatch(Diagnostic[] fixableDiagnostics)
    {
        if (fixableDiagnostics.Length <= 1)
            return fixableDiagnostics;

        // For each diagnostic, collect all its edit ranges and find the min offset for sorting.
        // Then process diagnostics in order of their earliest edit offset, selecting those
        // whose edit ranges don't overlap with already-accepted edits.
        var diagRanges = new (int minOffset, int diagIndex)[fixableDiagnostics.Length];
        var totalEditCount = 0;
        for (var i = 0; i < fixableDiagnostics.Length; i++)
        {
            var fix = fixableDiagnostics[i].Fix!.Value;
            var minOff = int.MaxValue;
            for (var j = 0; j < fix.Edits.Length; j++)
            {
                if (fix.Edits[j].Offset < minOff)
                {
                    minOff = fix.Edits[j].Offset;
                }

                totalEditCount++;
            }

            diagRanges[i] = (minOff, i);
        }

        Array.Sort(diagRanges, static (a, b) => a.minOffset.CompareTo(b.minOffset));

        // Track occupied ranges (offset, end) from selected diagnostics.
        var occupiedCount = 0;
        var occupied = new (int offset, int end)[totalEditCount];
        var selectedIndices = new int[fixableDiagnostics.Length];
        var selectedCount = 0;

        for (var i = 0; i < diagRanges.Length; i++)
        {
            var diagIdx = diagRanges[i].diagIndex;
            var fix = fixableDiagnostics[diagIdx].Fix!.Value;

            // Check if any edit of this diagnostic conflicts with occupied ranges.
            var conflicts = false;
            for (var j = 0; j < fix.Edits.Length; j++)
            {
                var editOffset = fix.Edits[j].Offset;
                var editEnd = editOffset + fix.Edits[j].Length;

                for (var k = 0; k < occupiedCount; k++)
                {
                    // Conflict: same offset, or overlapping range
                    if (editOffset == occupied[k].offset || editOffset < occupied[k].end ||
                        (editEnd > occupied[k].offset && editOffset < occupied[k].end))
                    {
                        conflicts = true;
                        break;
                    }
                }

                if (conflicts) break;
            }

            if (!conflicts)
            {
                selectedIndices[selectedCount++] = diagIdx;
                for (var j = 0; j < fix.Edits.Length; j++)
                {
                    var editOffset = fix.Edits[j].Offset;
                    var editEnd = editOffset + fix.Edits[j].Length;
                    // For 0-length inserts, treat end as offset+1 to prevent same-offset conflicts
                    if (editEnd == editOffset) editEnd = editOffset + 1;
                    occupied[occupiedCount++] = (editOffset, editEnd);
                }
            }
        }

        if (selectedCount == fixableDiagnostics.Length)
            return fixableDiagnostics;

        var result = new Diagnostic[selectedCount];
        for (var i = 0; i < selectedCount; i++)
        {
            result[i] = fixableDiagnostics[selectedIndices[i]];
        }

        return result;
    }

    /// <summary>
    /// Checks if there are diagnostics that pin remediation could resolve
    /// (unpinned-uses or unpinned-image without an existing fix).
    /// </summary>
    private static bool HasPinFixableDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var ruleId = diagnostics[i].RuleId;
            if (ruleId is "unpinned-uses" or "unpinned-image")
            {
                if (diagnostics[i].Fix is null)
                    return true;
            }
        }

        return false;
    }
}
