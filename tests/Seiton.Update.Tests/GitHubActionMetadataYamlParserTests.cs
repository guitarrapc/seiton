using Seiton.Update.Parsers;

namespace Seiton.Update.Tests;

public sealed class GitHubActionMetadataYamlParserTests
{
    [Test]
    public async Task ParseInputNames_WithInputsSection_ReturnsInputNames()
    {
        var yaml = """
            name: test action
            inputs:
              first-input:
                description: first
              second_input:
                description: second
            runs:
              using: composite
              steps: []
            """;

        var parser = new GitHubActionMetadataYamlParser();
        var names = parser.ParseInputNames(yaml);

        await Assert.That(names).Contains("first-input");
        await Assert.That(names).Contains("second_input");
        await Assert.That(names.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ParseInputNames_WithoutInputsSection_ReturnsEmpty()
    {
        var parser = new GitHubActionMetadataYamlParser();
        var names = parser.ParseInputNames("name: test\nruns:\n  using: composite");

        await Assert.That(names.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ParseOutputs_WithOutputsSection_ReturnsOutputNames()
    {
        var yaml = """
            name: test action
            outputs:
              cache-hit:
                description: Whether there was a cache hit
              artifact-path:
                description: Path to the artifact
            runs:
              using: node20
              main: index.js
            """;

        var parser = new GitHubActionMetadataYamlParser();
        var outputs = parser.ParseOutputs(yaml);

        await Assert.That(outputs.Count).IsEqualTo(2);
        await Assert.That(outputs.Select(o => o.Name)).Contains("artifact-path");
        await Assert.That(outputs.Select(o => o.Name)).Contains("cache-hit");
    }

    [Test]
    public async Task ParseOutputs_WithoutOutputsSection_ReturnsEmpty()
    {
        var parser = new GitHubActionMetadataYamlParser();
        var outputs = parser.ParseOutputs("name: test\nruns:\n  using: composite");

        await Assert.That(outputs.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ParseOutputs_CompositeAction_ParsesOutputKeys()
    {
        var yaml = """
            name: composite action
            outputs:
              result:
                description: The result
                value: ${{ steps.run.outputs.result }}
            runs:
              using: composite
              steps:
                - id: run
                  run: echo "result=ok" >> $GITHUB_OUTPUT
            """;

        var parser = new GitHubActionMetadataYamlParser();
        var outputs = parser.ParseOutputs(yaml);

        await Assert.That(outputs.Count).IsEqualTo(1);
        await Assert.That(outputs[0].Name).IsEqualTo("result");
    }

    [Test]
    public async Task ParseRunsUsing_NodeAction_ReturnsNodeVersion()
    {
        var yaml = """
            name: test action
            inputs:
              token:
                description: GitHub token
            runs:
              using: node20
              main: index.js
            """;

        var parser = new GitHubActionMetadataYamlParser();
        var runsUsing = parser.ParseRunsUsing(yaml);

        await Assert.That(runsUsing).IsEqualTo("node20");
    }

    [Test]
    public async Task ParseRunsUsing_CompositeAction_ReturnsComposite()
    {
        var yaml = """
            name: composite
            runs:
              using: composite
              steps:
                - run: echo hello
            """;

        var parser = new GitHubActionMetadataYamlParser();
        var runsUsing = parser.ParseRunsUsing(yaml);

        await Assert.That(runsUsing).IsEqualTo("composite");
    }

    [Test]
    public async Task ParseRunsUsing_QuotedValue_StripsQuotes()
    {
        var yaml = """
            name: test
            runs:
              using: 'node16'
              main: index.js
            """;

        var parser = new GitHubActionMetadataYamlParser();
        var runsUsing = parser.ParseRunsUsing(yaml);

        await Assert.That(runsUsing).IsEqualTo("node16");
    }

    [Test]
    public async Task ParseRunsUsing_NoRunsSection_ReturnsEmpty()
    {
        var parser = new GitHubActionMetadataYamlParser();
        var runsUsing = parser.ParseRunsUsing("name: test\ninputs:\n  foo:\n    description: bar");

        await Assert.That(runsUsing).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ParseRunsUsing_RealCheckout_ReturnsNode24()
    {
        var repoRoot = FindRepoRoot();
        var rawPath = Path.Combine(repoRoot, "data", "sources", "popular-actions", "github", "raw", "actions_checkout.action.yml");
        var yaml = File.ReadAllText(rawPath);

        var parser = new GitHubActionMetadataYamlParser();
        var runsUsing = parser.ParseRunsUsing(yaml);

        await Assert.That(runsUsing).IsEqualTo("node24");
    }

    [Test]
    public async Task ParseOutputs_RealCache_ContainsCacheHit()
    {
        var repoRoot = FindRepoRoot();
        var rawPath = Path.Combine(repoRoot, "data", "sources", "popular-actions", "github", "raw", "actions_cache.action.yml");
        var yaml = File.ReadAllText(rawPath);

        var parser = new GitHubActionMetadataYamlParser();
        var outputs = parser.ParseOutputs(yaml);

        await Assert.That(outputs.Select(o => o.Name)).Contains("cache-hit");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "seiton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
