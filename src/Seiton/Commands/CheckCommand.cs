using System.Globalization;
using System.Runtime.InteropServices;
using Seiton.Cli;
using Seiton.Config;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Output;

namespace Seiton.Commands;

internal static class CheckCommand
{
    private static readonly IReadOnlyDictionary<string, int> EmptySuppressedByRule =
        new Dictionary<string, int>(0, StringComparer.Ordinal);

    public static int Run(
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
        bool includeActions)
    {
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
            Console.Error.WriteLine(ex.Message);
            return ExitCode.FatalError;
        }

        var (lintConfig, configDiags) = CliConfigBridge.LoadConfig(configPath, enablePinNetwork: false, enableImageNetwork: false);
        if (HasConfigErrors(configDiags, resolvedFormat, colorEnabled, oneline))
            return ExitCode.FatalError;

        var verboseLogger = VerboseLogger.Create(verbose, Console.Error);

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
            Console.Error.WriteLine(ex.Message);
            return ExitCode.FatalError;
        }

        if (resolvedFiles.Length == 0 && !files.Contains("-"))
        {
            Console.Error.WriteLine(includeActions ? "no workflow/action files found" : "no workflow files found");
            return ExitCode.Success;
        }

        // Lint files
        var allDiagnostics = new List<Diagnostic>();
        Dictionary<string, byte[]>? sourceMap = resolvedFormat == OutputFormat.Text && !oneline ? new() : null;
        var totalSuppressed = 0;
        Dictionary<string, int>? suppressedByRule = null;

        var hasStdin = files.Contains("-");
        var workflowRuleSummaryLogged = false;
        var actionRuleSummaryLogged = false;
        var totalStart = verboseLogger.GetTimestamp();

        // 1-file, single CPU, or stdin: sequential fast path (no ThreadLocal overhead)
        if (resolvedFiles.Length <= 1 || hasStdin || Environment.ProcessorCount <= 1)
        {
            var engine = new LintEngine();
            for (var i = 0; i < resolvedFiles.Length; i++)
            {
                var filePath = resolvedFiles[i];
                byte[] utf8Yaml;

                if (filePath == "-")
                {
                    using var ms = new MemoryStream();
                    using var stdin = Console.OpenStandardInput();
                    stdin.CopyTo(ms);
                    utf8Yaml = ms.ToArray();
                    filePath = stdinFilename;
                }
                else
                {
                    utf8Yaml = File.ReadAllBytes(filePath);
                }

                if (verboseLogger.IsEnabled)
                {
                    verboseLogger.Log($"checking {filePath}...");
                }

                var fileStart = verboseLogger.GetTimestamp();
                using var result = engine.Check(utf8Yaml, filePath, lintConfig);
                allDiagnostics.AddRange(result.Diagnostics.AsSpan());
                sourceMap?.TryAdd(filePath, utf8Yaml);

                if (verboseLogger.IsEnabled)
                {
                    if (result.DocumentKind != DocumentKind.Unknown
                        && !HasLoggedRuleSummaryForKind(result.DocumentKind, ref workflowRuleSummaryLogged, ref actionRuleSummaryLogged))
                    {
                        WriteRuleSummary(verboseLogger, result.ActiveRuleCount, result.DisabledRuleCount, result.DisabledRuleIds, result.DocumentKind);
                        MarkRuleSummaryLogged(result.DocumentKind, ref workflowRuleSummaryLogged, ref actionRuleSummaryLogged);
                    }
                    var fileElapsed = verboseLogger.GetElapsedTime(fileStart);
                    var suppressedCount = result.SuppressionSummary.TotalSuppressed;
                    WriteFileTimingSummary(verboseLogger, filePath, result.DocumentKind, fileElapsed, result.DiagnosticCount, suppressedCount);
                    AccumulateSuppression(result.SuppressionSummary, ref totalSuppressed, ref suppressedByRule);
                }
            }
        }
        else
        {
            // 2+ files: parallel with per-thread LintEngine isolation
            using var engines = new ThreadLocal<LintEngine>(
                static () => new LintEngine(), trackAllValues: false);
            var slots = new FileCheckResult[resolvedFiles.Length];

            // Rule activation metadata is invariant per DocumentKind within a run.
            // Capture once per kind (at most 2 snapshots) instead of N per-file copies.
            int workflowMetadataCaptured = 0, actionMetadataCaptured = 0;
            RuleActivationMetadata workflowRuleMetadata = default, actionRuleMetadata = default;

            Parallel.For(0, resolvedFiles.Length,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                i =>
                {
                    var filePath = resolvedFiles[i];
                    var utf8Yaml = File.ReadAllBytes(filePath);

                    if (verboseLogger.IsEnabled)
                    {
                        // Progress visibility matters more than ordering for this line in parallel mode.
                        verboseLogger.Log($"checking {filePath}...");
                    }

                    var fileStart = verboseLogger.GetTimestamp();
                    var engine = engines.Value!;
                    using var result = engine.Check(utf8Yaml, filePath, lintConfig);
                    var fileElapsed = verboseLogger.IsEnabled ? verboseLogger.GetElapsedTime(fileStart) : default;

                    // Capture rule metadata only once per DocumentKind to avoid N string[] allocations.
                    if (verboseLogger.IsEnabled && result.DocumentKind != DocumentKind.Unknown)
                    {
                        if (result.DocumentKind == DocumentKind.ActionMetadata)
                        {
                            if (Interlocked.CompareExchange(ref actionMetadataCaptured, 1, 0) == 0)
                            {
                                actionRuleMetadata = new RuleActivationMetadata(
                                    result.ActiveRuleCount, result.DisabledRuleCount,
                                    result.DisabledRuleIds.ToArray());
                            }
                        }
                        else
                        {
                            if (Interlocked.CompareExchange(ref workflowMetadataCaptured, 1, 0) == 0)
                            {
                                workflowRuleMetadata = new RuleActivationMetadata(
                                    result.ActiveRuleCount, result.DisabledRuleCount,
                                    result.DisabledRuleIds.ToArray());
                            }
                        }
                    }

                    slots[i] = new FileCheckResult(
                        result.CopyDiagnostics(), filePath,
                        sourceMap is not null ? utf8Yaml : null,
                        verboseLogger.IsEnabled ? result.SuppressionSummary : default,
                        verboseLogger.IsEnabled ? result.DocumentKind : default,
                        verboseLogger.IsEnabled ? fileElapsed : default);
                });

            // Aggregate in input order for stable output
            for (var i = 0; i < slots.Length; i++)
            {
                allDiagnostics.AddRange(slots[i].Diagnostics.AsSpan());
                if (sourceMap is not null && slots[i].Utf8Yaml is { } yaml)
                    sourceMap.TryAdd(slots[i].FilePath, yaml);

                if (verboseLogger.IsEnabled)
                {
                    if (slots[i].DocumentKind != DocumentKind.Unknown
                        && !HasLoggedRuleSummaryForKind(slots[i].DocumentKind, ref workflowRuleSummaryLogged, ref actionRuleSummaryLogged))
                    {
                        var meta = slots[i].DocumentKind == DocumentKind.ActionMetadata ? actionRuleMetadata : workflowRuleMetadata;
                        WriteRuleSummary(verboseLogger, meta.ActiveRuleCount, meta.DisabledRuleCount, meta.DisabledRuleIds, slots[i].DocumentKind);
                        MarkRuleSummaryLogged(slots[i].DocumentKind, ref workflowRuleSummaryLogged, ref actionRuleSummaryLogged);
                    }
                    WriteFileTimingSummary(verboseLogger, slots[i].FilePath, slots[i].DocumentKind, slots[i].FileElapsed, slots[i].FileDiagnosticCount, slots[i].FileSuppressedCount);
                    AccumulateSuppression(slots[i].SuppressionSummary, ref totalSuppressed, ref suppressedByRule);
                }
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
            var threshold = ParseSeverity(minSeverity);
            if (threshold is not null)
                allDiagnostics.RemoveAll(d => d.Severity < threshold.Value);
        }

        // Output
        if (allDiagnostics.Count > 0)
            DiagnosticFormatter.Write(Console.Out, allDiagnostics, resolvedFormat, oneline, colorEnabled, sourceMap);

        if (totalSuppressed > 0)
        {
            WriteSuppressionSummary(verboseLogger,
                CreateAggregatedSuppressionSummary(totalSuppressed, suppressedByRule ?? EmptySuppressedByRule));
        }

        WriteSummary(allDiagnostics, resolvedFiles.Length, verbose, showExitHint: minSeverity is null);

        if (verboseLogger.IsEnabled)
            WriteTotalTiming(verboseLogger, resolvedFiles.Length, verboseLogger.GetElapsedTime(totalStart));

        return HasActionableDiagnostics(allDiagnostics) ? ExitCode.LintIssuesFound : ExitCode.Success;
    }

    internal static void WriteSummary(List<Diagnostic> diagnostics, int fileCount, bool verbose = false, bool showExitHint = false, bool showPerFile = true)
        => WriteSummary(Console.Error, diagnostics, fileCount, verbose, showExitHint, showPerFile);

    internal static void WriteSummary(TextWriter writer, List<Diagnostic> diagnostics, int fileCount, bool verbose = false, bool showExitHint = false, bool showPerFile = true)
    {
        var errors = 0;
        var warnings = 0;
        var infos = 0;
        for (var i = 0; i < diagnostics.Count; i++)
        {
            switch (diagnostics[i].Severity)
            {
                case DiagnosticSeverity.Error: errors++; break;
                case DiagnosticSeverity.Warning: warnings++; break;
                default: infos++; break;
            }
        }

        var parts = new System.Text.StringBuilder();
        if (errors > 0) parts.Append(errors == 1 ? "1 error" : $"{errors} errors");
        if (warnings > 0) { if (parts.Length > 0) parts.Append(", "); parts.Append(warnings == 1 ? "1 warning" : $"{warnings} warnings"); }
        if (infos > 0) { if (parts.Length > 0) parts.Append(", "); parts.Append(infos == 1 ? "1 info" : $"{infos} infos"); }

        if (parts.Length == 0)
            writer.WriteLine($"0 issues in {fileCount} {(fileCount == 1 ? "file" : "files")}");
        else
            writer.WriteLine($"{parts} in {fileCount} {(fileCount == 1 ? "file" : "files")}");

        if (showPerFile && diagnostics.Count > 0)
        {
            WritePerFileBreakdown(writer, diagnostics);
        }

        if (verbose && diagnostics.Count > 0)
        {
            WritePerRuleBreakdown(writer, diagnostics);
        }

        // Show hint when warnings cause non-zero exit but no errors exist
        if (showExitHint && errors == 0 && warnings > 0)
        {
            writer.WriteLine("hint: use --min-severity error to treat warnings as non-blocking in CI");
        }
    }

    private static void WritePerFileBreakdown(TextWriter writer, List<Diagnostic> diagnostics)
    {
        // Count per file: errors, warnings, infos. Skip diagnostics without FilePath.
        var fileCounts = new Dictionary<string, (int Errors, int Warnings, int Infos)>(StringComparer.Ordinal);
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var filePath = diagnostics[i].FilePath;
            if (filePath is null) continue;
            ref var counts = ref CollectionsMarshal.GetValueRefOrAddDefault(fileCounts, filePath, out _);
            switch (diagnostics[i].Severity)
            {
                case DiagnosticSeverity.Error: counts.Errors++; break;
                case DiagnosticSeverity.Warning: counts.Warnings++; break;
                default: counts.Infos++; break;
            }
        }

        if (fileCounts.Count == 0) return;

        // Sort by total count descending, then by file name for determinism
        var sorted = new List<KeyValuePair<string, (int Errors, int Warnings, int Infos)>>(fileCounts);
        sorted.Sort((a, b) =>
        {
            var totalA = a.Value.Errors + a.Value.Warnings + a.Value.Infos;
            var totalB = b.Value.Errors + b.Value.Warnings + b.Value.Infos;
            var byCount = totalB.CompareTo(totalA);
            return byCount != 0 ? byCount : string.Compare(Path.GetFileName(a.Key), Path.GetFileName(b.Key), StringComparison.Ordinal);
        });

        for (var i = 0; i < sorted.Count; i++)
        {
            var (filePath, (errors, warnings, infos)) = sorted[i];
            var displayName = Path.GetFileName(filePath);

            var parts = new System.Text.StringBuilder();
            parts.Append("  ");
            parts.Append(displayName);
            parts.Append(": ");
            var hasPart = false;
            if (errors > 0) { parts.Append(errors == 1 ? "1 error" : $"{errors} errors"); hasPart = true; }
            if (warnings > 0) { if (hasPart) parts.Append(", "); parts.Append(warnings == 1 ? "1 warning" : $"{warnings} warnings"); hasPart = true; }
            if (infos > 0) { if (hasPart) parts.Append(", "); parts.Append(infos == 1 ? "1 info" : $"{infos} infos"); }
            writer.WriteLine(parts.ToString());
        }
    }

    private static void WritePerRuleBreakdown(TextWriter writer, List<Diagnostic> diagnostics)
    {
        // Count per rule, excluding null RuleId (parser diagnostics)
        var ruleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var ruleId = diagnostics[i].RuleId;
            if (ruleId is null) continue;
            if (!ruleCounts.TryGetValue(ruleId, out var count))
                ruleCounts[ruleId] = 1;
            else
                ruleCounts[ruleId] = count + 1;
        }

        if (ruleCounts.Count == 0) return;

        // Sort by count descending, then by rule ID for determinism
        var sorted = new List<KeyValuePair<string, int>>(ruleCounts);
        sorted.Sort((a, b) =>
        {
            var byCount = b.Value.CompareTo(a.Value);
            return byCount != 0 ? byCount : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
        });

        var sb = new System.Text.StringBuilder();
        sb.Append("  ");
        for (var i = 0; i < sorted.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(sorted[i].Key);
            sb.Append(": ");
            sb.Append(sorted[i].Value);
        }

        writer.WriteLine(sb.ToString());
    }

    internal static void WriteNetworkFixHint(TextWriter writer, List<Diagnostic> diagnostics, bool enablePinNetwork, bool enableImageNetwork)
    {
        var needsPin = false;
        var needsImage = false;
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var ruleId = diagnostics[i].RuleId;
            if (ruleId is null) continue;
            if (!enablePinNetwork && ruleId == "unpinned-uses") needsPin = true;
            if (!enableImageNetwork && ruleId == "unpinned-image") needsImage = true;
            if (needsPin && needsImage) break;
        }

        if (needsPin && needsImage)
            writer.WriteLine("hint: re-run with --enable-pin-network --enable-image-network to auto-fix pinning");
        else if (needsPin)
            writer.WriteLine("hint: re-run with --enable-pin-network to auto-fix action pinning");
        else if (needsImage)
            writer.WriteLine("hint: re-run with --enable-image-network to auto-fix image pinning");
    }

    internal static bool HasActionableDiagnostics(List<Diagnostic> diagnostics)
    {
        for (var i = 0; i < diagnostics.Count; i++)
        {
            if (diagnostics[i].Severity >= DiagnosticSeverity.Warning)
                return true;
        }
        return false;
    }

    internal static bool HasConfigErrors(Diagnostic[] configDiags, OutputFormat format, bool color, bool oneline)
        => HasConfigErrors(configDiags, format, color, oneline, Console.Error);

    internal static bool HasConfigErrors(Diagnostic[] configDiags, OutputFormat format, bool color, bool oneline, TextWriter error)
    {
        if (configDiags.Length == 0)
            return false;

        var hasError = false;
        for (var i = 0; i < configDiags.Length; i++)
        {
            if (configDiags[i].Severity == DiagnosticSeverity.Error)
            {
                hasError = true;
                break;
            }
        }

        if (hasError)
        {
            DiagnosticFormatter.Write(error, configDiags, format, oneline, color);
        }

        return hasError;
    }

    internal static void WriteSuppressionSummary(VerboseLogger logger, SuppressionSummary summary)
    {
        if (summary.TotalSuppressed == 0) return;

        var sorted = new List<KeyValuePair<string, int>>(summary.SuppressedByRule);
        sorted.Sort((a, b) =>
        {
            var byCount = b.Value.CompareTo(a.Value);
            return byCount != 0 ? byCount : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
        });

        var sb = new System.Text.StringBuilder();
        sb.Append(summary.TotalSuppressed);
        sb.Append(" diagnostic(s)");
        if (sorted.Count == 0)
        {
            logger.Log("suppressed", sb.ToString());
            return;
        }

        sb.Append(" (");
        for (var i = 0; i < sorted.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(sorted[i].Key);
            sb.Append(": ");
            sb.Append(sorted[i].Value);
        }
        sb.Append(')');

        logger.Log("suppressed", sb.ToString());
    }

    internal static SuppressionSummary CreateAggregatedSuppressionSummary(int totalSuppressed, IReadOnlyDictionary<string, int> suppressedByRule)
    {
        // Aggregate summaries intentionally preserve only merged counts. Individual
        // suppression records are file-scoped and are not meaningful after combining
        // multiple files, so the record list is empty here by design.
        return new SuppressionSummary(totalSuppressed, suppressedByRule, []);
    }

    internal static void AccumulateSuppression(SuppressionSummary summary, ref int totalSuppressed, ref Dictionary<string, int>? suppressedByRule)
    {
        var perRuleSuppression = summary.SuppressedByRule ?? EmptySuppressedByRule;
        var totalToAdd = summary.TotalSuppressed;
        var hasPerRuleSuppression = perRuleSuppression.Count > 0;
        if (totalToAdd == 0 && !hasPerRuleSuppression) return;

        if (hasPerRuleSuppression)
        {
            suppressedByRule ??= new Dictionary<string, int>(StringComparer.Ordinal);
            var summedSuppressed = 0;
            foreach (var kvp in perRuleSuppression)
            {
                summedSuppressed += kvp.Value;
                ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(suppressedByRule, kvp.Key, out _);
                existing += kvp.Value;
            }

            if (totalToAdd == 0)
            {
                totalToAdd = summedSuppressed;
            }
        }

        totalSuppressed += totalToAdd;
    }

    internal static void WriteRuleSummary(VerboseLogger logger, int activeRuleCount, int disabledRuleCount, ReadOnlySpan<string> disabledRuleIds, DocumentKind documentKind)
    {
        var kind = GetDocumentKindLabel(documentKind);
        logger.Log("rules", $"{activeRuleCount} enabled, {disabledRuleCount} disabled ({kind})");

        if (disabledRuleIds.Length > 0)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < disabledRuleIds.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(disabledRuleIds[i]);
            }
            logger.Log("rules", $"disabled: {sb}");
        }
    }

    internal static bool HasLoggedRuleSummaryForKind(DocumentKind kind, ref bool workflowLogged, ref bool actionLogged)
    {
        return kind == DocumentKind.ActionMetadata ? actionLogged : workflowLogged;
    }

    internal static void MarkRuleSummaryLogged(DocumentKind kind, ref bool workflowLogged, ref bool actionLogged)
    {
        if (kind == DocumentKind.ActionMetadata)
            actionLogged = true;
        else
            workflowLogged = true;
    }

    internal static void WriteFileTimingSummary(VerboseLogger logger, string filePath, DocumentKind documentKind, TimeSpan elapsed, int diagnosticCount, int suppressedCount)
    {
        var kind = GetDocumentKindLabel(documentKind);
        logger.LogFile(filePath, $"{kind}, {FormatMilliseconds(elapsed)} ms, {diagnosticCount} diagnostics, {suppressedCount} suppressed");
    }

    internal static string GetDocumentKindLabel(DocumentKind documentKind)
    {
        return documentKind switch
        {
            DocumentKind.Workflow => "workflow",
            DocumentKind.ActionMetadata => "action",
            _ => "unknown",
        };
    }

    internal static void WriteTotalTiming(VerboseLogger logger, int fileCount, TimeSpan elapsed, string verb = "checked")
    {
        logger.Log("total", $"{fileCount} file(s) {verb} in {FormatMilliseconds(elapsed)} ms");
    }

    internal static string FormatMilliseconds(TimeSpan elapsed)
    {
        return elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
    }

    private static DiagnosticSeverity? ParseSeverity(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            "info" => DiagnosticSeverity.Info,
            _ => null,
        };
    }
}

/// <summary>Lightweight result slot for parallel check. Holds caller-owned diagnostic copy.</summary>
internal readonly struct FileCheckResult
{
    public readonly OwnedDiagnostics Diagnostics;
    public readonly string FilePath;
    public readonly byte[]? Utf8Yaml;
    public readonly SuppressionSummary SuppressionSummary;
    public readonly DocumentKind DocumentKind;
    public readonly TimeSpan FileElapsed;
    public int FileDiagnosticCount => Diagnostics.Length;
    public int FileSuppressedCount => SuppressionSummary.TotalSuppressed;

    public FileCheckResult(OwnedDiagnostics diagnostics, string filePath, byte[]? utf8Yaml, SuppressionSummary suppressionSummary = default,
        DocumentKind documentKind = default, TimeSpan fileElapsed = default)
    {
        Diagnostics = diagnostics;
        FilePath = filePath;
        Utf8Yaml = utf8Yaml;
        SuppressionSummary = suppressionSummary;
        DocumentKind = documentKind;
        FileElapsed = fileElapsed;
    }
}

/// <summary>
/// Rule activation metadata captured once per DocumentKind in the parallel path.
/// Invariant within a single lint run for a given DocumentKind.
/// </summary>
internal readonly struct RuleActivationMetadata
{
    public readonly int ActiveRuleCount;
    public readonly int DisabledRuleCount;
    public readonly string[] DisabledRuleIds;

    public RuleActivationMetadata(int activeRuleCount, int disabledRuleCount, string[] disabledRuleIds)
    {
        ActiveRuleCount = activeRuleCount;
        DisabledRuleCount = disabledRuleCount;
        DisabledRuleIds = disabledRuleIds ?? [];
    }
}
