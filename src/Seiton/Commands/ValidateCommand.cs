using System.Globalization;
using Seiton.Cli;
using Seiton.Config;
using Seiton.Core.Linting;
using Seiton.Output;

namespace Seiton.Commands;

internal static class ValidateCommand
{
    public static int Run(
        string? config,
        VerboseLevel verboseLevel = VerboseLevel.Off,
        string? baseDirectory = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        var outputWriter = output ?? Console.Out;
        var errorWriter = error ?? Console.Error;
        var verboseLogger = VerboseLogger.Create(verboseLevel, errorWriter);
        var repositoryRoot = baseDirectory ?? Directory.GetCurrentDirectory();

        ConfigPathResolution configResolution;
        try
        {
            configResolution = CliConfigBridge.ResolveConfigPath(config);
        }
        catch (FileNotFoundException ex)
        {
            errorWriter.WriteLine(ex.Message);
            return ExitCode.FatalError;
        }

        var configPath = configResolution.Path;
        if (verboseLogger.IsEnabled)
        {
            verboseLogger.Log("config", configResolution.FormatVerboseMessage());
        }

        if (configPath is null)
        {
            errorWriter.WriteLine("no config file found");
            return ExitCode.FatalError;
        }

        var parseStart = verboseLogger.GetTimestamp();
        var result = LintConfigLibrary.ValidateFile(configPath);
        var diagnostics = new List<Core.Parsing.Diagnostic>(result.Diagnostics);

        if (result.Config is not null)
        {
            var skipAgentic = result.Config.Discovery?.SkipAgenticWorkflows ?? false;
            var workflowFiles = InputDiscovery.ResolveFiles(
                [],
                includeActions: false,
                verboseLogger,
                skipAgenticWorkflows: skipAgentic,
                startDirectory: repositoryRoot);

            var jobIdDiagnostics = ExclusionJobIdValidator.Validate(
                result.Config,
                workflowFiles,
                configPath,
                out var jobIdStats);

            if (jobIdDiagnostics.Length > 0)
            {
                diagnostics.AddRange(jobIdDiagnostics);
            }

            if (verboseLogger.IsEnabled)
            {
                verboseLogger.Log(
                    "job-id-check",
                    $"{jobIdStats.WorkflowsScanned} workflow file(s) scanned for {jobIdStats.JobScopedExclusionsChecked} job-scoped exclusion(s)");
            }
        }

        if (verboseLogger.IsEnabled)
        {
            var parseElapsed = verboseLogger.GetElapsedTime(parseStart);
            verboseLogger.Log("parse", $"{parseElapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms");

            if (result.Config is not null)
            {
                var ruleStatuses = RuleListResolver.Resolve(result.Config);
                var enabledRuleCount = 0;
                for (var i = 0; i < ruleStatuses.Count; i++)
                {
                    if (ruleStatuses[i].Enabled)
                    {
                        enabledRuleCount++;
                    }
                }

                var exclusionCount = result.Config.Exclusions?.Count ?? 0;
                verboseLogger.Log("rules", $"{enabledRuleCount} enabled");
                verboseLogger.Log("exclusions", $"{exclusionCount} entry(s)");
            }
            else
            {
                verboseLogger.Log("rules", "n/a (config invalid)");
                verboseLogger.Log("exclusions", "n/a (config invalid)");
            }
        }

        if (diagnostics.Count > 0)
        {
            DiagnosticFormatter.WriteToTextWriter(errorWriter, diagnostics, OutputFormat.Text, oneline: false, color: false);
        }

        var isValid = true;
        for (var i = 0; i < diagnostics.Count; i++)
        {
            if (diagnostics[i].Severity == Core.Parsing.DiagnosticSeverity.Error)
            {
                isValid = false;
                break;
            }
        }

        if (isValid)
        {
            outputWriter.WriteLine($"config valid: {configPath}");
            return ExitCode.Success;
        }

        return ExitCode.LintIssuesFound;
    }
}
