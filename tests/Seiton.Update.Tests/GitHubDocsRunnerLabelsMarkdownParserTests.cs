using Seiton.Update.Parsers;

namespace Seiton.Update.Tests;

public sealed class GitHubDocsRunnerLabelsMarkdownParserTests
{
    [Test]
    public async Task ParseSupportedRunnerLabels_ExtractsHostedLabelsAndPreviewFlags()
    {
        var markdown = """
            ## Supported runners and hardware resources

            <table>
              <tbody>
                <tr>
                  <td><code><a href="x">ubuntu-latest</a></code>, <code><a href="x">ubuntu-24.04</a></code></td>
                </tr>
                <tr>
                  <td><code><a href="x">windows-2025-vs2026</a></code> (public preview)</td>
                </tr>
                <tr>
                  <td><code><a href="x">macos-15</a></code></td>
                </tr>
              </tbody>
            </table>

            ## Administrative privileges
            """;

        var parser = new GitHubDocsRunnerLabelsMarkdownParser();
        var labels = parser.ParseSupportedRunnerLabels(markdown);

        await Assert.That(labels.Any(x => x.Label == "ubuntu-latest" && !x.IsPreview)).IsTrue();
        await Assert.That(labels.Any(x => x.Label == "ubuntu-24.04" && !x.IsPreview)).IsTrue();
        await Assert.That(labels.Any(x => x.Label == "windows-2025-vs2026" && x.IsPreview)).IsTrue();
        await Assert.That(labels.Any(x => x.Label == "macos-15" && !x.IsPreview)).IsTrue();
    }

    [Test]
    public async Task ParseSupportedRunnerLabels_ExtractsUbuntu2604PreviewLabels()
    {
        var markdown = """
            ## Supported runners and hardware resources

            <table>
              <tbody>
                <tr>
                  <td><code><a href="x">ubuntu-26.04</a></code> (Public preview)</td>
                </tr>
                <tr>
                  <td><code><a href="x">ubuntu-26.04-arm</a></code> (Public preview)</td>
                </tr>
              </tbody>
            </table>

            ## Administrative privileges
            """;

        var parser = new GitHubDocsRunnerLabelsMarkdownParser();
        var labels = parser.ParseSupportedRunnerLabels(markdown);

        await Assert.That(labels.Any(x => x.Label == "ubuntu-26.04" && x.IsPreview)).IsTrue();
        await Assert.That(labels.Any(x => x.Label == "ubuntu-26.04-arm" && x.IsPreview)).IsTrue();
    }

    [Test]
    public async Task ParseSupportedRunnerLabels_IgnoresNonHostedLabels()
    {
        var markdown = """
            ## Supported runners and hardware resources

            <code><a href="x">self-hosted</a></code>
            <code><a href="x">my-custom-runner</a></code>
            <code><a href="x">ubuntu-22.04</a></code>

            ## Administrative privileges
            """;

        var parser = new GitHubDocsRunnerLabelsMarkdownParser();
        var labels = parser.ParseSupportedRunnerLabels(markdown);

        await Assert.That(labels.Any(x => x.Label == "ubuntu-22.04")).IsTrue();
        await Assert.That(labels.Any(x => x.Label == "self-hosted")).IsFalse();
        await Assert.That(labels.Any(x => x.Label == "my-custom-runner")).IsFalse();
    }
}
