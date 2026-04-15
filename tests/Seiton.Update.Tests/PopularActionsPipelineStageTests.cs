using System.Text.Json;
using Seiton.Update.Sources;

namespace Seiton.Update.Tests;

public sealed class PopularActionsPipelineStageTests
{
    [Test]
    public async Task ValidateTargetsConfig_WhenValid_DoesNotThrow()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var fetcher = new GitHubPopularActionsFetcher();
            fetcher.ValidateTargetsConfig(tempRepo);
            var targetsPath = Path.Combine(tempRepo, "data", "sources", "popular-actions", "targets.json");
            await Assert.That(File.Exists(targetsPath)).IsTrue();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task ValidateTargetsConfig_WhenDuplicateRawFileName_Throws()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var targetsPath = Path.Combine(tempRepo, "data", "sources", "popular-actions", "targets.json");
            var targetsJson = """
                        {
                            "schemaVersion": 1,
                            "targets": [
                                {
                                    "actionRef": "actions/checkout@v4",
                                    "uses": "actions/checkout",
                                    "url": "https://raw.githubusercontent.com/actions/checkout/v4/action.yml",
                                    "rawFileName": "dup.action.yml"
                                },
                                {
                                    "actionRef": "actions/setup-node@v4",
                                    "uses": "actions/setup-node",
                                    "url": "https://raw.githubusercontent.com/actions/setup-node/v4/action.yml",
                                    "rawFileName": "dup.action.yml"
                                }
                            ]
                        }
                        """;
            File.WriteAllText(targetsPath, targetsJson.Replace("\r\n", "\n"));

            var fetcher = new GitHubPopularActionsFetcher();
            await Assert.That(() => fetcher.ValidateTargetsConfig(tempRepo)).ThrowsException();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task ValidateTargetsConfig_WhenConfigMissing_Throws()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var targetsPath = Path.Combine(tempRepo, "data", "sources", "popular-actions", "targets.json");
            File.Delete(targetsPath);

            var fetcher = new GitHubPopularActionsFetcher();
            await Assert.That(() => fetcher.ValidateTargetsConfig(tempRepo)).ThrowsException();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task ParseLocalSourceFiles_UsesTargetsConfigToSelectActionSet()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var targetsPath = Path.Combine(tempRepo, "data", "sources", "popular-actions", "targets.json");
            var targetsJson = """
                        {
                            "schemaVersion": 1,
                            "targets": [
                                {
                                    "actionRef": "actions/checkout@v4",
                                    "uses": "actions/checkout",
                                    "url": "https://raw.githubusercontent.com/actions/checkout/v4/action.yml",
                                    "rawFileName": "actions_checkout_v4.action.yml"
                                }
                            ]
                        }
                        """;
            File.WriteAllText(targetsPath, targetsJson.Replace("\r\n", "\n"));

            var fetcher = new GitHubPopularActionsFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var parsedPath = Path.Combine(tempRepo, "data", "sources", "popular-actions", "github", "parsed", "popular-actions-metadata.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(parsedPath));

            var actions = doc.RootElement.GetProperty("actions").EnumerateArray().ToList();
            await Assert.That(actions.Count).IsEqualTo(1);
            await Assert.That(actions[0].GetProperty("uses").GetString()).IsEqualTo("actions/checkout");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task ParseLocalSourceFiles_WhenTargetsConfigHasDuplicateUses_Throws()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var targetsPath = Path.Combine(tempRepo, "data", "sources", "popular-actions", "targets.json");
            var targetsJson = """
                        {
                            "schemaVersion": 1,
                            "targets": [
                                {
                                    "actionRef": "actions/checkout@v4",
                                    "uses": "actions/checkout",
                                    "url": "https://raw.githubusercontent.com/actions/checkout/v4/action.yml",
                                    "rawFileName": "actions_checkout_v4.action.yml"
                                },
                                {
                                    "actionRef": "actions/checkout@v4",
                                    "uses": "actions/checkout",
                                    "url": "https://raw.githubusercontent.com/actions/setup-node/v4/action.yml",
                                    "rawFileName": "actions_setup-node_v4.action.yml"
                                }
                            ]
                        }
                        """;
            File.WriteAllText(targetsPath, targetsJson.Replace("\r\n", "\n"));

            var fetcher = new GitHubPopularActionsFetcher();
            await Assert.That(() => fetcher.ParseLocalSourceFiles(tempRepo)).ThrowsException();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task ParseLocalSourceFiles_WhenTargetsConfigMissingRequiredField_Throws()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var targetsPath = Path.Combine(tempRepo, "data", "sources", "popular-actions", "targets.json");
            var targetsJson = """
                        {
                            "schemaVersion": 1,
                            "targets": [
                                {
                                    "actionRef": "actions/checkout@v4",
                                    "uses": "",
                                    "url": "https://raw.githubusercontent.com/actions/checkout/v4/action.yml",
                                    "rawFileName": "actions_checkout_v4.action.yml"
                                }
                            ]
                        }
                        """;
            File.WriteAllText(targetsPath, targetsJson.Replace("\r\n", "\n"));

            var fetcher = new GitHubPopularActionsFetcher();
            await Assert.That(() => fetcher.ParseLocalSourceFiles(tempRepo)).ThrowsException();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task ParseLocalSourceFiles_ProducesOutputMatchingCommittedParsedFile()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var fetcher = new GitHubPopularActionsFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var actual = File.ReadAllText(
                Path.Combine(tempRepo, "data", "sources", "popular-actions", "github", "parsed", "popular-actions-metadata.json"))
                .Replace("\r\n", "\n");

            var expected = File.ReadAllText(
                Path.Combine(repoRoot, "data", "sources", "popular-actions", "github", "parsed", "popular-actions-metadata.json"))
                .Replace("\r\n", "\n");

            await Assert.That(actual).IsEqualTo(expected);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task MergeParsedSources_ProducesOutputMatchingCommittedSnapshot()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            var fetcher = new GitHubPopularActionsFetcher();
            fetcher.MergeParsedSources(tempRepo);

            var actual = File.ReadAllText(
                Path.Combine(tempRepo, "data", "sources", "popular-actions", "github", "popular_actions.json"))
                .Replace("\r\n", "\n");

            var expected = File.ReadAllText(
                Path.Combine(repoRoot, "data", "sources", "popular-actions", "github", "popular_actions.json"))
                .Replace("\r\n", "\n");

            await Assert.That(actual).IsEqualTo(expected);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task MergeParsedSources_WhenTargetsConfigInvalid_Throws()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            var targetsPath = Path.Combine(tempRepo, "data", "sources", "popular-actions", "targets.json");
            var targetsJson = """
                        {
                            "schemaVersion": 1,
                            "targets": [
                                {
                                    "actionRef": "actions/checkout@v4",
                                    "uses": "actions/checkout",
                                    "url": "https://raw.githubusercontent.com/actions/checkout/v4/action.yml",
                                    "rawFileName": "dup.action.yml"
                                },
                                {
                                    "actionRef": "actions/setup-node@v4",
                                    "uses": "actions/setup-node",
                                    "url": "https://raw.githubusercontent.com/actions/setup-node/v4/action.yml",
                                    "rawFileName": "dup.action.yml"
                                }
                            ]
                        }
                        """;
            File.WriteAllText(targetsPath, targetsJson.Replace("\r\n", "\n"));

            var fetcher = new GitHubPopularActionsFetcher();
            await Assert.That(() => fetcher.MergeParsedSources(tempRepo)).ThrowsException();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task MergeParsedSources_SnapshotContainsKnownActionAndInputs()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            var fetcher = new GitHubPopularActionsFetcher();
            fetcher.MergeParsedSources(tempRepo);

            var path = Path.Combine(tempRepo, "data", "sources", "popular-actions", "github", "popular_actions.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            var checkout = doc.RootElement
                .GetProperty("actions")
                .EnumerateArray()
                .FirstOrDefault(x => x.GetProperty("uses").GetString() == "actions/checkout");

            await Assert.That(checkout.ValueKind).IsNotEqualTo(JsonValueKind.Undefined);

            var inputNames = checkout.GetProperty("inputs")
                .EnumerateArray()
                .Select(x => x.GetString())
                .ToHashSet(StringComparer.Ordinal);

            await Assert.That(inputNames).Contains("fetch-depth");
            await Assert.That(inputNames).Contains("repository");
            await Assert.That(inputNames).Contains("token");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    static string CreateTempRepoWithRaw(string repoRoot)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-update-tests-" + Guid.NewGuid().ToString("N"));
        var srcRaw = Path.Combine(repoRoot, "data", "sources", "popular-actions", "github", "raw");
        var srcTargets = Path.Combine(repoRoot, "data", "sources", "popular-actions", "targets.json");
        var dstTargetsDir = Path.Combine(tempRepo, "data", "sources", "popular-actions");
        var dstRaw = Path.Combine(tempRepo, "data", "sources", "popular-actions", "github", "raw");
        Directory.CreateDirectory(dstTargetsDir);
        Directory.CreateDirectory(dstRaw);

        File.Copy(srcTargets, Path.Combine(dstTargetsDir, "targets.json"), overwrite: true);

        foreach (var file in Directory.GetFiles(srcRaw))
        {
            File.Copy(file, Path.Combine(dstRaw, Path.GetFileName(file)));
        }

        return tempRepo;
    }

    static string CreateTempRepoWithParsed(string repoRoot)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-update-tests-" + Guid.NewGuid().ToString("N"));
        var srcParsed = Path.Combine(repoRoot, "data", "sources", "popular-actions", "github", "parsed");
        var srcTargets = Path.Combine(repoRoot, "data", "sources", "popular-actions", "targets.json");
        var dstTargetsDir = Path.Combine(tempRepo, "data", "sources", "popular-actions");
        var dstParsed = Path.Combine(tempRepo, "data", "sources", "popular-actions", "github", "parsed");
        Directory.CreateDirectory(dstTargetsDir);
        Directory.CreateDirectory(dstParsed);

        File.Copy(srcTargets, Path.Combine(dstTargetsDir, "targets.json"), overwrite: true);

        foreach (var file in Directory.GetFiles(srcParsed))
        {
            File.Copy(file, Path.Combine(dstParsed, Path.GetFileName(file)));
        }

        return tempRepo;
    }

    static string FindRepoRoot()
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
