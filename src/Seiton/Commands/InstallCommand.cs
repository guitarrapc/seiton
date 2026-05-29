namespace Seiton.Commands;

internal static class InstallCommand
{
    private const string DefaultCiWorkflowPath = ".github/workflows/seiton.yml";

    public static int Run(bool skills, string target, string? output, bool force, bool ci = false, string? baseDirectory = null, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var outWriter = stdout ?? Console.Out;
        var errWriter = stderr ?? Console.Error;
        var cwd = baseDirectory ?? Directory.GetCurrentDirectory();

        if (!skills && !ci)
        {
            outWriter.WriteLine("Usage: seiton install --skills [--target claude|copilot|cursor] [--ci] [--output PATH] [--force]");
            return ExitCode.Success;
        }

        var exitCode = ExitCode.Success;

        if (skills)
        {
            exitCode = InstallSkills(target, output, force, cwd, outWriter, errWriter);
            if (exitCode != ExitCode.Success)
                return exitCode;
        }

        if (ci)
        {
            var ciOutput = skills ? null : output; // --output applies to --ci only when --skills is not also given
            exitCode = InstallCi(ciOutput, force, cwd, outWriter, errWriter);
        }

        return exitCode;
    }

    private static int InstallSkills(string target, string? output, bool force, string cwd, TextWriter outWriter, TextWriter errWriter)
    {
        var destDir = ResolveSkillDestination(target, output, cwd);
        if (destDir is null)
        {
            errWriter.WriteLine($"unknown target: {target}. Use 'claude', 'copilot', or 'cursor'.");
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

    private static int InstallCi(string? output, bool force, string cwd, TextWriter outWriter, TextWriter errWriter)
    {
        var destPath = output is not null
            ? Path.GetFullPath(output, cwd)
            : Path.Combine(cwd, DefaultCiWorkflowPath.Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(destPath) && !force)
        {
            errWriter.WriteLine($"workflow file already exists: {destPath}");
            errWriter.WriteLine("use --force to overwrite");
            return ExitCode.FatalError;
        }

        var content = CiWorkflowResources.GetWorkflowTemplate();
        if (content is null)
        {
            errWriter.WriteLine("no CI workflow template found in assembly resources");
            return ExitCode.FatalError;
        }

        var dir = Path.GetDirectoryName(destPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(destPath, content);

        var relativePath = Path.GetRelativePath(cwd, destPath);
        outWriter.WriteLine($"CI workflow installed to `{relativePath}`.");

        return ExitCode.Success;
    }

    private static string? ResolveSkillDestination(string target, string? output, string cwd)
    {
        if (output is not null)
            return Path.GetFullPath(output, cwd);

        return target switch
        {
            "claude" => Path.Combine(cwd, ".claude", "skills", "seiton"),
            "copilot" => Path.Combine(cwd, ".github", "instructions", "seiton"),
            "cursor" => Path.Combine(cwd, ".cursor", "rules", "seiton"),
            _ => null,
        };
    }
}
