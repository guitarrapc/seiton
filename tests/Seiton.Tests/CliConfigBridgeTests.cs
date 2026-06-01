using Seiton.Config;

namespace Seiton.Tests;

[NotInParallel("ProcessState")]
public sealed class CliConfigBridgeTests
{
    [Test]
    public async Task DiscoverConfigPath_FoundInCurrentDirectory_ReportsZeroLevelsWalked()
    {
        var root = CreateTempDir();
        var configPath = Path.Combine(root, ".github", "seiton.yaml");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, "rules: {}\n");

            var resolution = CliConfigBridge.DiscoverConfigPath(root, discoveryBoundary: root);

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
    public async Task DiscoverConfigPath_FoundInParent_ReportsLevelsWalked()
    {
        var root = CreateTempDir();
        var nested = Path.Combine(root, "nested", "repo");
        var configPath = Path.Combine(root, ".github", "seiton.yaml");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            Directory.CreateDirectory(nested);
            File.WriteAllText(configPath, "rules: {}\n");

            var resolution = CliConfigBridge.DiscoverConfigPath(nested, discoveryBoundary: root);

            await Assert.That(resolution.Path).IsEqualTo(configPath);
            await Assert.That(resolution.Source).IsEqualTo(ConfigPathSource.Discovery);
            await Assert.That(resolution.DiscoveryLevelsWalked).IsEqualTo(2);
            await Assert.That(resolution.DiscoveryStartDirectory).IsEqualTo(Path.GetFullPath(nested));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task DiscoverConfigPath_NotFoundWithinBoundary_ReportsSearchMetadata()
    {
        var root = CreateTempDir();
        var nested = Path.Combine(root, "child");
        try
        {
            Directory.CreateDirectory(nested);

            var resolution = CliConfigBridge.DiscoverConfigPath(nested, discoveryBoundary: root);

            await Assert.That(resolution.Path).IsNull();
            await Assert.That(resolution.Source).IsEqualTo(ConfigPathSource.None);
            await Assert.That(resolution.DiscoveryStartDirectory).IsEqualTo(Path.GetFullPath(nested));
            await Assert.That(resolution.DiscoveryLevelsWalked).IsEqualTo(1);
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
    public async Task FormatVerboseMessage_Discovery_IncludesStartDirectoryAndLevelsWalked()
    {
        var start = Path.Combine("C:", "repo", "nested");
        var config = Path.Combine("C:", "repo", ".github", "seiton.yaml");
        var resolution = new ConfigPathResolution(
            config,
            ConfigPathSource.Discovery,
            start,
            DiscoveryLevelsWalked: 1);

        var message = resolution.FormatVerboseMessage();

        await Assert.That(message).Contains(Path.GetFullPath(config));
        await Assert.That(message).Contains($"discovered from {Path.GetFullPath(start)}");
        await Assert.That(message).Contains("walked up 1 level(s)");
    }

    [Test]
    public async Task FormatVerboseMessage_None_IncludesSearchMetadata()
    {
        var start = Path.Combine("C:", "repo", "nested");
        var resolution = new ConfigPathResolution(
            null,
            ConfigPathSource.None,
            start,
            DiscoveryLevelsWalked: 2);

        var message = resolution.FormatVerboseMessage();

        await Assert.That(message).IsEqualTo(
            $"(none, using defaults) (searched from {Path.GetFullPath(start)}, walked up 2 level(s))");
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

        await Assert.That(message).IsEqualTo($"{Path.GetFullPath(config)} (discovered, walked up 2 level(s))");
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
