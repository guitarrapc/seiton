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
    internal const int DefaultPerRuleBreakdownTopN = 10;

    internal readonly record struct CheckSummaryMetadata(int ExcludedCount = 0, int SuppressedCount = 0);

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
        VerboseLevel verboseLevel,
        bool includeActions,
        bool skipAgenticWorkflows = false,
        bool formatExplicitlySet = false)
    {
        var resolvedFormat = CliConfigBridge.ResolveOutputFormat(format, formatExplicitlySet);
        GitHubStepSummaryWriter.Reset();
        var colorEnabled = CliConfigBridge.ResolveColorEnabled(color, noColor);

        // Resolve config
        ConfigPathResolution configResolution;
        try
        {
            configResolution = CliConfigBridge.ResolveConfigPath(config);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCode.FatalError;
        }

        var configPath = configResolution.Path;
        var (lintConfig, configDiags) = CliConfigBridge.LoadConfig(configPath, enablePinNetwork: false, enableImageNetwork: false);
        if (HasConfigErrors(configDiags, resolvedFormat, colorEnabled, oneline))
            return ExitCode.FatalError;

        var skipAgentic = skipAgenticWorkflows || lintConfig?.Discovery.SkipAgenticWorkflows == true;
        var verboseLogger = VerboseLogger.Create(verboseLevel, Console.Error);

        if (verboseLevel >= VerboseLevel.Summary)
        {
            lintConfig ??= new LintConfig();
            lintConfig.Verbose = true;
        }

        if (verboseLogger.IsEnabled)
            verboseLogger.Log("config", configResolution.FormatVerboseMessage());

        var discoveryDirectory = configResolution.DiscoveryStartDirectory ?? Environment.CurrentDirectory;
        if (ShouldSuggestIncludeActions(includeActions, discoveryDirectory))
        {
            WriteIncludeActionsNotice(Console.Error);
        }

        // Resolve input files
        string[] resolvedFiles;
        try
        {
            resolvedFiles = InputDiscovery.ResolveFiles(files, includeActions, verboseLogger, skipAgentic);
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

        var lintRunConfig = CreateCheckLintConfig(lintConfig, resolvedFormat);

        // Lint files
        var allDiagnostics = new List<Diagnostic>();
        Dictionary<string, byte[]>? sourceMap = resolvedFormat.UsesRichTextOutput() && !oneline ? new() : null;
        var totalSuppressed = 0;
        Dictionary<string, int>? suppressedByRule = null;
        var excludedCount = 0;
        List<string>? excludedFiles = verboseLogger.IsEnabled ? [] : null;

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

                if (filePath != "-" && ExclusionMatcher.IsFileFullyExcluded(lintConfig?.Exclusions, filePath))
                {
                    excludedCount++;
                    excludedFiles?.Add(filePath);
                }

                if (verboseLogger.LogFileProgress)
                {
                    verboseLogger.Log($"checking {filePath}...");
                }

                var fileStart = verboseLogger.GetTimestamp();
                using var result = engine.Check(utf8Yaml, filePath, lintRunConfig);
                allDiagnostics.AddRange(result.Diagnostics.AsSpan());
                sourceMap?.TryAdd(filePath, utf8Yaml);
                if (verboseLogger.IsEnabled)
                {
                    AccumulateSuppression(result.SuppressionSummary, ref totalSuppressed, ref suppressedByRule);
                }
                else
                {
                    totalSuppressed += result.SuppressionSummary.TotalSuppressed;
                }

                if (verboseLogger.IsEnabled)
                {
                    if (result.DocumentKind != DocumentKind.Unknown
                        && !HasLoggedRuleSummaryForKind(result.DocumentKind, ref workflowRuleSummaryLogged, ref actionRuleSummaryLogged))
                    {
                        WriteRuleSummary(verboseLogger, result.ActiveRuleCount, result.DisabledRuleCount, result.DisabledRuleIds, result.DocumentKind);
                        MarkRuleSummaryLogged(result.DocumentKind, ref workflowRuleSummaryLogged, ref actionRuleSummaryLogged);
                    }
                }

                if (verboseLogger.LogFileProgress)
                {
                    var fileElapsed = verboseLogger.GetElapsedTime(fileStart);
                    var suppressedCount = result.SuppressionSummary.TotalSuppressed;
                    WriteFileTimingSummary(verboseLogger, filePath, result.DocumentKind, fileElapsed, result.DiagnosticCount, suppressedCount);
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

                    if (verboseLogger.LogFileProgress)
                    {
                        // Progress visibility matters more than ordering for this line in parallel mode.
                        verboseLogger.Log($"checking {filePath}...");
                    }

                    var fileStart = verboseLogger.GetTimestamp();
                    var engine = engines.Value!;
                    using var result = engine.Check(utf8Yaml, filePath, lintRunConfig);
                    var fileElapsed = verboseLogger.LogFileProgress ? verboseLogger.GetElapsedTime(fileStart) : default;
                    var isExcluded = ExclusionMatcher.IsFileFullyExcluded(lintConfig?.Exclusions, filePath);

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
                        result.SuppressionSummary,
                        GetSlotDocumentKind(verboseLogger, result.DocumentKind),
                        verboseLogger.LogFileProgress ? fileElapsed : default,
                        isExcluded);
                });

            // Aggregate in input order for stable output
            for (var i = 0; i < slots.Length; i++)
            {
                allDiagnostics.AddRange(slots[i].Diagnostics.AsSpan());
                if (sourceMap is not null && slots[i].Utf8Yaml is { } yaml)
                    sourceMap.TryAdd(slots[i].FilePath, yaml);

                if (slots[i].IsFullyExcluded)
                {
                    excludedCount++;
                    excludedFiles?.Add(slots[i].FilePath);
                }

                if (verboseLogger.IsEnabled)
                {
                    AccumulateSuppression(slots[i].SuppressionSummary, ref totalSuppressed, ref suppressedByRule);
                }
                else
                {
                    totalSuppressed += slots[i].SuppressionSummary.TotalSuppressed;
                }

                if (verboseLogger.IsEnabled)
                {
                    if (slots[i].DocumentKind != DocumentKind.Unknown
                        && !HasLoggedRuleSummaryForKind(slots[i].DocumentKind, ref workflowRuleSummaryLogged, ref actionRuleSummaryLogged))
                    {
                        var meta = slots[i].DocumentKind == DocumentKind.ActionMetadata ? actionRuleMetadata : workflowRuleMetadata;
                        WriteRuleSummary(verboseLogger, meta.ActiveRuleCount, meta.DisabledRuleCount, meta.DisabledRuleIds, slots[i].DocumentKind);
                        MarkRuleSummaryLogged(slots[i].DocumentKind, ref workflowRuleSummaryLogged, ref actionRuleSummaryLogged);
                    }
                }

                if (verboseLogger.LogFileProgress)
                {
                    WriteFileTimingSummary(verboseLogger, slots[i].FilePath, slots[i].DocumentKind, slots[i].FileElapsed, slots[i].FileDiagnosticCount, slots[i].FileSuppressedCount);
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
        {
            DiagnosticFormatter.WriteToStandardOutput(allDiagnostics, resolvedFormat, oneline, colorEnabled, sourceMap);
        }

        if (totalSuppressed > 0 && verboseLogger.IsEnabled)
        {
            WriteSuppressionSummary(verboseLogger,
                CreateAggregatedSuppressionSummary(totalSuppressed, suppressedByRule ?? EmptySuppressedByRule));
        }
        if (verboseLogger.IsEnabled && excludedFiles is { Count: > 0 })
        {
            WriteExcludedSummary(verboseLogger, excludedFiles, showAll: verboseLevel >= VerboseLevel.Files);
        }

        var summaryMetadata = new CheckSummaryMetadata(excludedCount, totalSuppressed);
        WriteSummary(allDiagnostics, resolvedFiles.Length, resolvedFormat, verboseLevel >= VerboseLevel.Summary, showExitHint: minSeverity is null, metadata: summaryMetadata);
        if (ShouldShowInitHint(configResolution, resolvedFormat, allDiagnostics))
        {
            WriteInitHint(Console.Error);
        }

        if (verboseLogger.IsEnabled)
            WriteTotalTiming(verboseLogger, resolvedFiles.Length, verboseLogger.GetElapsedTime(totalStart));

        return HasActionableDiagnostics(allDiagnostics) ? ExitCode.LintIssuesFound : ExitCode.Success;
    }

    internal static LintConfig? CreateCheckLintConfig(LintConfig? lintConfig, OutputFormat format)
    {
        if (format != OutputFormat.Json)
        {
            return lintConfig;
        }

        if (lintConfig is null)
        {
            return new LintConfig
            {
                Fix = new FixConfig { Enabled = true },
                Network = new NetworkConfig(),
                Output = new OutputConfig(),
            };
        }

        if (lintConfig.Fix.Enabled)
        {
            return lintConfig;
        }

        return new LintConfig
        {
            Rules = lintConfig.Rules,
            Exclusions = lintConfig.Exclusions,
            Fix = lintConfig.Fix with { Enabled = true },
            Network = lintConfig.Network,
            Output = lintConfig.Output,
            Discovery = lintConfig.Discovery,
            SkipSuppressionSummary = lintConfig.SkipSuppressionSummary,
            Verbose = lintConfig.Verbose,
            ConfigFilePath = lintConfig.ConfigFilePath,
        };
    }

    internal static void WriteSummary(List<Diagnostic> diagnostics, int fileCount, OutputFormat format = OutputFormat.Text, bool verbose = false, bool showExitHint = false, bool showPerFile = true, CheckSummaryMetadata metadata = default, bool isRemainMode = false)
        => WriteSummary(Console.Error, diagnostics, fileCount, format, verbose, showExitHint, showPerFile, metadata, isRemainMode);

    internal static void WriteSummary(TextWriter writer, List<Diagnostic> diagnostics, int fileCount, OutputFormat format = OutputFormat.Text, bool verbose = false, bool showExitHint = false, bool showPerFile = true, CheckSummaryMetadata metadata = default, bool isRemainMode = false)
    {
        CountSeverityTotals(diagnostics, out var errors, out var warnings, out var infos);

        if (!GitHubStepSummaryWriter.TryAppend(format, jobSummary =>
                WriteSummaryContent(jobSummary, diagnostics, fileCount, verbose, showPerFile, metadata, isRemainMode, errors, warnings, infos)))
        {
            WriteSummaryContent(writer, diagnostics, fileCount, verbose, showPerFile, metadata, isRemainMode, errors, warnings, infos);
        }

        if (!verbose && ShouldOfferFullPerRuleBreakdownHint(diagnostics))
            writer.WriteLine("hint: re-run with --verbose for the full per-rule breakdown");

        if (showExitHint && errors == 0 && warnings > 0)
            writer.WriteLine("hint: use --min-severity error to treat warnings as non-blocking in CI");
    }

    private static bool ShouldOfferFullPerRuleBreakdownHint(List<Diagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return false;
        }

        var distinctRules = 0;
        HashSet<string>? seen = null;
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var ruleId = diagnostics[i].RuleId;
            if (ruleId is null)
            {
                continue;
            }

            seen ??= new HashSet<string>(StringComparer.Ordinal);
            if (!seen.Add(ruleId))
            {
                continue;
            }

            distinctRules++;
            if (distinctRules > DefaultPerRuleBreakdownTopN)
            {
                return true;
            }
        }

        return false;
    }

    private static void CountSeverityTotals(List<Diagnostic> diagnostics, out int errors, out int warnings, out int infos)
    {
        errors = 0;
        warnings = 0;
        infos = 0;
        for (var i = 0; i < diagnostics.Count; i++)
        {
            switch (diagnostics[i].Severity)
            {
                case DiagnosticSeverity.Error: errors++; break;
                case DiagnosticSeverity.Warning: warnings++; break;
                default: infos++; break;
            }
        }
    }

    private static void WriteSummaryContent(
        TextWriter writer,
        List<Diagnostic> diagnostics,
        int fileCount,
        bool verbose,
        bool showPerFile,
        CheckSummaryMetadata metadata,
        bool isRemainMode,
        int errors,
        int warnings,
        int infos)
    {
        var parts = new System.Text.StringBuilder();
        if (errors > 0) parts.Append(errors == 1 ? "1 error" : $"{errors} errors");
        if (warnings > 0) { if (parts.Length > 0) parts.Append(", "); parts.Append(warnings == 1 ? "1 warning" : $"{warnings} warnings"); }
        if (infos > 0) { if (parts.Length > 0) parts.Append(", "); parts.Append(infos == 1 ? "1 info" : $"{infos} infos"); }

        if (isRemainMode)
        {
            // In fix mode, use "remain" wording to clarify these are post-fix residual issues.
            // Count files that actually have remaining diagnostics (not total files checked).
            var filesWithIssues = 0;
            HashSet<string>? seen = null;
            for (var i = 0; i < diagnostics.Count; i++)
            {
                var file = diagnostics[i].FilePath;
                if (file is null) continue;
                seen ??= new HashSet<string>(StringComparer.Ordinal);
                if (seen.Add(file)) filesWithIssues++;
            }

            if (parts.Length == 0)
                writer.WriteLine(AppendSummaryMetadata("0 issues remain", metadata));
            else
            {
                var total = errors + warnings + infos;
                var verb = total == 1 ? "remains" : "remain";
                writer.WriteLine(AppendSummaryMetadata($"{parts} {verb} in {filesWithIssues} {(filesWithIssues == 1 ? "file" : "files")}", metadata));
            }
        }
        else
        {
            if (parts.Length == 0)
                writer.WriteLine(AppendSummaryMetadata($"0 issues in {fileCount} {(fileCount == 1 ? "file" : "files")}", metadata));
            else
                writer.WriteLine(AppendSummaryMetadata($"{parts} in {fileCount} {(fileCount == 1 ? "file" : "files")}", metadata));
        }

        if (showPerFile && diagnostics.Count > 0)
        {
            WritePerFileBreakdown(writer, diagnostics);
        }

        if (diagnostics.Count > 0)
        {
            var maxRows = verbose ? int.MaxValue : DefaultPerRuleBreakdownTopN;
            WritePerRuleBreakdown(writer, diagnostics, isRemainMode, maxRows);
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

        // Compute column widths for table formatting
        var maxFileLen = 4; // "File".Length
        for (var i = 0; i < sorted.Count; i++)
        {
            var name = Path.GetFileName(sorted[i].Key);
            if (name.Length > maxFileLen)
                maxFileLen = name.Length;
        }

        var maxErrorLen = 6; // "Errors".Length
        var maxWarnLen = 8; // "Warnings".Length
        var hasInfos = false;
        var maxInfoLen = 5; // "Infos".Length
        for (var i = 0; i < sorted.Count; i++)
        {
            var errDigits = CountDigits(sorted[i].Value.Errors);
            var warnDigits = CountDigits(sorted[i].Value.Warnings);
            var infoDigits = CountDigits(sorted[i].Value.Infos);
            if (errDigits > maxErrorLen) maxErrorLen = errDigits;
            if (warnDigits > maxWarnLen) maxWarnLen = warnDigits;
            if (infoDigits > maxInfoLen) maxInfoLen = infoDigits;
            hasInfos |= sorted[i].Value.Infos > 0;
        }

        // Write table with blank line separator before it
        writer.WriteLine();

        // Header row
        writer.Write("| File");
        writer.Write(new string(' ', maxFileLen - 4));
        writer.Write(" | ");
        writer.Write("Errors");
        writer.Write(new string(' ', maxErrorLen - 6));
        writer.Write(" | ");
        writer.Write("Warnings");
        writer.Write(new string(' ', maxWarnLen - 8));
        if (hasInfos)
        {
            writer.Write(" | ");
            writer.Write("Infos");
            writer.Write(new string(' ', maxInfoLen - 5));
        }
        writer.WriteLine(" |");

        // Separator row (right-aligned numeric columns)
        writer.Write('|');
        writer.Write(new string('-', maxFileLen + 2));
        writer.Write('|');
        writer.Write(new string('-', maxErrorLen + 1));
        writer.Write(":|");
        writer.Write(new string('-', maxWarnLen + 1));
        writer.Write(":|");
        if (hasInfos)
        {
            writer.Write(new string('-', maxInfoLen + 1));
            writer.Write(":|");
        }
        writer.WriteLine();

        // Data rows
        for (var i = 0; i < sorted.Count; i++)
        {
            var (filePath, (errors, warnings, infos)) = sorted[i];
            var displayName = Path.GetFileName(filePath);

            writer.Write("| ");
            writer.Write(displayName);
            writer.Write(new string(' ', maxFileLen - displayName.Length));
            writer.Write(" | ");
            var errStr = errors.ToString();
            writer.Write(new string(' ', maxErrorLen - errStr.Length));
            writer.Write(errStr);
            writer.Write(" | ");
            var warnStr = warnings.ToString();
            writer.Write(new string(' ', maxWarnLen - warnStr.Length));
            writer.Write(warnStr);
            if (hasInfos)
            {
                writer.Write(" | ");
                var infoStr = infos.ToString();
                writer.Write(new string(' ', maxInfoLen - infoStr.Length));
                writer.Write(infoStr);
            }
            writer.WriteLine(" |");
        }
    }

    private static void WritePerRuleBreakdown(TextWriter writer, List<Diagnostic> diagnostics, bool isRemainMode = false, int maxRows = int.MaxValue)
    {
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

        WritePerRuleCountTable(writer, ruleCounts, isRemainMode ? "Remaining" : "Count", maxRows);
    }

    internal static void WritePerRuleCountTable(
        TextWriter writer,
        IReadOnlyDictionary<string, int> ruleCounts,
        string countHeader,
        int maxRows = int.MaxValue)
    {
        if (ruleCounts.Count == 0)
        {
            return;
        }

        var sorted = new List<KeyValuePair<string, int>>(ruleCounts.Count);
        foreach (var kvp in ruleCounts)
        {
            sorted.Add(kvp);
        }

        sorted.Sort(static (a, b) =>
        {
            var byCount = b.Value.CompareTo(a.Value);
            return byCount != 0 ? byCount : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
        });

        var rowCount = sorted.Count;
        if (maxRows < rowCount)
        {
            rowCount = maxRows;
        }

        var countHeaderLen = countHeader.Length;
        var maxRuleLen = 4;
        for (var i = 0; i < rowCount; i++)
        {
            if (sorted[i].Key.Length > maxRuleLen)
            {
                maxRuleLen = sorted[i].Key.Length;
            }
        }

        var maxCountLen = countHeaderLen;
        for (var i = 0; i < rowCount; i++)
        {
            var digits = CountDigits(sorted[i].Value);
            if (digits > maxCountLen)
            {
                maxCountLen = digits;
            }
        }

        writer.WriteLine();
        writer.Write("| Rule");
        writer.Write(new string(' ', maxRuleLen - 4));
        writer.Write(" | ");
        writer.Write(countHeader);
        writer.Write(new string(' ', maxCountLen - countHeaderLen));
        writer.WriteLine(" |");

        writer.Write('|');
        writer.Write(new string('-', maxRuleLen + 2));
        writer.Write('|');
        writer.Write(new string('-', maxCountLen + 1));
        writer.WriteLine(":|");

        for (var i = 0; i < rowCount; i++)
        {
            var rule = sorted[i].Key;
            var count = sorted[i].Value;
            writer.Write("| ");
            writer.Write(rule);
            writer.Write(new string(' ', maxRuleLen - rule.Length));
            writer.Write(" | ");
            var countStr = count.ToString();
            writer.Write(new string(' ', maxCountLen - countStr.Length));
            writer.Write(countStr);
            writer.WriteLine(" |");
        }
    }

    internal static int CountDigits(int value) => DecimalFormat.CountDigits(value);

    internal static void WriteNetworkFixHint(TextWriter writer, List<Diagnostic> diagnostics, bool enablePinNetwork, bool enableImageNetwork)
    {
        var needsPin = false;
        var needsImage = false;
        var hasUnfixedUnpinnedImage = false;
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var diagnostic = diagnostics[i];
            var ruleId = diagnostic.RuleId;
            if (ruleId is null) continue;
            if (!enablePinNetwork && ruleId == "unpinned-uses") needsPin = true;
            if (!enableImageNetwork && ruleId == "unpinned-image") needsImage = true;
            if (enableImageNetwork && ruleId == "unpinned-image" && diagnostic.Fix is null)
            {
                hasUnfixedUnpinnedImage = true;
            }

            if (needsPin && needsImage) break;
        }

        if (needsPin && needsImage)
            writer.WriteLine("hint: re-run with --enable-pin-network --enable-image-network to auto-fix pinning");
        else if (needsPin)
            writer.WriteLine("hint: re-run with --enable-pin-network to auto-fix action pinning");
        else if (needsImage)
            writer.WriteLine("hint: re-run with --enable-image-network to auto-fix image pinning");
        else if (hasUnfixedUnpinnedImage)
        {
            writer.WriteLine(
                "hint: tagless or :latest images are not auto-pinned by default (fix.images.exclude-tags); use an explicit tag (e.g. redis:7) or clear exclude-tags in config");
        }
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
            DiagnosticFormatter.WriteToTextWriter(error, configDiags, format, oneline, color);
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

    internal static void WriteExcludedSummary(VerboseLogger logger, IReadOnlyList<string> excludedFiles, bool showAll)
    {
        if (excludedFiles.Count == 0)
        {
            return;
        }

        if (showAll)
        {
            for (var i = 0; i < excludedFiles.Count; i++)
            {
                logger.Log("excluded", excludedFiles[i]);
            }

            return;
        }

        var maxPreview = 5;
        var previewCount = Math.Min(maxPreview, excludedFiles.Count);
        var sb = new System.Text.StringBuilder();
        sb.Append(excludedFiles.Count);
        sb.Append(" file(s): ");
        for (var i = 0; i < previewCount; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(excludedFiles[i]);
        }

        if (excludedFiles.Count > previewCount)
        {
            sb.Append(", ... (+");
            sb.Append(excludedFiles.Count - previewCount);
            sb.Append(" more; use -vv to show all)");
        }

        logger.Log("excluded", sb.ToString());
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

    internal static void WriteFixTotalTiming(
        VerboseLogger logger,
        int processedFileCount,
        int modifiedFileCount,
        TimeSpan elapsed,
        bool dryRun = false)
    {
        var modifiedVerb = dryRun ? "would be modified" : "modified";
        logger.Log("total", $"{processedFileCount} file(s) processed, {modifiedFileCount} {modifiedVerb} in {FormatMilliseconds(elapsed)} ms");
    }

    internal static string FormatMilliseconds(TimeSpan elapsed)
    {
        return elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
    }

    internal static string AppendSummaryMetadata(string summaryLine, CheckSummaryMetadata metadata)
    {
        if (metadata.ExcludedCount <= 0 && metadata.SuppressedCount <= 0)
        {
            return summaryLine;
        }

        var suffix = new System.Text.StringBuilder(" (");
        if (metadata.ExcludedCount > 0)
        {
            suffix.Append(metadata.ExcludedCount);
            suffix.Append(" excluded");
        }

        if (metadata.SuppressedCount > 0)
        {
            if (metadata.ExcludedCount > 0)
            {
                suffix.Append(", ");
            }

            suffix.Append(metadata.SuppressedCount);
            suffix.Append(" suppressed");
        }

        suffix.Append(')');
        return summaryLine + suffix;
    }

    internal static bool ShouldShowInitHint(ConfigPathResolution configResolution, OutputFormat format, IReadOnlyList<Diagnostic> diagnostics)
    {
        if (configResolution.Path is not null || !format.UsesRichTextOutput() || IsCi())
        {
            return false;
        }

        var actionable = 0;
        for (var i = 0; i < diagnostics.Count; i++)
        {
            if (diagnostics[i].Severity >= DiagnosticSeverity.Warning)
            {
                actionable++;
            }
        }

        return actionable >= 20;
    }

    internal static void WriteInitHint(TextWriter writer)
    {
        writer.WriteLine("hint: many issues detected with default config; run 'seiton init' to create .github/seiton.yaml and customize exclusions");
    }

    internal static void WriteIncludeActionsNotice(TextWriter writer)
    {
        writer.WriteLine("notice: composite actions are not included; re-run with --include-actions");
    }

    internal static bool ShouldSuggestIncludeActions(bool includeActions, string? discoveryStartDirectory)
    {
        if (includeActions || string.IsNullOrWhiteSpace(discoveryStartDirectory))
        {
            return false;
        }

        return HasGitHubActionsDirectoryUnderCwd(discoveryStartDirectory);
    }

    internal static DocumentKind GetSlotDocumentKind(VerboseLogger logger, DocumentKind documentKind)
        => logger.IsEnabled ? documentKind : default;

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

    private static bool IsCi() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));

    private static bool HasGitHubActionsDirectoryUnderCwd(string startDirectory)
    {
        return Directory.Exists(Path.Combine(Path.GetFullPath(startDirectory), ".github", "actions"));
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
    public readonly bool IsFullyExcluded;
    public int FileDiagnosticCount => Diagnostics.Length;
    public int FileSuppressedCount => SuppressionSummary.TotalSuppressed;

    public FileCheckResult(OwnedDiagnostics diagnostics, string filePath, byte[]? utf8Yaml, SuppressionSummary suppressionSummary = default,
        DocumentKind documentKind = default, TimeSpan fileElapsed = default, bool isFullyExcluded = false)
    {
        Diagnostics = diagnostics;
        FilePath = filePath;
        Utf8Yaml = utf8Yaml;
        SuppressionSummary = suppressionSummary;
        DocumentKind = documentKind;
        FileElapsed = fileElapsed;
        IsFullyExcluded = isFullyExcluded;
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
