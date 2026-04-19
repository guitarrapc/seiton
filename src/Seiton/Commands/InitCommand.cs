using Seiton.Core.Linting;

namespace Seiton.Commands;

internal static class InitCommand
{
    public static int Run(string output, bool force)
    {
        var targetPath = Path.GetFullPath(output);
        var directory = Path.GetDirectoryName(targetPath);

        if (directory is not null && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(targetPath) && !force)
        {
            Console.Error.WriteLine($"config file already exists: {targetPath}");
            Console.Error.WriteLine("use --force to overwrite");
            return ExitCode.FatalError;
        }

        var template = LintConfigLibrary.GenerateTemplateYaml();
        File.WriteAllText(targetPath, template);
        Console.WriteLine($"created {targetPath}");
        return ExitCode.Success;
    }
}
