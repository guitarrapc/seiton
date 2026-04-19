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
        bool verbose)
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

        // Lint files
        var engine = new LintEngine();
        var allDiagnostics = new List<Diagnostic>();

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
            var threshold = ParseSeverity(minSeverity);
            if (threshold is not null)
                allDiagnostics.RemoveAll(d => d.Severity < threshold.Value);
        }

        // Output
        if (allDiagnostics.Count > 0)
        {
            DiagnosticFormatter.Write(Console.Out, allDiagnostics, resolvedFormat, oneline, colorEnabled);
            return ExitCode.LintIssuesFound;
        }

        return ExitCode.Success;
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

    static DiagnosticSeverity? ParseSeverity(string value)
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
