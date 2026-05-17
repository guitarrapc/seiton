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

                // Apply network-assisted pin remediation: attaches SHA/digest DiagnosticFix values
                // to unpinned-uses and unpinned-image diagnostics. This runs once per file.
                IReadOnlyList<Diagnostic> effectiveDiagnostics = lintDiagnostics;
                if (pinRemediation != null)
                {
                    var netStart = verboseLogger.GetTimestamp();
                    var remResult = await pinRemediation.RemediateAsync(lintDiagnostics, utf8Yaml);
                    effectiveDiagnostics = remResult.Diagnostics;
                    if (remResult.ResolvedCount > 0 && verboseLogger.IsEnabled)
                    {
                        var netElapsed = verboseLogger.GetElapsedTime(netStart);
                        verboseLogger.Log("network", $"resolved pins for {filePath} in {CheckCommand.FormatMilliseconds(netElapsed)} ms");
                    }
                }

                // Check whether any diagnostic (local or pin-remediated) has a fix attached.
                var hasAnyFix = false;
                for (var j = 0; j < effectiveDiagnostics.Count; j++)
                {
                    if (effectiveDiagnostics[j].Fix != null)
                    {
                        hasAnyFix = true;
                        break;
                    }
                }

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
                    // --dry-run: print diff using all effective (local + pin-remediated) fixes
                    hasPrintedDiff |= FixEngine.TryWriteUnifiedDiff(outputWriter, utf8Yaml, effectiveDiagnostics, filePath);
                    allDiagnostics.AddRange(effectiveDiagnostics);
                    continue;
                }

                // Apply first fix pass: includes both pin-remediated and local fixes.
                var currentYaml = utf8Yaml;
                var appliedFixes = 0;
                const int maxFixPasses = 8;

                var firstPassYaml = FixEngine.Apply(currentYaml, effectiveDiagnostics);
                if (!firstPassYaml.AsSpan().SequenceEqual(currentYaml))
                {
                    var firstPassFixCount = 0;
                    for (var j = 0; j < effectiveDiagnostics.Count; j++)
                        if (effectiveDiagnostics[j].Fix != null) firstPassFixCount++;
                    appliedFixes += firstPassFixCount;
                    currentYaml = firstPassYaml;
                }

                // Subsequent re-check passes: local AST fixes only (pin diagnostics are now
                // satisfied so they won't reappear). Skip pass 0 since it was already applied above.
                LintResult? currentHandle = null;
                try
                {
                    currentHandle = engine.Check(currentYaml, filePath, fixEnabledLintConfig);
                    for (var pass = 1; pass < maxFixPasses && currentHandle.HasFixableDiagnostics; pass++)
                    {
                        var nextYaml = FixEngine.Apply(currentYaml, currentHandle.FixableDiagnostics);
                        if (nextYaml.AsSpan().SequenceEqual(currentYaml))
                        {
                            break;
                        }

                        appliedFixes += currentHandle.FixableDiagnosticCount;
                        currentYaml = nextYaml;
                        currentHandle.Dispose();
                        currentHandle = engine.Check(currentYaml, filePath, fixEnabledLintConfig);
                    }

                    File.WriteAllBytes(filePath, currentYaml);
                    allDiagnostics.AddRange(currentHandle.Diagnostics.AsSpan());
                }
                finally
                {
                    currentHandle?.Dispose();
                }

                if (verboseLogger.IsEnabled)
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
                CheckCommand.WriteSuppressionSummary(verboseLogger,
                    CheckCommand.CreateAggregatedSuppressionSummary(totalSuppressed, suppressedByRule!));
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
}
