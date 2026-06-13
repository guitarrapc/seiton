using Seiton.Update.Parsers;

namespace Seiton.Update.Tests;

/// <summary>
/// Contract tests against committed <c>data/sources/**/raw/*.md</c> files.
/// They fail when GitHub Docs markup drifts enough that Stage 2 parsers stop extracting
/// data (empty or sharply reduced output), which would otherwise surface only after a fetch.
/// </summary>
public sealed class CommittedRawMarkdownParserContractTests
{
    [Test]
    public async Task WebhookEventsDocs_ExtractsEventCatalogAndParseableActivityTables()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "data", "sources", "webhooks", "github", "raw",
            "events-that-trigger-workflows.docs.md");
        var markdown = File.ReadAllText(path);
        var parser = new GitHubDocsWebhookMarkdownParser();

        var names = parser.ParseEventNames(markdown);
        var byEvent = parser.ParseActivityTypesByEvent(markdown);

        await Assert.That(names.Count).IsGreaterThanOrEqualTo(30);
        await Assert.That(byEvent.Count).IsGreaterThanOrEqualTo(20);

        await Assert.That(names.Contains("push")).IsTrue();
        await Assert.That(names.Contains("pull_request")).IsTrue();

        await Assert.That(byEvent.ContainsKey("check_suite")).IsTrue();
        await Assert.That(byEvent["check_suite"]).IsNotNull();
        await Assert.That(byEvent["check_suite"]!.Contains("completed")).IsTrue();
    }

    [Test]
    public async Task AvailabilityContextsDocs_ExtractsWorkflowKeyContextMap()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "data", "sources", "availability", "github", "raw",
            "contexts.docs.md");
        var markdown = File.ReadAllText(path);
        var parser = new GitHubDocsAvailabilityMarkdownParser();
        var map = parser.ParseWorkflowKeyContexts(markdown);

        await Assert.That(map.Count).IsGreaterThanOrEqualTo(28);
        await Assert.That(map.ContainsKey("run-name")).IsTrue();
        await Assert.That(map["run-name"]).Contains("github");
        await Assert.That(map.ContainsKey("jobs.<job_id>.steps.run")).IsTrue();
        await Assert.That(map["jobs.<job_id>.steps.run"]).Contains("secrets");
    }

    [Test]
    public async Task RunnerLabelsDocs_ExtractsHostedRunnerLabels()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "data", "sources", "runner-labels", "github", "raw",
            "github-hosted-runners.docs.md");
        var markdown = File.ReadAllText(path);
        var parser = new GitHubDocsRunnerLabelsMarkdownParser();
        var labels = parser.ParseSupportedRunnerLabels(markdown);

        await Assert.That(labels.Count).IsGreaterThanOrEqualTo(12);
        await Assert.That(labels.Any(x => x.Label == "ubuntu-latest")).IsTrue();
        await Assert.That(labels.Any(x => x.Label == "ubuntu-26.04" && x.IsPreview)).IsTrue();
        await Assert.That(labels.Any(x => x.Label == "ubuntu-26.04-arm" && x.IsPreview)).IsTrue();
    }

    [Test]
    public async Task ContextTypesDocs_ExtractsContextPropertyTables()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "data", "sources", "context-types", "github", "raw",
            "contexts.docs.md");
        var markdown = File.ReadAllText(path);
        var parser = new GitHubDocsContextTypesMarkdownParser();
        var contexts = parser.ParseContextProperties(markdown);

        await Assert.That(contexts.Count).IsGreaterThanOrEqualTo(8);
        var github = contexts.FirstOrDefault(c => c.Name == "github");
        await Assert.That(github).IsNotNull();
        await Assert.That(github!.Properties.Count).IsGreaterThanOrEqualTo(15);
    }

    [Test]
    public async Task ExpressionsDocs_ExtractsFunctionHeadings()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "data", "sources", "function-specs", "github", "raw",
            "expressions.docs.md");
        var markdown = File.ReadAllText(path);
        var parser = new GitHubDocsExpressionsMarkdownParser();
        var names = parser.ParseFunctionNames(markdown);

        await Assert.That(names.Count).IsGreaterThanOrEqualTo(10);
        await Assert.That(names.Contains("hashfiles")).IsTrue();
        await Assert.That(names.Contains("contains")).IsTrue();
    }

    [Test]
    public async Task PermissionsReusableDocs_ExtractsYamlScopeLines()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "data", "sources", "permissions", "github", "raw",
            "github-token-available-permissions.md");
        var markdown = File.ReadAllText(path);
        var parser = new GitHubDocsPermissionsMarkdownParser();
        var model = parser.Parse(markdown);

        await Assert.That(model.Scopes.Count).IsGreaterThanOrEqualTo(14);
        var actions = model.Scopes.FirstOrDefault(s => s.Name == "actions");
        await Assert.That(actions).IsNotNull();
        await Assert.That(actions!.Allowed).Contains("read");
        await Assert.That(actions.Allowed).Contains("write");
    }

    [Test]
    public async Task SupportedShellsReusable_ExtractsBuiltinShellRows()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "data", "sources", "shells", "github", "raw",
            "supported-shells.md");
        var markdown = File.ReadAllText(path);
        var rows = new GitHubDocsSupportedShellsMarkdownParser().Parse(markdown);

        await Assert.That(rows.Count).IsEqualTo(6);
        await Assert.That(rows.Any(r => r.Name == "bash")).IsTrue();
        await Assert.That(rows.Any(r => r.Name == "pwsh")).IsTrue();
        await Assert.That(rows.Single(r => r.Name == "sh").Platforms).IsEquivalentTo(new[] { "linux", "macos" });
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

        throw new InvalidOperationException("Repository root not found from test base directory.");
    }
}
