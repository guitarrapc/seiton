using Seiton.Config;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Output;

namespace Seiton.Commands;

internal static class CheckCommand
{
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

        if (verbose)
        {
            lintConfig ??= new LintConfig();
            lintConfig.Verbose = true;
        }

        CliConfigBridge.WriteResolvedConfigVerbose(Console.Error, verbose, configPath);

        // Resolve input files
        string[] resolvedFiles;
        try
        {
            resolvedFiles = InputDiscovery.ResolveFiles(files, includeActions);
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

        var hasStdin = files.Contains("-");

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

                if (verbose)
                    Console.Error.WriteLine($"checking {filePath}...");

                using var result = engine.Check(utf8Yaml, filePath, lintConfig);
                allDiagnostics.AddRange(result.Diagnostics.AsSpan());
                sourceMap?.TryAdd(filePath, utf8Yaml);
            }
        }
        else
        {
            // 2+ files: parallel with per-thread LintEngine isolation
            using var engines = new ThreadLocal<LintEngine>(
                static () => new LintEngine(), trackAllValues: false);
            var slots = new FileCheckResult[resolvedFiles.Length];

            Parallel.For(0, resolvedFiles.Length,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                i =>
                {
                    var filePath = resolvedFiles[i];
                    var utf8Yaml = File.ReadAllBytes(filePath);

                    if (verbose)
                        Console.Error.WriteLine($"checking {filePath}...");

                    var engine = engines.Value!;
                    using var result = engine.Check(utf8Yaml, filePath, lintConfig);
                    slots[i] = new FileCheckResult(result.CopyDiagnostics(), filePath, sourceMap is not null ? utf8Yaml : null);
                });

            // Aggregate in input order for stable output
            for (var i = 0; i < slots.Length; i++)
            {
                allDiagnostics.AddRange(slots[i].Diagnostics.AsSpan());
                if (sourceMap is not null && slots[i].Utf8Yaml is { } yaml)
                    sourceMap.TryAdd(slots[i].FilePath, yaml);
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

        WriteSummary(allDiagnostics, resolvedFiles.Length, verbose, showExitHint: minSeverity is null);

        return HasActionableDiagnostics(allDiagnostics) ? ExitCode.LintIssuesFound : ExitCode.Success;
    }

    internal static void WriteSummary(List<Diagnostic> diagnostics, int fileCount, bool verbose = false, bool showExitHint = false)
        => WriteSummary(Console.Error, diagnostics, fileCount, verbose, showExitHint);

    internal static void WriteSummary(TextWriter writer, List<Diagnostic> diagnostics, int fileCount, bool verbose = false, bool showExitHint = false)
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

    public FileCheckResult(OwnedDiagnostics diagnostics, string filePath, byte[]? utf8Yaml)
    {
        Diagnostics = diagnostics;
        FilePath = filePath;
        Utf8Yaml = utf8Yaml;
    }
}
