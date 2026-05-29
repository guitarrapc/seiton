namespace Seiton.Commands;

internal static class InstallCommand
{
    public static int Run(bool skills, string target, string? output, bool force, string? baseDirectory = null, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var outWriter = stdout ?? Console.Out;
        var errWriter = stderr ?? Console.Error;
        var cwd = baseDirectory ?? Directory.GetCurrentDirectory();

        if (!skills)
        {
            outWriter.WriteLine("Usage: seiton install --skills [--target claude|copilot] [--force]");
            return ExitCode.Success;
        }

        var destDir = ResolveDestination(target, output, cwd);
        if (destDir is null)
        {
            errWriter.WriteLine($"unknown target: {target}. Use 'claude' or 'copilot'.");
            return ExitCode.InvalidOptions;
        }

        if (Directory.Exists(destDir) && !force)
        {
            errWriter.WriteLine($"skill directory already exists: {destDir}");
            errWriter.WriteLine("use --force to overwrite");
            return ExitCode.FatalError;
        }

        var files = SkillResources.GetAllSkillFiles();
        if (files.Count == 0)
        {
            errWriter.WriteLine("no skill files found in assembly resources");
            return ExitCode.FatalError;
        }

        foreach (var (relativePath, content) in files)
        {
            var filePath = Path.Combine(destDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, content);
        }

        var relativeDestDir = Path.GetRelativePath(cwd, destDir);
        outWriter.WriteLine($"Skills installed to `{relativeDestDir}`.");
        outWriter.WriteLine();
        outWriter.WriteLine("Files:");
        foreach (var (relativePath, _) in files)
        {
            outWriter.WriteLine($"  {Path.Combine(relativeDestDir, relativePath.Replace('/', Path.DirectorySeparatorChar))}");
        }

        return ExitCode.Success;
    }

    private static string? ResolveDestination(string target, string? output, string cwd)
    {
        if (output is not null)
            return Path.GetFullPath(output, cwd);

        return target switch
        {
            "claude" => Path.Combine(cwd, ".claude", "skills", "seiton"),
            "copilot" => Path.Combine(cwd, ".github", "instructions", "seiton"),
            _ => null,
        };
    }
}
