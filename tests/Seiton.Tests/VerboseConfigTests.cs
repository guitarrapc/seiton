using Seiton.Cli;
using Seiton.Commands;
using Seiton.Core.Linting;

namespace Seiton.Tests;

public sealed class VerboseConfigTests
{
    // === Suppression Summary Tests ===

    [Test]
    public async Task WriteSuppressionSummary_WithSuppressions_EmitsFormattedLine()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);
        var summary = new SuppressionSummary(
            3,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["unpinned-uses"] = 2,
                ["template-injection"] = 1,
            },
            []);

        CheckCommand.WriteSuppressionSummary(logger, summary);

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("verbose: suppressed: 3 diagnostic(s) (unpinned-uses: 2, template-injection: 1)");
    }

    [Test]
    public async Task WriteSuppressionSummary_NoSuppressions_EmitsNothing()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        CheckCommand.WriteSuppressionSummary(logger, SuppressionSummary.Empty);

        await Assert.That(sw.ToString()).IsEqualTo("");
    }

    [Test]
    public async Task WriteSuppressionSummary_MultipleRules_SortedByCountDescending()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);
        var summary = new SuppressionSummary(
            10,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["rule-a"] = 2,
                ["rule-b"] = 5,
                ["rule-c"] = 3,
            },
            []);

        CheckCommand.WriteSuppressionSummary(logger, summary);

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("verbose: suppressed: 10 diagnostic(s) (rule-b: 5, rule-c: 3, rule-a: 2)");
    }

    [Test]
    public async Task WriteSuppressionSummary_VerboseDisabled_EmitsNothing()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: false, sw);
        var summary = new SuppressionSummary(
            5,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["rule-x"] = 5 },
            []);

        CheckCommand.WriteSuppressionSummary(logger, summary);

        await Assert.That(sw.ToString()).IsEqualTo("");
    }

    // === Effective Network Config Tests ===

    [Test]
    public async Task WriteEffectiveNetworkConfig_CliOverride_ShowsCliSource()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        FixCommand.WriteEffectiveNetworkConfig(logger,
            enablePinNetwork: true, enableImageNetwork: false,
            pinningConfig: null,
            imagesConfig: null);

        var lines = sw.ToString().TrimEnd().Split(Environment.NewLine);
        await Assert.That(lines).Count().IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo("verbose: config: fix.pinning.enable-network=true (source: --enable-pin-network)");
        await Assert.That(lines[1]).IsEqualTo("verbose: config: fix.images.enable-network=false (source: default)");
    }

    [Test]
    public async Task WriteEffectiveNetworkConfig_FromConfig_ShowsConfigSource()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        FixCommand.WriteEffectiveNetworkConfig(logger,
            enablePinNetwork: false, enableImageNetwork: false,
            pinningConfig: new FixPinningConfig { EnableNetwork = true, HasEnableNetwork = true },
            imagesConfig: new FixImagesConfig { EnableNetwork = true, HasEnableNetwork = true });

        var lines = sw.ToString().TrimEnd().Split(Environment.NewLine);
        await Assert.That(lines).Count().IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo("verbose: config: fix.pinning.enable-network=true (source: config)");
        await Assert.That(lines[1]).IsEqualTo("verbose: config: fix.images.enable-network=true (source: config)");
    }

    [Test]
    public async Task WriteEffectiveNetworkConfig_Default_ShowsDefaultSource()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        FixCommand.WriteEffectiveNetworkConfig(logger,
            enablePinNetwork: false, enableImageNetwork: false,
            pinningConfig: null,
            imagesConfig: null);

        var lines = sw.ToString().TrimEnd().Split(Environment.NewLine);
        await Assert.That(lines).Count().IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo("verbose: config: fix.pinning.enable-network=false (source: default)");
        await Assert.That(lines[1]).IsEqualTo("verbose: config: fix.images.enable-network=false (source: default)");
    }

    [Test]
    public async Task WriteEffectiveNetworkConfig_ExplicitFalseInConfig_ShowsConfigSource()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        FixCommand.WriteEffectiveNetworkConfig(logger,
            enablePinNetwork: false, enableImageNetwork: false,
            pinningConfig: new FixPinningConfig { EnableNetwork = false, HasEnableNetwork = true },
            imagesConfig: new FixImagesConfig { EnableNetwork = false, HasEnableNetwork = true });

        var lines = sw.ToString().TrimEnd().Split(Environment.NewLine);
        await Assert.That(lines).Count().IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo("verbose: config: fix.pinning.enable-network=false (source: config)");
        await Assert.That(lines[1]).IsEqualTo("verbose: config: fix.images.enable-network=false (source: config)");
    }

    // === Discovery Logging Tests ===

    [Test]
    public async Task ResolveFiles_AutoDiscovery_LogsSearchPathAndFoundDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"seiton-test-{Guid.NewGuid():N}");
        var workflowsDir = Path.Combine(tempDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);
        File.WriteAllText(Path.Combine(workflowsDir, "ci.yml"), "on: push");

        try
        {
            using var sw = new StringWriter();
            var logger = VerboseLogger.Create(verbose: true, sw);

            _ = InputDiscovery.ResolveFiles([], includeActions: false, logger, tempDir);

            var output = sw.ToString();
            await Assert.That(output).Contains($"verbose: discovery: searching from {tempDir}");
            await Assert.That(output).Contains($"verbose: discovery: found {workflowsDir}");
            await Assert.That(output).Contains("verbose: discovery: 1 file(s) resolved");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task ResolveFiles_ExplicitArgs_LogsExplicitCount()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"seiton-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var testFile = Path.Combine(tempDir, "test.yml");
        File.WriteAllText(testFile, "on: push");

        try
        {
            using var sw = new StringWriter();
            var logger = VerboseLogger.Create(verbose: true, sw);

            _ = InputDiscovery.ResolveFiles([testFile], includeActions: false, logger, tempDir);

            await Assert.That(sw.ToString().TrimEnd())
                .IsEqualTo("verbose: discovery: 1 file(s) from explicit args");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task ResolveFiles_NoVerbose_NoOutput()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"seiton-test-{Guid.NewGuid():N}");
        var workflowsDir = Path.Combine(tempDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);
        File.WriteAllText(Path.Combine(workflowsDir, "ci.yml"), "on: push");

        try
        {
            using var sw = new StringWriter();
            var logger = VerboseLogger.Create(verbose: false, sw);

            _ = InputDiscovery.ResolveFiles([], includeActions: false, logger, tempDir);

            await Assert.That(sw.ToString()).IsEqualTo("");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
