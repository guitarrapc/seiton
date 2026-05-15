using Seiton.Update.Parsers;

namespace Seiton.Update.Tests;

public sealed class GitHubDocsSupportedShellsMarkdownParserTests
{
    [Test]
    public async Task Parse_MinimalTable_SkipsUnspecifiedAndMergesPwsh()
    {
        var md = """
                 | Supported platform | `shell` parameter | Description | Command run internally |
                 | ------------------ | ----------------- | ----------- | ----------------------------------------------- |
                 | Linux / macOS      | unspecified       | (default)   | `bash -e {0}`                                   |
                 | All                | `bash`            | bash        | `bash --noprofile --norc -eo pipefail {0}`      |
                 | All                | `pwsh`            | pwsh all    | `pwsh -command ". '{0}'"`                       |
                 | Windows            | `pwsh`            | pwsh win    | `pwsh -command ". '{0}'"`.                      |
                 """;

        var rows = new GitHubDocsSupportedShellsMarkdownParser().Parse(md);

        await Assert.That(rows.Count).IsEqualTo(2);
        var bash = rows.Single(r => r.Name == "bash");
        await Assert.That(bash.Platforms).IsEquivalentTo(new[] { "linux", "macos", "windows" });

        var pwsh = rows.Single(r => r.Name == "pwsh");
        await Assert.That(pwsh.Platforms).IsEquivalentTo(new[] { "linux", "macos", "windows" });
        await Assert.That(pwsh.Command).IsEqualTo("pwsh -command \". '{0}'\"");
    }

    [Test]
    public async Task Parse_WhenHeaderMissing_Throws()
    {
        var md = "| foo | bar |\n| --- | --- |\n";

        await Assert.That(() => new GitHubDocsSupportedShellsMarkdownParser().Parse(md))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Parse_WhenCommandsConflictForSameShell_Throws()
    {
        var md = """
                 | Supported platform | `shell` parameter | Description | Command run internally |
                 | ------------------ | ----------------- | ----------- | ---------------------- |
                 | All                | `bash`            | a           | `bash -e {0}`          |
                 | Linux / macOS      | `bash`            | b           | `bash -x {0}`          |
                 """;

        await Assert.That(() => new GitHubDocsSupportedShellsMarkdownParser().Parse(md))
            .Throws<InvalidDataException>();
    }
}
