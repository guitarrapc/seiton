using Seiton.Update.Generators;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Tests;

public sealed class WebhookUpdaterGoldenTests
{
    [Test]
    public async Task Generate_FromGitHubPrimarySource_MatchesWebhookTypesGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        var sourcePath = Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "webhook_types.json");
        var goldenPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "WebhookTypes.g.cs");

        var webhookParser = new GitHubWebhookSourceParser();
        var generator = new WebhookTypesCSharpGenerator();
        var eventFilterKeys = LoadEventFilterKeys(repoRoot);

        var events = webhookParser.Parse(sourcePath);
        var actual = generator.Generate(events, eventFilterKeys);
        var expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");

        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task Sync_WhenOutputAlreadyCurrent_IsIdempotent()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoCopy(repoRoot);

        try
        {
            var sync = new WebhookSyncService();

            var first = sync.Sync(tempRepo);
            var second = sync.Sync(tempRepo);

            await Assert.That(first).IsFalse();
            await Assert.That(second).IsFalse();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task Verify_WhenGeneratedFileIsStale_ReturnsFalseThenSyncRepairs()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoCopy(repoRoot);

        try
        {
            var outputPath = Path.Combine(tempRepo, "src", "Seiton.Core", "Generated", "WebhookTypes.g.cs");
            File.AppendAllText(outputPath, "// stale\n");

            var sync = new WebhookSyncService();
            var stale = sync.IsUpToDate(tempRepo);
            var changed = sync.Sync(tempRepo);
            var fixedNow = sync.IsUpToDate(tempRepo);

            await Assert.That(stale).IsFalse();
            await Assert.That(changed).IsTrue();
            await Assert.That(fixedNow).IsTrue();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    private static string CreateTempRepoCopy(string repoRoot)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-update-tests-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempRepo);
        Directory.CreateDirectory(Path.Combine(tempRepo, "data", "sources", "webhooks", "github"));
        Directory.CreateDirectory(Path.Combine(tempRepo, "data", "sources", "expected-keys", "github"));
        Directory.CreateDirectory(Path.Combine(tempRepo, "src", "Seiton.Core", "Generated"));

        File.Copy(
            Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "webhook_types.json"),
            Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "webhook_types.json"),
            overwrite: true);

        File.Copy(
            Path.Combine(repoRoot, "data", "sources", "expected-keys", "github", "expected-keys.json"),
            Path.Combine(tempRepo, "data", "sources", "expected-keys", "github", "expected-keys.json"),
            overwrite: true);

        File.Copy(
            Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "WebhookTypes.g.cs"),
            Path.Combine(tempRepo, "src", "Seiton.Core", "Generated", "WebhookTypes.g.cs"),
            overwrite: true);

        return tempRepo;
    }

    private static Dictionary<string, string[]> LoadEventFilterKeys(string repoRoot)
    {
        var primaryPath = ExpectedKeysSourcePathResolver.ResolvePrimary(repoRoot);
        var parser = new ExpectedKeysSourceParser();
        var model = parser.Parse(primaryPath);
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var section in model.Sections)
        {
            if (!section.Name.StartsWith("on-", StringComparison.Ordinal))
                continue;
            if (section.Name == "on-event")
                continue;

            var eventName = section.Name["on-".Length..].Replace('-', '_');
            result[eventName] = section.Keys.ToArray();
        }

        return result;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var slnxPath = Path.Combine(dir.FullName, "seiton.slnx");
            if (File.Exists(slnxPath))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found from test base directory.");
    }
}
