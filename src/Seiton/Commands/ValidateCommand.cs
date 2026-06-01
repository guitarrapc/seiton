using System.Globalization;
using Seiton.Cli;
using Seiton.Config;
using Seiton.Core.Linting;
using Seiton.Output;

namespace Seiton.Commands;

internal static class ValidateCommand
{
    public static int Run(string? config, VerboseLevel verboseLevel = VerboseLevel.Off, TextWriter? output = null, TextWriter? error = null)
    {
        var outputWriter = output ?? Console.Out;
        var errorWriter = error ?? Console.Error;
        var verboseLogger = VerboseLogger.Create(verboseLevel, errorWriter);

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

        if (result.Diagnostics.Length > 0)
        {
            DiagnosticFormatter.Write(errorWriter, result.Diagnostics, OutputFormat.Text, oneline: false, color: false);
        }

        if (result.IsValid)
        {
            outputWriter.WriteLine($"config valid: {configPath}");
            return ExitCode.Success;
        }

        return ExitCode.LintIssuesFound;
    }
}
