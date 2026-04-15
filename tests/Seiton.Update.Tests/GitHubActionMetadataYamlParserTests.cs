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
}
