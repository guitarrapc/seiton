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
        var engine = new LintEngine();
        var allDiagnostics = new List<Diagnostic>();
        Dictionary<string, byte[]>? sourceMap = resolvedFormat == OutputFormat.Text && !oneline ? new() : null;

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

            var result = engine.Check(utf8Yaml, filePath, lintConfig);
            allDiagnostics.AddRange(result.Diagnostics);
            sourceMap?.TryAdd(filePath, utf8Yaml);
        }

        // Apply ignore patterns
        if (ignore.Length > 0)
        {
            var patterns = DiagnosticsIgnoreFilter.CompileMessagePatterns(ignore);

            allDiagnostics.RemoveAll(d => DiagnosticsIgnoreFilter.IsMessageIgnored(patterns, d.Message));
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

        WriteSummary(allDiagnostics, resolvedFiles.Length);

        return HasActionableDiagnostics(allDiagnostics) ? ExitCode.LintIssuesFound : ExitCode.Success;
    }

    internal static void WriteSummary(List<Diagnostic> diagnostics, int fileCount)
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
            Console.Error.WriteLine($"0 issues in {fileCount} {(fileCount == 1 ? "file" : "files")}");
        else
            Console.Error.WriteLine($"{parts} in {fileCount} {(fileCount == 1 ? "file" : "files")}");
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
            DiagnosticFormatter.Write(Console.Error, configDiags, format, oneline, color);
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
