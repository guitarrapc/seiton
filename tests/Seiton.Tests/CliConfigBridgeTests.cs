using Seiton.Config;
using Seiton.Output;

namespace Seiton.Tests;

[NotInParallel("ProcessState")]
public sealed class CliConfigBridgeTests
{
    [Test]
    public async Task ResolveOutputFormat_GitHubActionsEnv_ReturnsGitHubActions()
    {
        var originalFormat = Environment.GetEnvironmentVariable("SEITON_FORMAT");
        var originalGha = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        try
        {
            Environment.SetEnvironmentVariable("SEITON_FORMAT", "github-actions");
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);

            var resolved = CliConfigBridge.ResolveOutputFormat(OutputFormat.Text);

            await Assert.That(resolved).IsEqualTo(OutputFormat.GitHubActions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEITON_FORMAT", originalFormat);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalGha);
        }
    }

    [Test]
    public async Task ResolveOutputFormat_GitHubActionsEnvUnset_DefaultText()
    {
        var originalFormat = Environment.GetEnvironmentVariable("SEITON_FORMAT");
        var originalGha = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        try
        {
            Environment.SetEnvironmentVariable("SEITON_FORMAT", null);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);

            var resolved = CliConfigBridge.ResolveOutputFormat(OutputFormat.Text);

            await Assert.That(resolved).IsEqualTo(OutputFormat.Text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEITON_FORMAT", originalFormat);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalGha);
        }
    }

    [Test]
    public async Task ResolveOutputFormat_GitHubActionsRunner_DefaultGitHubActions()
    {
        var originalFormat = Environment.GetEnvironmentVariable("SEITON_FORMAT");
        var originalGha = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        try
        {
            Environment.SetEnvironmentVariable("SEITON_FORMAT", null);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");

            var resolved = CliConfigBridge.ResolveOutputFormat(OutputFormat.Text);

            await Assert.That(resolved).IsEqualTo(OutputFormat.GitHubActions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEITON_FORMAT", originalFormat);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalGha);
        }
    }

    [Test]
    public async Task ResolveOutputFormat_ExplicitJson_IgnoresGitHubActionsEnv()
    {
        var originalGha = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");

            var resolved = CliConfigBridge.ResolveOutputFormat(OutputFormat.Json);

            await Assert.That(resolved).IsEqualTo(OutputFormat.Json);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalGha);
        }
    }

    [Test]
    public async Task ResolveOutputFormat_SeatonFormatText_OnGitHubActionsRunner_ReturnsText()
    {
        var originalFormat = Environment.GetEnvironmentVariable("SEITON_FORMAT");
        var originalGha = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        try
        {
            Environment.SetEnvironmentVariable("SEITON_FORMAT", "text");
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");

            var resolved = CliConfigBridge.ResolveOutputFormat(OutputFormat.Text);

            await Assert.That(resolved).IsEqualTo(OutputFormat.Text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEITON_FORMAT", originalFormat);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalGha);
        }
    }

    [Test]
    public async Task ResolveOutputFormat_ExplicitTextFlag_OnGitHubActionsRunner_ReturnsText()
    {
        var originalGha = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");

            var resolved = CliConfigBridge.ResolveOutputFormat(OutputFormat.Text, formatExplicitlySet: true);

            await Assert.That(resolved).IsEqualTo(OutputFormat.Text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalGha);
        }
    }

    [Test]
    public async Task ResolveOutputFormat_ExplicitGitHubActionsFlag_ReturnsGitHubActions()
    {
        var originalGha = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);

            var resolved = CliConfigBridge.ResolveOutputFormat(OutputFormat.GitHubActions);

            await Assert.That(resolved).IsEqualTo(OutputFormat.GitHubActions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalGha);
        }
    }

    [Test]
    public async Task ResolveOutputFormat_AutoDefaultDisabled_OnGitHubActionsRunner_ReturnsText()
    {
        var originalFormat = Environment.GetEnvironmentVariable("SEITON_FORMAT");
        var originalGha = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        try
        {
            Environment.SetEnvironmentVariable("SEITON_FORMAT", null);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");

            var resolved = CliConfigBridge.ResolveOutputFormat(OutputFormat.Text, allowGitHubActionsAutoDefault: false);

            await Assert.That(resolved).IsEqualTo(OutputFormat.Text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEITON_FORMAT", originalFormat);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalGha);
        }
    }

    [Test]
    public async Task DiscoverConfigPath_FoundInCurrentDirectory_ReportsZeroLevelsWalked()
    {
        var root = CreateTempDir();
        var configPath = Path.Combine(root, ".github", "seiton.yaml");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, "rules: {}\n");

            var resolution = CliConfigBridge.DiscoverConfigPath(root);

            await Assert.That(resolution.Path).IsEqualTo(configPath);
            await Assert.That(resolution.Source).IsEqualTo(ConfigPathSource.Discovery);
            await Assert.That(resolution.DiscoveryLevelsWalked).IsEqualTo(0);
            await Assert.That(resolution.DiscoveryStartDirectory).IsEqualTo(Path.GetFullPath(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task DiscoverConfigPath_ParentConfigOnly_ReturnsNone()
    {
        var root = CreateTempDir();
        var nested = Path.Combine(root, "nested", "repo");
        var configPath = Path.Combine(root, ".github", "seiton.yaml");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            Directory.CreateDirectory(nested);
            File.WriteAllText(configPath, "rules: {}\n");

            var resolution = CliConfigBridge.DiscoverConfigPath(nested);

            await Assert.That(resolution.Path).IsNull();
            await Assert.That(resolution.Source).IsEqualTo(ConfigPathSource.None);
            await Assert.That(resolution.DiscoveryLevelsWalked).IsEqualTo(0);
            await Assert.That(resolution.DiscoveryStartDirectory).IsEqualTo(Path.GetFullPath(nested));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task DiscoverConfigPath_NestedCiLayout_UsesChildConfigNotParent()
    {
        var root = CreateTempDir();
        var child = Path.Combine(root, "LogicLooper");
        var parentConfig = Path.Combine(root, ".github", "seiton.yaml");
        var childConfig = Path.Combine(child, ".github", "seiton.yaml");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(parentConfig)!);
            Directory.CreateDirectory(Path.GetDirectoryName(childConfig)!);
            File.WriteAllText(parentConfig, "rules:\n  runner-no-latest:\n    enabled: false\n");
            File.WriteAllText(childConfig, "rules:\n  runner-no-latest:\n    enabled: true\n");

            var resolution = CliConfigBridge.DiscoverConfigPath(child);

            await Assert.That(resolution.Path).IsEqualTo(childConfig);
            await Assert.That(resolution.Source).IsEqualTo(ConfigPathSource.Discovery);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task DiscoverConfigPath_NotFoundUnderCwd_ReportsSearchMetadata()
    {
        var root = CreateTempDir();
        var nested = Path.Combine(root, "child");
        try
        {
            Directory.CreateDirectory(nested);

            var resolution = CliConfigBridge.DiscoverConfigPath(nested);

            await Assert.That(resolution.Path).IsNull();
            await Assert.That(resolution.Source).IsEqualTo(ConfigPathSource.None);
            await Assert.That(resolution.DiscoveryStartDirectory).IsEqualTo(Path.GetFullPath(nested));
            await Assert.That(resolution.DiscoveryLevelsWalked).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ResolveConfigPath_ExplicitFlag_ReportsExplicitSource()
    {
        var root = CreateTempDir();
        var configPath = Path.Combine(root, "custom.yaml");
        try
        {
            File.WriteAllText(configPath, "rules: {}\n");

            var resolution = CliConfigBridge.ResolveConfigPath(configPath);

            await Assert.That(resolution.Path).IsEqualTo(configPath);
            await Assert.That(resolution.Source).IsEqualTo(ConfigPathSource.ExplicitFlag);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ResolveConfigPath_EnvironmentVariable_ReportsEnvironmentSource()
    {
        var root = CreateTempDir();
        var configPath = Path.Combine(root, "env-config.yaml");
        var originalEnv = Environment.GetEnvironmentVariable("SEITON_CONFIG");
        try
        {
            File.WriteAllText(configPath, "rules: {}\n");
            Environment.SetEnvironmentVariable("SEITON_CONFIG", configPath);

            var resolution = CliConfigBridge.ResolveConfigPath(explicitConfigPath: null);

            await Assert.That(resolution.Path).IsEqualTo(configPath);
            await Assert.That(resolution.Source).IsEqualTo(ConfigPathSource.EnvironmentVariable);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEITON_CONFIG", originalEnv);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ResolveConfigPath_ExplicitFlag_TakesPrecedenceOverEnvironmentVariable()
    {
        var root = CreateTempDir();
        var explicitPath = Path.Combine(root, "explicit.yaml");
        var envPath = Path.Combine(root, "env.yaml");
        var originalEnv = Environment.GetEnvironmentVariable("SEITON_CONFIG");
        try
        {
            File.WriteAllText(explicitPath, "rules: {}\n");
            File.WriteAllText(envPath, "rules: {}\n");
            Environment.SetEnvironmentVariable("SEITON_CONFIG", envPath);

            var resolution = CliConfigBridge.ResolveConfigPath(explicitPath);

            await Assert.That(resolution.Path).IsEqualTo(explicitPath);
            await Assert.That(resolution.Source).IsEqualTo(ConfigPathSource.ExplicitFlag);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEITON_CONFIG", originalEnv);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ResolveConfigPath_CurrentDirectory_OnlyUsesCwdConfig()
    {
        var root = CreateTempDir();
        var nested = Path.Combine(root, "nested");
        var parentConfig = Path.Combine(root, ".github", "seiton.yaml");
        var childConfig = Path.Combine(nested, ".github", "seiton.yaml");
        var originalCwd = Environment.CurrentDirectory;
        var originalEnv = Environment.GetEnvironmentVariable("SEITON_CONFIG");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(parentConfig)!);
            Directory.CreateDirectory(Path.GetDirectoryName(childConfig)!);
            File.WriteAllText(parentConfig, "rules: {}\n");
            File.WriteAllText(childConfig, "rules: {}\n");
            Environment.SetEnvironmentVariable("SEITON_CONFIG", null);
            Environment.CurrentDirectory = nested;

            var resolution = CliConfigBridge.ResolveConfigPath(explicitConfigPath: null);

            await Assert.That(resolution.Path).IsEqualTo(childConfig);
            await Assert.That(resolution.Source).IsEqualTo(ConfigPathSource.Discovery);
            await Assert.That(resolution.DiscoveryLevelsWalked).IsEqualTo(0);
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            Environment.SetEnvironmentVariable("SEITON_CONFIG", originalEnv);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task FormatVerboseMessage_Discovery_IncludesCwdScope()
    {
        var start = Path.Combine("C:", "repo", "nested");
        var config = Path.Combine(start, ".github", "seiton.yaml");
        var resolution = new ConfigPathResolution(
            config,
            ConfigPathSource.Discovery,
            start,
            DiscoveryLevelsWalked: 0);

        var message = resolution.FormatVerboseMessage();

        await Assert.That(message).Contains(Path.GetFullPath(config));
        await Assert.That(message).Contains($"discovered under cwd {Path.GetFullPath(start)}");
    }

    [Test]
    public async Task FormatVerboseMessage_None_IncludesSearchMetadata()
    {
        var start = Path.Combine("C:", "repo", "nested");
        var resolution = new ConfigPathResolution(
            null,
            ConfigPathSource.None,
            start,
            DiscoveryLevelsWalked: 0);

        var message = resolution.FormatVerboseMessage();

        await Assert.That(message).IsEqualTo(
            $"(none, using defaults) (searched under cwd {Path.GetFullPath(start)})");
    }

    [Test]
    public async Task FormatVerboseMessage_ExplicitFlag_IncludesSourceHint()
    {
        var config = Path.Combine("C:", "repo", ".github", "seiton.yaml");
        var resolution = new ConfigPathResolution(config, ConfigPathSource.ExplicitFlag);

        var message = resolution.FormatVerboseMessage();

        await Assert.That(message).IsEqualTo($"{Path.GetFullPath(config)} (from --config)");
    }

    [Test]
    public async Task FormatVerboseMessage_EnvironmentVariable_IncludesSourceHint()
    {
        var config = Path.Combine("C:", "repo", ".github", "seiton.yaml");
        var resolution = new ConfigPathResolution(config, ConfigPathSource.EnvironmentVariable);

        var message = resolution.FormatVerboseMessage();

        await Assert.That(message).IsEqualTo($"{Path.GetFullPath(config)} (from SEITON_CONFIG)");
    }

    [Test]
    public async Task FormatVerboseMessage_DiscoveryWithoutStartDirectory_DoesNotThrow()
    {
        var config = Path.Combine("C:", "repo", ".github", "seiton.yaml");
        var resolution = new ConfigPathResolution(
            config,
            ConfigPathSource.Discovery,
            DiscoveryStartDirectory: null,
            DiscoveryLevelsWalked: 2);

        var message = resolution.FormatVerboseMessage();

        await Assert.That(message).IsEqualTo($"{Path.GetFullPath(config)} (discovered)");
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
