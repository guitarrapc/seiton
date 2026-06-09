using Seiton.Update.Parsers;

namespace Seiton.Update.Tests;

public sealed class GitHubDocsMarkdownParserTests
{
    // ParseEventNames

    [Test]
    public async Task ParseEventNames_SimpleHeadings_ReturnsAllNames()
    {
        var markdown = """
            ## `push`

            Some description.

            ## `pull_request`

            Another description.
            """;

        var parser = new GitHubDocsWebhookMarkdownParser();
        var names = parser.ParseEventNames(markdown);

        await Assert.That(names).Contains("push");
        await Assert.That(names).Contains("pull_request");
        await Assert.That(names.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ParseEventNames_EmptyMarkdown_ReturnsEmptySet()
    {
        var parser = new GitHubDocsWebhookMarkdownParser();
        var names = parser.ParseEventNames(string.Empty);

        await Assert.That(names.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ParseEventNames_HeadingsWithoutBackticks_AreRecognized()
    {
        var markdown = """
            ## issues

            Description here.
            """;

        var parser = new GitHubDocsWebhookMarkdownParser();
        var names = parser.ParseEventNames(markdown);

        await Assert.That(names).Contains("issues");
    }

    // ParseActivityTypesByEvent

    [Test]
    public async Task ParseActivityTypesByEvent_TableWithTypes_ReturnsCorrectList()
    {
        var markdown = """
            ## `check_suite`

            | Event | Activity types | `GITHUB_SHA` | `GITHUB_REF` |
            | --- | --- | --- | --- |
            | `check_suite` | `completed` | ... | ... |
            """;

        var parser = new GitHubDocsWebhookMarkdownParser();
        var result = parser.ParseActivityTypesByEvent(markdown);

        await Assert.That(result.ContainsKey("check_suite")).IsTrue();
        await Assert.That(result["check_suite"]).IsNotNull();
        await Assert.That(result["check_suite"]!).Contains("completed");
        await Assert.That(result["check_suite"]!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ParseActivityTypesByEvent_MultipleTypes_AllReturned()
    {
        var markdown = """
            ## `issues`

            | Event | Activity types | `GITHUB_SHA` | `GITHUB_REF` |
            | --- | --- | --- | --- |
            | `issues` | `opened`, `edited`, `closed` | ... | ... |
            """;

        var parser = new GitHubDocsWebhookMarkdownParser();
        var result = parser.ParseActivityTypesByEvent(markdown);

        await Assert.That(result.ContainsKey("issues")).IsTrue();
        var types = result["issues"]!;
        await Assert.That(types).Contains("opened");
        await Assert.That(types).Contains("edited");
        await Assert.That(types).Contains("closed");
    }

    [Test]
    public async Task ParseActivityTypesByEvent_IssuesWithLiquidConditional_ParsesStableTypes()
    {
        var markdown = """
            ## `issues`

            | Event | Activity types | `GITHUB_SHA` | `GITHUB_REF` |
            | --- | --- | --- | --- |
            | `issues` | - `opened`<br/>- `edited`<br/>- `deleted`<br/>- `transferred`<br/>- `pinned`<br/>- `unpinned`<br/>- `closed`<br/>- `reopened`<br/>- `assigned`<br/>- `unassigned`<br/>- `labeled`<br/>- `unlabeled`<br/>- `locked`<br/>- `unlocked`<br/>- `milestoned`<br/> - `demilestoned`<br/> - `typed`<br/> - `untyped`{% ifversion issue-fields %}<br/> - `field_added`<br/> - `field_removed`{% endif %} | ... | ... |
            """;

        var parser = new GitHubDocsWebhookMarkdownParser();
        var result = parser.ParseActivityTypesByEvent(markdown);

        await Assert.That(result.ContainsKey("issues")).IsTrue();
        var types = result["issues"]!;
        await Assert.That(types).Contains("typed");
        await Assert.That(types).Contains("untyped");
        await Assert.That(types).DoesNotContain("field_added");
        await Assert.That(types).DoesNotContain("field_removed");
    }

    [Test]
    public async Task ParseActivityTypesByEvent_LiquidTemplateCell_EventAbsentFromResult()
    {
        // Cells with {%...%} are unparseable; the event should not appear in the dict.
        var markdown = """
            ## `pull_request`

            | Event | Activity types | `GITHUB_SHA` | `GITHUB_REF` |
            | --- | --- | --- | --- |
            | `pull_request` | {% data ...  reusables... %} | ... | ... |
            """;

        var parser = new GitHubDocsWebhookMarkdownParser();
        var result = parser.ParseActivityTypesByEvent(markdown);

        await Assert.That(result.ContainsKey("pull_request")).IsFalse();
    }

    [Test]
    public async Task ParseEventNames_LiquidTemplateEvent_HeadingStillPresent()
    {
        // ParseEventNames is heading-based; Liquid cells don't affect it.
        var markdown = """
            ## `pull_request`

            | Event | Activity types | `GITHUB_SHA` | `GITHUB_REF` |
            | --- | --- | --- | --- |
            | `pull_request` | {% data reusables... %} | ... | ... |
            """;

        var parser = new GitHubDocsWebhookMarkdownParser();
        var names = parser.ParseEventNames(markdown);

        await Assert.That(names).Contains("pull_request");
    }

    [Test]
    public async Task ParseActivityTypesByEvent_NotApplicableCell_ReturnsEmptyList()
    {
        var markdown = """
            ## `push`

            | Event | Activity types | `GITHUB_SHA` | `GITHUB_REF` |
            | --- | --- | --- | --- |
            | `push` | Not applicable | ... | ... |
            """;

        var parser = new GitHubDocsWebhookMarkdownParser();
        var result = parser.ParseActivityTypesByEvent(markdown);

        await Assert.That(result.ContainsKey("push")).IsTrue();
        await Assert.That(result["push"]).IsNotNull();
        await Assert.That(result["push"]!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ParseActivityTypesByEvent_CustomCell_ReturnsNull()
    {
        // "Custom" means user-defined; null represents unconstrained.
        var markdown = """
            ## `repository_dispatch`

            | Event | Activity types | `GITHUB_SHA` | `GITHUB_REF` |
            | --- | --- | --- | --- |
            | `repository_dispatch` | Custom | ... | ... |
            """;

        var parser = new GitHubDocsWebhookMarkdownParser();
        var result = parser.ParseActivityTypesByEvent(markdown);

        await Assert.That(result.ContainsKey("repository_dispatch")).IsTrue();
        await Assert.That(result["repository_dispatch"]).IsNull();
    }

    [Test]
    public async Task ParseActivityTypesByEvent_HeadingWithNoTable_NotInResult()
    {
        var markdown = """
            ## `fork`

            No table here, just description text.
            """;

        var parser = new GitHubDocsWebhookMarkdownParser();
        var result = parser.ParseActivityTypesByEvent(markdown);

        await Assert.That(result.ContainsKey("fork")).IsFalse();
    }

    [Test]
    public async Task ParseActivityTypesByEvent_MultipleEvents_EachParsedIndependently()
    {
        var markdown = """
            ## `check_suite`

            | Event | Activity types | `GITHUB_SHA` | `GITHUB_REF` |
            | --- | --- | --- | --- |
            | `check_suite` | `completed` | ... | ... |

            ## `push`

            | Event | Activity types | `GITHUB_SHA` | `GITHUB_REF` |
            | --- | --- | --- | --- |
            | `push` | Not applicable | ... | ... |
            """;

        var parser = new GitHubDocsWebhookMarkdownParser();
        var result = parser.ParseActivityTypesByEvent(markdown);

        await Assert.That(result.ContainsKey("check_suite")).IsTrue();
        await Assert.That(result["check_suite"]!).Contains("completed");

        await Assert.That(result.ContainsKey("push")).IsTrue();
        await Assert.That(result["push"]!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ParseActivityTypesByEvent_EmptyMarkdown_ReturnsEmptyDict()
    {
        var parser = new GitHubDocsWebhookMarkdownParser();
        var result = parser.ParseActivityTypesByEvent(string.Empty);

        await Assert.That(result.Count).IsEqualTo(0);
    }
}
