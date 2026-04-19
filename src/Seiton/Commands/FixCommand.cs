using Seiton.Config;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Parsing;
using Seiton.Output;

namespace Seiton.Commands;

internal static class FixCommand
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
        bool dryRun,
        bool check,
        bool enablePinNetwork,
        bool enableImageNetwork)
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

        var (lintConfig, configDiags) = CliConfigBridge.LoadConfig(configPath, enablePinNetwork, enableImageNetwork);
        if (CheckCommand.HasConfigErrors(configDiags, resolvedFormat, colorEnabled, oneline))
            return ExitCode.FatalError;

        // Resolve input files
        string[] resolvedFiles;
        try
        {
            resolvedFiles = InputDiscovery.ResolveFiles(files);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCode.FatalError;
        }

        if (resolvedFiles.Length == 0 && !files.Contains("-"))
        {
            Console.Error.WriteLine("no workflow files found");
            return ExitCode.Success;
        }

        var engine = new LintEngine();
        var allDiagnostics = new List<Diagnostic>();
        var hasFixable = false;

        for (var i = 0; i < resolvedFiles.Length; i++)
        {
            var filePath = resolvedFiles[i];
            if (filePath == "-")
            {
                Console.Error.WriteLine("fix: stdin not supported for fix command");
                return ExitCode.InvalidOptions;
            }

            var utf8Yaml = File.ReadAllBytes(filePath);

            if (verbose)
                Console.Error.WriteLine($"fixing {filePath}...");

            var result = engine.Check(utf8Yaml, filePath, lintConfig);

            if (!result.HasFixableDiagnostics)
            {
                allDiagnostics.AddRange(result.Diagnostics);
                continue;
            }

            hasFixable = true;

            if (check)
            {
                // --check: report fixable but don't apply
                allDiagnostics.AddRange(result.Diagnostics);
                continue;
            }

            if (dryRun)
            {
                // --dry-run: print diff
                FixEngine.WriteUnifiedDiff(Console.Out, utf8Yaml, result.FixableDiagnostics, filePath);
                allDiagnostics.AddRange(result.Diagnostics);
                continue;
            }

            // Apply fixes in-place
            var fixResult = FixEngine.ApplyAndRelint(engine, utf8Yaml, filePath, result.FixableDiagnostics, lintConfig);
            File.WriteAllBytes(filePath, fixResult.UpdatedUtf8Yaml);
            allDiagnostics.AddRange(fixResult.After.Diagnostics);

            if (verbose)
                Console.Error.WriteLine($"  applied {result.FixableDiagnosticCount} fix(es) to {filePath}");
        }

        // Apply ignore patterns
        if (ignore.Length > 0)
        {
            var patterns = new System.Text.RegularExpressions.Regex[ignore.Length];
            for (var i = 0; i < ignore.Length; i++)
                patterns[i] = new System.Text.RegularExpressions.Regex(ignore[i], System.Text.RegularExpressions.RegexOptions.Compiled);

            allDiagnostics.RemoveAll(d =>
            {
                for (var i = 0; i < patterns.Length; i++)
                {
                    if (patterns[i].IsMatch(d.Message))
                        return true;
                }
                return false;
            });
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
            DiagnosticFormatter.Write(Console.Out, allDiagnostics, resolvedFormat, oneline, colorEnabled);

        if (check && hasFixable)
            return ExitCode.LintIssuesFound;

        return allDiagnostics.Count > 0 ? ExitCode.LintIssuesFound : ExitCode.Success;
    }
}
