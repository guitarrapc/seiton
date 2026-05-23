using Seiton.Config;
using Seiton.Core.Linting;
using Seiton.Output;

namespace Seiton.Commands;

internal static class ValidateCommand
{
    public static int Run(string? config, TextWriter? output = null, TextWriter? error = null)
    {
        var outputWriter = output ?? Console.Out;
        var errorWriter = error ?? Console.Error;

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

        if (configPath is null)
        {
            errorWriter.WriteLine("no config file found");
            return ExitCode.FatalError;
        }

        var result = LintConfigLibrary.ValidateFile(configPath);

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
