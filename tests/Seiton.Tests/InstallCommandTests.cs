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

            var exitCode = InstallCommand.Run(skills: true, target: "claude", output: null, force: false, ci: false, baseDirectory: dir, stdout, stderr);

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

            var exitCode = InstallCommand.Run(skills: true, target: "claude", output: null, force: false, ci: false, baseDirectory: dir, stdout, stderr);

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

            var exitCode = InstallCommand.Run(skills: true, target: "claude", output: null, force: true, ci: false, baseDirectory: dir, stdout, stderr);

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

            var exitCode = InstallCommand.Run(skills: false, target: "claude", output: null, force: false, ci: false, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(stdout.ToString()).Contains("--output PATH");
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

            var exitCode = InstallCommand.Run(skills: true, target: "unknown", output: null, force: false, ci: false, baseDirectory: dir, stdout, stderr);

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

            var exitCode = InstallCommand.Run(skills: true, target: "copilot", output: null, force: false, ci: false, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            var baseDir = Path.Combine(dir, ".github", "instructions", "seiton");
            await Assert.That(File.Exists(Path.Combine(baseDir, "SKILL.md"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(baseDir, "references", "rules.md"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(baseDir, "references", "fix-mode.md"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(baseDir, "references", "configuration.md"))).IsTrue();

            // Verify output message references the correct path
            var output = stdout.ToString();
            await Assert.That(output).Contains(".github");
            await Assert.That(output).Contains("instructions");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_Skills_CopilotTarget_ExistingWithForce_Overwrites()
    {
        var dir = CreateTempDir();
        try
        {
            var skillDir = Path.Combine(dir, ".github", "instructions", "seiton");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "old copilot content");

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: true, target: "copilot", output: null, force: true, ci: false, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            var content = File.ReadAllText(Path.Combine(skillDir, "SKILL.md"));
            await Assert.That(content).Contains("name: seiton");
            await Assert.That(content).DoesNotContain("old copilot content");
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

            var exitCode = InstallCommand.Run(skills: true, target: "claude", output: null, force: false, ci: false, baseDirectory: dir, stdout, stderr);

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
    public async Task Run_Skills_CursorTarget_CreatesSkillFiles()
    {
        var dir = CreateTempDir();
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: true, target: "cursor", output: null, force: false, ci: false, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            var baseDir = Path.Combine(dir, ".cursor", "rules", "seiton");
            await Assert.That(File.Exists(Path.Combine(baseDir, "SKILL.md"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(baseDir, "references", "rules.md"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(baseDir, "references", "fix-mode.md"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(baseDir, "references", "configuration.md"))).IsTrue();

            var output = stdout.ToString();
            await Assert.That(output).Contains(".cursor");
            await Assert.That(output).Contains("rules");
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

            var exitCode = InstallCommand.Run(skills: true, target: "claude", output: customPath, force: false, ci: false, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(File.Exists(Path.Combine(customPath, "SKILL.md"))).IsTrue();
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_Skills_DeploysReferenceFiles()
    {
        var dir = CreateTempDir();
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: true, target: "claude", output: null, force: false, ci: false, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            var refsDir = Path.Combine(dir, ".claude", "skills", "seiton", "references");
            await Assert.That(Directory.Exists(refsDir)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(refsDir, "rules.md"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(refsDir, "fix-mode.md"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(refsDir, "configuration.md"))).IsTrue();

            // Verify content is meaningful (not empty)
            var rulesContent = File.ReadAllText(Path.Combine(refsDir, "rules.md"));
            await Assert.That(rulesContent).Contains("template-injection");
            await Assert.That(rulesContent).Contains("unpinned-uses");

            var fixContent = File.ReadAllText(Path.Combine(refsDir, "fix-mode.md"));
            await Assert.That(fixContent).Contains("--dry-run");

            var configContent = File.ReadAllText(Path.Combine(refsDir, "configuration.md"));
            await Assert.That(configContent).Contains("seiton.yaml");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_Ci_CreatesWorkflowFile()
    {
        var dir = CreateTempDir();
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: false, target: "claude", output: null, force: false, ci: true, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            var workflowPath = Path.Combine(dir, ".github", "workflows", "seiton.yml");
            await Assert.That(File.Exists(workflowPath)).IsTrue();

            var content = File.ReadAllText(workflowPath);
            await Assert.That(content).Contains("seiton");
            await Assert.That(content).Contains("pull_request");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_Ci_ExistingFile_WithoutForce_ReturnsFatalError()
    {
        var dir = CreateTempDir();
        try
        {
            var workflowDir = Path.Combine(dir, ".github", "workflows");
            Directory.CreateDirectory(workflowDir);
            File.WriteAllText(Path.Combine(workflowDir, "seiton.yml"), "existing");

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: false, target: "claude", output: null, force: false, ci: true, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.FatalError);
            await Assert.That(stderr.ToString()).Contains("already exists");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_Ci_ExistingFile_WithForce_Overwrites()
    {
        var dir = CreateTempDir();
        try
        {
            var workflowDir = Path.Combine(dir, ".github", "workflows");
            Directory.CreateDirectory(workflowDir);
            File.WriteAllText(Path.Combine(workflowDir, "seiton.yml"), "old content");

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: false, target: "claude", output: null, force: true, ci: true, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            var content = File.ReadAllText(Path.Combine(workflowDir, "seiton.yml"));
            await Assert.That(content).Contains("seiton");
            await Assert.That(content).DoesNotContain("old content");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_Ci_CustomOutput_CreatesAtSpecifiedPath()
    {
        var dir = CreateTempDir();
        var customPath = Path.Combine(dir, "custom", "ci.yml");
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: false, target: "claude", output: customPath, force: false, ci: true, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(File.Exists(customPath)).IsTrue();

            var content = File.ReadAllText(customPath);
            await Assert.That(content).Contains("seiton");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_SkillsAndCi_BothInstalled()
    {
        var dir = CreateTempDir();
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: true, target: "claude", output: null, force: false, ci: true, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            // Skills installed
            var skillPath = Path.Combine(dir, ".claude", "skills", "seiton", "SKILL.md");
            await Assert.That(File.Exists(skillPath)).IsTrue();

            // CI workflow installed
            var workflowPath = Path.Combine(dir, ".github", "workflows", "seiton.yml");
            await Assert.That(File.Exists(workflowPath)).IsTrue();
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_SkillsAndCi_WithOutput_OutputAppliesToSkillsOnly()
    {
        var dir = CreateTempDir();
        var customSkillPath = Path.Combine(dir, "custom", "skills");
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = InstallCommand.Run(skills: true, target: "claude", output: customSkillPath, force: false, ci: true, baseDirectory: dir, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            // Skills installed at custom path
            await Assert.That(File.Exists(Path.Combine(customSkillPath, "SKILL.md"))).IsTrue();

            // CI workflow installed at default path (not custom)
            var workflowPath = Path.Combine(dir, ".github", "workflows", "seiton.yml");
            await Assert.That(File.Exists(workflowPath)).IsTrue();
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
