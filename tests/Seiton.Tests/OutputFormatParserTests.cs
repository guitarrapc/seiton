using Seiton.Output;

namespace Seiton.Tests;

public sealed class OutputFormatParserTests
{
    [Test]
    public async Task TryParse_GithubActions_ReturnsTrue()
    {
        var ok = OutputFormatParser.TryParse("github-actions", out var format);

        await Assert.That(ok).IsEqualTo(true);
        await Assert.That(format).IsEqualTo(OutputFormat.GitHubActions);
    }

    [Test]
    public async Task TryParse_InvalidValue_ReturnsFalse()
    {
        var ok = OutputFormatParser.TryParse("xml", out var format);

        await Assert.That(ok).IsEqualTo(false);
        await Assert.That(format).IsEqualTo(OutputFormat.Text);
    }
}
