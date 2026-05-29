using Seiton.Commands;

namespace Seiton.Tests;

public sealed class InstallCommandTests
{
    [Test]
    public async Task Run_Skills_DefaultTarget_CreatesSkillFiles()
    {
        var dir = CreateTempDir();
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: true, target: "claude", output: null, force: false, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            var skillPath = Path.Combine(dir, ".claude", "skills", "seiton", "SKILL.md");
            await Assert.That(File.Exists(skillPath)).IsTrue();

            var content = File.ReadAllText(skillPath);
            await Assert.That(content).Contains("name: seiton");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_Skills_ExistingDirectory_WithoutForce_ReturnsFatalError()
    {
        var dir = CreateTempDir();
        try
        {
            // Pre-create the target directory
            var skillDir = Path.Combine(dir, ".claude", "skills", "seiton");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "old content");

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: true, target: "claude", output: null, force: false, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.FatalError);
            await Assert.That(stderr.ToString()).Contains("already exists");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_Skills_ExistingDirectory_WithForce_OverwritesSuccessfully()
    {
        var dir = CreateTempDir();
        try
        {
            // Pre-create the target directory with old content
            var skillDir = Path.Combine(dir, ".claude", "skills", "seiton");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "old content");

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: true, target: "claude", output: null, force: true, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            var content = File.ReadAllText(Path.Combine(skillDir, "SKILL.md"));
            await Assert.That(content).Contains("name: seiton");
            await Assert.That(content).DoesNotContain("old content");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_WithoutSkills_ShowsUsage()
    {
        var dir = CreateTempDir();
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: false, target: "claude", output: null, force: false, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(stdout.ToString()).Contains("seiton install --skills");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_Skills_UnknownTarget_ReturnsInvalidOptions()
    {
        var dir = CreateTempDir();
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: true, target: "unknown", output: null, force: false, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.InvalidOptions);
            await Assert.That(stderr.ToString()).Contains("unknown target");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_Skills_CopilotTarget_CreatesSkillFiles()
    {
        var dir = CreateTempDir();
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: true, target: "copilot", output: null, force: false, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            var skillPath = Path.Combine(dir, ".github", "instructions", "seiton", "SKILL.md");
            await Assert.That(File.Exists(skillPath)).IsTrue();
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_Skills_OutputsInstalledFilePaths()
    {
        var dir = CreateTempDir();
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: true, target: "claude", output: null, force: false, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            var output = stdout.ToString();
            await Assert.That(output).Contains("SKILL.md");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_Skills_CustomOutput_CreatesAtSpecifiedPath()
    {
        var dir = CreateTempDir();
        var customPath = Path.Combine(dir, "custom", "location");
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: true, target: "claude", output: customPath, force: false, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(File.Exists(Path.Combine(customPath, "SKILL.md"))).IsTrue();
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDirectory(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
