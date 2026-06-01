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
        VerboseLevel verboseLevel,
        bool dryRun,
        bool check,
        bool enablePinNetwork,
        bool enableImageNetwork,
        bool includeActions,
        bool skipAgenticWorkflows = false,
        bool showDiff = false,
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

        var skipAgentic = skipAgenticWorkflows || lintConfig?.Discovery.SkipAgenticWorkflows == true;
        var verboseLogger = VerboseLogger.Create(verboseLevel, errorWriter);

        if (verboseLevel >= VerboseLevel.Summary)
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
            resolvedFiles = InputDiscovery.ResolveFiles(files, includeActions, verboseLogger, skipAgentic);
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
            var excludedCount = 0;
            var workflowRuleSummaryLogged = false;
            var actionRuleSummaryLogged = false;
            var totalStart = verboseLogger.GetTimestamp();

            // Track per-file fix counts for the fix summary.
            // Key: filePath, Value: number of fixes applied.
            List<(string FilePath, int FixedCount)>? fixedFiles = null;

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

                if (ExclusionMatcher.IsFileFullyExcluded(lintConfig?.Exclusions, filePath))
                {
                    excludedCount++;
                }

                if (verboseLogger.LogFileProgress)
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
                        CheckCommand.AccumulateSuppression(handle.SuppressionSummary, ref totalSuppressed, ref suppressedByRule);
                    }
                    else
                    {
                        totalSuppressed += handle.SuppressionSummary.TotalSuppressed;
                    }

                    if (verboseLogger.IsEnabled)
                    {
                        if (handle.DocumentKind != DocumentKind.Unknown
                            && !CheckCommand.HasLoggedRuleSummaryForKind(handle.DocumentKind, ref workflowRuleSummaryLogged, ref actionRuleSummaryLogged))
                        {
                            CheckCommand.WriteRuleSummary(verboseLogger, handle.ActiveRuleCount, handle.DisabledRuleCount, handle.DisabledRuleIds, handle.DocumentKind);
                            CheckCommand.MarkRuleSummaryLogged(handle.DocumentKind, ref workflowRuleSummaryLogged, ref actionRuleSummaryLogged);
                        }
                    }

                    if (verboseLogger.LogFileProgress)
                    {
                        var fileElapsed = verboseLogger.GetElapsedTime(fileStart);
                        CheckCommand.WriteFileTimingSummary(verboseLogger, filePath, handle.DocumentKind, fileElapsed, handle.DiagnosticCount, handle.SuppressionSummary.TotalSuppressed);
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
                if (check && pinRemediation != null && HasPinFixableDiagnostics(lintDiagnostics))
                {
                    try
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
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        WriteFixApplicationError(errorWriter, filePath, ex, verboseLevel >= VerboseLevel.Summary);
                        return ExitCode.FatalError;
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
                    // --check: report fixable but don't apply.
                    // Summary entries are built after ignore/min-severity filters so the
                    // reported fixable counts match the diagnostics the user actually sees.
                    allDiagnostics.AddRange(effectiveDiagnostics);
                    continue;
                }

                if (dryRun)
                {
                    // --dry-run: compute fixed YAML via iterative conflict-safe apply, then diff.
                    byte[] dryRunYaml;
                    var dryRunApplied = 0;
                    try
                    {
                        dryRunYaml = ApplyFixesIteratively(engine, utf8Yaml, filePath, fixEnabledLintConfig, 8, ref dryRunApplied);
                        if (pinRemediation != null)
                        {
                            var (pinYaml, pinCount) = await ApplyPinRemediationAsync(pinRemediation, engine, dryRunYaml, filePath, fixEnabledLintConfig, verboseLogger);
                            dryRunYaml = pinYaml;
                            dryRunApplied += pinCount;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        WriteFixApplicationError(errorWriter, filePath, ex, verboseLevel >= VerboseLevel.Summary);
                        return ExitCode.FatalError;
                    }

                    if (!dryRunYaml.AsSpan().SequenceEqual(utf8Yaml))
                    {
                        TryWriteFixDiff(utf8Yaml, dryRunYaml, filePath, resolvedFormat, outputWriter, errorWriter, ref hasPrintedDiff);
                    }

                    // Relint the hypothetical fixed YAML to report remaining diagnostics,
                    // consistent with the normal fix path's final-lint behavior.
                    using (var dryRunHandle = engine.Check(dryRunYaml, filePath, fixEnabledLintConfig))
                    {
                        allDiagnostics.AddRange(dryRunHandle.Diagnostics.AsSpan());
                    }

                    if (dryRunApplied > 0)
                    {
                        fixedFiles ??= new List<(string, int)>();
                        fixedFiles.Add((filePath, dryRunApplied));
                    }

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
                        var (pinYaml, pinCount) = await ApplyPinRemediationAsync(pinRemediation, engine, currentYaml, filePath, fixEnabledLintConfig, verboseLogger);
                        currentYaml = pinYaml;
                        appliedFixes += pinCount;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    WriteFixApplicationError(errorWriter, filePath, ex, verboseLevel >= VerboseLevel.Summary);
                    return ExitCode.FatalError;
                }

                // Final lint to collect remaining diagnostics.
                LintResult? currentHandle = null;
                try
                {
                    currentHandle = engine.Check(currentYaml, filePath, fixEnabledLintConfig);
                    File.WriteAllBytes(filePath, currentYaml);
                    if (showDiff)
                    {
                        TryWriteFixDiff(utf8Yaml, currentYaml, filePath, resolvedFormat, outputWriter, errorWriter, ref hasPrintedDiff);
                    }
                    allDiagnostics.AddRange(currentHandle.Diagnostics.AsSpan());
                }
                finally
                {
                    currentHandle?.Dispose();
                }

                if (verboseLogger.LogFileProgress && appliedFixes > 0)
                {
                    verboseLogger.LogFile(filePath, $"applied {appliedFixes} fix(es)");
                }

                if (appliedFixes > 0)
                {
                    fixedFiles ??= new List<(string, int)>();
                    fixedFiles.Add((filePath, appliedFixes));
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

            if (check)
            {
                fixedFiles = CreateCheckSummaryEntries(allDiagnostics);
            }

            // Output remaining diagnostics
            if (allDiagnostics.Count > 0)
            {
                if (hasPrintedDiff && resolvedFormat == OutputFormat.Text)
                    outputWriter.WriteLine();
                DiagnosticFormatter.Write(outputWriter, allDiagnostics, resolvedFormat, oneline, colorEnabled);
            }

            if (totalSuppressed > 0 && verboseLogger.IsEnabled)
            {
                var suppressionCounts = suppressedByRule ?? EmptySuppressedByRule;
                CheckCommand.WriteSuppressionSummary(verboseLogger,
                    CheckCommand.CreateAggregatedSuppressionSummary(totalSuppressed, suppressionCounts));
            }

            var summaryMetadata = new CheckCommand.CheckSummaryMetadata(excludedCount, totalSuppressed);
            var showVerboseSummary = verboseLevel >= VerboseLevel.Summary;

            // Write fix summary FIRST (what was done), then remaining summary (what's left).
            // This order is more intuitive: action taken → consequences.
            if (fixedFiles is { Count: > 0 })
            {
                var summaryMode = check ? FixSummaryMode.Check
                    : dryRun ? FixSummaryMode.DryRun
                    : FixSummaryMode.Applied;
                WriteFixSummary(errorWriter, fixedFiles, allDiagnostics, summaryMode);
                // Use "remain" wording only for applied/dry-run (where fixes were/would be applied).
                // In check mode, nothing was changed so "remain" is misleading.
                var useRemainMode = !check;
                CheckCommand.WriteSummary(errorWriter, allDiagnostics, resolvedFiles.Length, showVerboseSummary, showExitHint: minSeverity is null, showPerFile: false, metadata: summaryMetadata, isRemainMode: useRemainMode);
            }
            else
            {
                CheckCommand.WriteSummary(errorWriter, allDiagnostics, resolvedFiles.Length, showVerboseSummary, showExitHint: minSeverity is null, showPerFile: false, metadata: summaryMetadata);
            }

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

    private static List<(string FilePath, int Fixed)>? CreateCheckSummaryEntries(List<Diagnostic> diagnostics)
    {
        Dictionary<string, int>? fixableByFile = null;
        for (var i = 0; i < diagnostics.Count; i++)
        {
            if (diagnostics[i].Fix is null || diagnostics[i].FilePath is not { } filePath)
            {
                continue;
            }

            fixableByFile ??= new Dictionary<string, int>(StringComparer.Ordinal);
            ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(fixableByFile, filePath, out _);
            count++;
        }

        if (fixableByFile is null || fixableByFile.Count == 0)
        {
            return null;
        }

        var entries = new List<(string FilePath, int Fixed)>(fixableByFile.Count);
        foreach (var kvp in fixableByFile)
        {
            entries.Add((kvp.Key, kvp.Value));
        }

        return entries;
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

    internal static string[] CreateFixApplicationErrorLines(string filePath, Exception ex, bool verbose)
    {
        // Normalize message to single line — exception messages can contain newlines
        // which would break the structured error:/hint:/detail: output format.
        var message = ex.Message.ReplaceLineEndings(" ");

        if (!verbose)
        {
            return
            [
                $"error: fix failed for {filePath}: {message}",
                "hint: this may indicate conflicting lint rules or a bug in fix generation. Please report this issue."
            ];
        }

        // Exception.ToString() preserves the exception type, message, inner exceptions,
        // and stack frames. Prefix each line with "detail:" to keep the structured
        // error:/hint:/detail: output format intact.
        var detail = ex.ToString();
        var detailLines = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new string[2 + detailLines.Length];
        result[0] = $"error: fix failed for {filePath}: {message}";
        result[1] = "hint: this may indicate conflicting lint rules or a bug in fix generation. Please report this issue.";
        for (var i = 0; i < detailLines.Length; i++)
        {
            result[2 + i] = $"detail: {detailLines[i].TrimEnd()}";
        }

        return result;
    }

    private static void WriteFixApplicationError(TextWriter errorWriter, string filePath, Exception ex, bool verbose)
    {
        var lines = CreateFixApplicationErrorLines(filePath, ex, verbose);
        for (var i = 0; i < lines.Length; i++)
        {
            errorWriter.WriteLine(lines[i]);
        }
    }

    internal enum FixSummaryMode { Applied, DryRun, Check }

    internal static void WriteFixSummary(
        TextWriter writer,
        List<(string FilePath, int FixedCount)> fixedFiles,
        List<Diagnostic> remainingDiagnostics,
        FixSummaryMode mode = FixSummaryMode.Applied)
    {
        // Compute per-file remaining counts from the filtered diagnostics list.
        // Use a dictionary keyed by file path for O(1) lookup.
        Dictionary<string, int>? remainingByFile = null;
        for (var i = 0; i < remainingDiagnostics.Count; i++)
        {
            var file = remainingDiagnostics[i].FilePath;
            if (file is null) continue;
            remainingByFile ??= new Dictionary<string, int>(StringComparer.Ordinal);
            ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(remainingByFile, file, out _);
            count++;
        }

        // Build a set of files that had fixes for fast lookup
        var fixedFileSet = new HashSet<string>(fixedFiles.Count, StringComparer.Ordinal);
        for (var i = 0; i < fixedFiles.Count; i++)
            fixedFileSet.Add(fixedFiles[i].FilePath);

        // Pre-compute totals for the summary line (which now goes first).
        var totalFixed = 0;
        var totalRemaining = 0;
        for (var i = 0; i < fixedFiles.Count; i++)
        {
            var (filePath, fixedCount) = fixedFiles[i];
            var remaining = 0;
            remainingByFile?.TryGetValue(filePath, out remaining);
            if (mode == FixSummaryMode.Check)
                remaining = Math.Max(0, remaining - fixedCount);
            totalFixed += fixedCount;
            totalRemaining += remaining;
        }

        // Also count remaining from unfixed files
        if (remainingByFile is not null)
        {
            foreach (var kvp in remainingByFile)
            {
                if (!fixedFileSet.Contains(kvp.Key))
                    totalRemaining += kvp.Value;
            }
        }

        // Per-file detail as table
        // Build combined list of all files (fixed + unfixed with remaining)
        var allFileEntries = new List<(string FilePath, int Fixed, int Remaining)>(fixedFiles.Count);
        for (var i = 0; i < fixedFiles.Count; i++)
        {
            var (filePath, fixedCount) = fixedFiles[i];
            var remaining = 0;
            remainingByFile?.TryGetValue(filePath, out remaining);

            // In check mode, allDiagnostics includes fixable issues (they weren't applied).
            // Subtract fixable count to show only non-fixable "remaining" issues.
            if (mode == FixSummaryMode.Check)
                remaining = Math.Max(0, remaining - fixedCount);

            allFileEntries.Add((filePath, fixedCount, remaining));
        }

        // Add unfixed files with remaining diagnostics
        if (remainingByFile is not null)
        {
            foreach (var kvp in remainingByFile)
            {
                if (!fixedFileSet.Contains(kvp.Key))
                    allFileEntries.Add((kvp.Key, 0, kvp.Value));
            }
        }

        if (allFileEntries.Count == 0) return;

        // Total summary line FIRST (action taken / overview)
        var totalFound = totalFixed + totalRemaining;
        var totalFiles = allFileEntries.Count;
        var fileWord = totalFiles == 1 ? "file" : "files";
        if (mode == FixSummaryMode.Check)
        {
            writer.WriteLine($"{totalFixed} of {totalFound} {(totalFound == 1 ? "issue" : "issues")} fixable in {totalFiles} {fileWord} ({totalRemaining} remaining)");
        }
        else
        {
            var totalVerb = mode == FixSummaryMode.DryRun ? "Would fix" : "Fixed";
            writer.WriteLine($"{totalVerb} {totalFixed} of {totalFound} {(totalFound == 1 ? "issue" : "issues")} in {totalFiles} {fileWord} ({totalRemaining} remaining)");
        }

        // Sort by total count (fixed + remaining) descending, then by file name for determinism
        allFileEntries.Sort((a, b) =>
        {
            var totalA = a.Fixed + a.Remaining;
            var totalB = b.Fixed + b.Remaining;
            var byCount = totalB.CompareTo(totalA);
            return byCount != 0 ? byCount : string.Compare(Path.GetFileName(a.FilePath), Path.GetFileName(b.FilePath), StringComparison.Ordinal);
        });

        // Mode-specific column header for the "fixed" column
        var fixColumnHeader = mode switch
        {
            FixSummaryMode.DryRun => "Would Fix",
            FixSummaryMode.Check => "Fixable",
            _ => "Fixed",
        };
        var fixHeaderLen = fixColumnHeader.Length;
        const string remainingHeader = "Remaining";
        var remainHeaderLen = remainingHeader.Length;

        // Compute column widths
        var maxFileLen = 4; // "File".Length
        var maxFixLen = fixHeaderLen;
        var maxRemainLen = remainHeaderLen;
        for (var i = 0; i < allFileEntries.Count; i++)
        {
            var name = Path.GetFileName(allFileEntries[i].FilePath);
            if (name.Length > maxFileLen) maxFileLen = name.Length;
            var fixDigits = CheckCommand.CountDigits(allFileEntries[i].Fixed);
            var remDigits = CheckCommand.CountDigits(allFileEntries[i].Remaining);
            if (fixDigits > maxFixLen) maxFixLen = fixDigits;
            if (remDigits > maxRemainLen) maxRemainLen = remDigits;
        }

        // Write table with blank line separator
        writer.WriteLine();

        // Header row
        writer.Write("| File");
        writer.Write(new string(' ', maxFileLen - 4));
        writer.Write(" | ");
        writer.Write(fixColumnHeader);
        writer.Write(new string(' ', maxFixLen - fixHeaderLen));
        writer.Write(" | ");
        writer.Write(remainingHeader);
        writer.Write(new string(' ', maxRemainLen - remainHeaderLen));
        writer.WriteLine(" |");

        // Separator row (right-aligned numeric columns)
        writer.Write('|');
        writer.Write(new string('-', maxFileLen + 2));
        writer.Write('|');
        writer.Write(new string('-', maxFixLen + 1));
        writer.Write(":|");
        writer.Write(new string('-', maxRemainLen + 1));
        writer.WriteLine(":|");

        // Data rows
        for (var i = 0; i < allFileEntries.Count; i++)
        {
            var (filePath, fixedCount, remaining) = allFileEntries[i];
            var displayName = Path.GetFileName(filePath);

            writer.Write("| ");
            writer.Write(displayName);
            writer.Write(new string(' ', maxFileLen - displayName.Length));
            writer.Write(" | ");
            var fixStr = fixedCount.ToString();
            writer.Write(new string(' ', maxFixLen - fixStr.Length));
            writer.Write(fixStr);
            writer.Write(" | ");
            var remStr = remaining.ToString();
            writer.Write(new string(' ', maxRemainLen - remStr.Length));
            writer.Write(remStr);
            writer.WriteLine(" |");
        }
    }

    private static readonly IReadOnlyDictionary<string, int> EmptySuppressedByRule =
        new Dictionary<string, int>(StringComparer.Ordinal);

    private static void TryWriteFixDiff(
        byte[] originalYaml,
        byte[] fixedYaml,
        string filePath,
        OutputFormat resolvedFormat,
        TextWriter outputWriter,
        TextWriter errorWriter,
        ref bool hasPrintedDiff)
    {
        if (fixedYaml.AsSpan().SequenceEqual(originalYaml))
        {
            return;
        }

        var diff = FixEngine.BuildUnifiedDiffFromBytes(originalYaml, fixedYaml, filePath);
        if (diff.Length == 0)
        {
            return;
        }

        // When output format is non-text (json/sarif), diff goes to stderr
        // to keep stdout as pure machine-parseable output.
        var diffWriter = resolvedFormat == OutputFormat.Text ? outputWriter : errorWriter;
        diffWriter.Write(diff);
        hasPrintedDiff = true;
    }

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
    /// Returns the number of pins actually applied (0 if nothing changed).
    /// </summary>
    private static async Task<(byte[] Yaml, int AppliedCount)> ApplyPinRemediationAsync(
        PinRemediationEngine pinRemediation,
        LintEngine engine,
        byte[] currentYaml,
        string filePath,
        LintConfig fixEnabledLintConfig,
        VerboseLogger verboseLogger)
    {
        using var handle = engine.Check(currentYaml, filePath, fixEnabledLintConfig);
        var diagnostics = handle.CopyDiagnostics();

        // Quick pre-scan: skip network remediation entirely when no pin-target diagnostics exist.
        if (!HasPinFixableDiagnostics(diagnostics))
        {
            return (currentYaml, 0);
        }

        var netStart = verboseLogger.GetTimestamp();
        var remResult = await pinRemediation.RemediateAsync(diagnostics, currentYaml);
        if (remResult.ResolvedCount > 0)
        {
            if (verboseLogger.IsEnabled)
            {
                var netElapsed = verboseLogger.GetElapsedTime(netStart);
                verboseLogger.Log("network", $"resolved {remResult.ResolvedCount} pin(s) for {filePath} in {CheckCommand.FormatMilliseconds(netElapsed)} ms");
            }

            // Apply only pin-rule fixes. remResult.Diagnostics may still contain non-pin
            // diagnostics with fixes (if maxPasses didn't fully converge). Applying those here
            // would bypass the conflict-aware selection logic. Filter to pin rules only.
            var pinYaml = FixEngine.Apply(currentYaml, PinFixableDiagnostics(remResult.Diagnostics));
            if (!pinYaml.AsSpan().SequenceEqual(currentYaml))
            {
                return (pinYaml, remResult.ResolvedCount);
            }
        }

        return (currentYaml, 0);
    }

    /// <summary>
    /// Selects a non-conflicting subset from fixable diagnostics. Diagnostics whose
    /// edits overlap or share the same offset with an already-selected edit are deferred
    /// to the next pass. Uses a greedy offset-ordered approach.
    /// </summary>
    internal static Diagnostic[] SelectNonConflictingBatch(Diagnostic[] fixableDiagnostics)
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

        // Stable tie-break by diagIndex ensures deterministic batch selection
        // when multiple diagnostics share the same minOffset (the conflict scenario).
        Array.Sort(diagRanges, static (a, b) =>
        {
            var cmp = a.minOffset.CompareTo(b.minOffset);
            return cmp != 0 ? cmp : a.diagIndex.CompareTo(b.diagIndex);
        });

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
                // For 0-length inserts, treat end as offset+1 to keep conflict checks
                // consistent with how occupied ranges are recorded below.
                if (editEnd == editOffset) editEnd = editOffset + 1;

                for (var k = 0; k < occupiedCount; k++)
                {
                    // Overlap: intervals [editOffset, editEnd) and [occupied.offset, occupied.end) intersect.
                    if (editOffset < occupied[k].end && editEnd > occupied[k].offset)
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

    /// <summary>
    /// Returns only the pin-rule diagnostics (unpinned-uses/unpinned-image) that have a fix attached.
    /// Used to ensure non-pin diagnostics with fixes don't bypass conflict-aware selection.
    /// </summary>
    private static IEnumerable<Diagnostic> PinFixableDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            if (d.Fix is not null && d.RuleId is "unpinned-uses" or "unpinned-image")
            {
                yield return d;
            }
        }
    }
}
