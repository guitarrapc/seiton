using Seiton.Config;
using Seiton.Core.Linting;
using Seiton.Output;

namespace Seiton.Commands;

internal static class ValidateCommand
{
    public static int Run(string? config)
    {
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

        if (configPath is null)
        {
            Console.Error.WriteLine("no config file found");
            return ExitCode.FatalError;
        }

        var result = LintConfigLibrary.ValidateFile(configPath);

        if (result.Diagnostics.Length > 0)
        {
            DiagnosticFormatter.Write(Console.Error, result.Diagnostics, OutputFormat.Text, oneline: false, color: false);
        }

        if (result.IsValid)
        {
            Console.WriteLine($"config valid: {configPath}");
            return ExitCode.Success;
        }

        return ExitCode.LintIssuesFound;
    }
}
