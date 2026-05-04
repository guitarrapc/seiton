using System.Text.Json;
using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Tests;

/// <summary>
/// Stage 2 JSON must include <c>rawSources</c> with per-file SHA-256 matching the raw bytes on disk
/// (same algorithm as <c>manifest.json</c> <c>rawFileHashes</c>; filenames must be manifest keys).
/// </summary>
public sealed class Stage2RawSourceMetadataTests
{
    [Test]
    public async Task Availability_Parse_IncludesRawSources_AlignedWithRawFiles()
    {
        await AssertParseRawSourcesMatchRawOnDisk(
            dataset: "availability",
            tempRepo => new GitHubAvailabilityFetcher().ParseLocalSourceFiles(tempRepo),
            parsedJsonRelativePath: Path.Combine("data", "sources", "availability", "github", "parsed", "docs-context-availability.json"));
    }

    [Test]
    public async Task Permissions_Parse_IncludesRawSources_AlignedWithRawFiles()
    {
        await AssertParseRawSourcesMatchRawOnDisk(
            dataset: "permissions",
            tempRepo => new GitHubPermissionsFetcher().ParseLocalSourceFiles(tempRepo),
            parsedJsonRelativePath: Path.Combine("data", "sources", "permissions", "github", "parsed", "permissions-scopes.json"));
    }

    [Test]
    public async Task ExpectedKeys_Parse_IncludesRawSources_AlignedWithRawFiles()
    {
        await AssertParseRawSourcesMatchRawOnDisk(
            dataset: "expected-keys",
            tempRepo => new GitHubExpectedKeysFetcher().ParseLocalSourceFiles(tempRepo),
            parsedJsonRelativePath: Path.Combine("data", "sources", "expected-keys", "github", "parsed", "expected-keys.json"));
    }

    [Test]
    public async Task Shells_Parse_IncludesRawSources_AlignedWithRawFiles()
    {
        await AssertParseRawSourcesMatchRawOnDisk(
            dataset: "shells",
            tempRepo => new GitHubShellsFetcher().ParseLocalSourceFiles(tempRepo),
            parsedJsonRelativePath: Path.Combine("data", "sources", "shells", "github", "parsed", "shells.json"));
    }

    private static async Task AssertParseRawSourcesMatchRawOnDisk(
        string dataset,
        Action<string> runParse,
        string parsedJsonRelativePath)
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithManifestRawAndDirs(repoRoot, dataset);

        try
        {
            runParse(tempRepo);

            var parsedPath = Path.Combine(tempRepo, parsedJsonRelativePath);
            using var doc = JsonDocument.Parse(File.ReadAllText(parsedPath));
            var root = doc.RootElement;

            await Assert.That(root.TryGetProperty("rawSources", out var rawSources)).IsTrue();

            var manifestPath = Path.Combine(tempRepo, "data", "sources", "manifest.json");
            using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var entry = manifestDoc.RootElement.GetProperty("entries").EnumerateArray()
                .First(e => string.Equals(e.GetProperty("dataset").GetString(), dataset, StringComparison.Ordinal));
            var hashes = entry.GetProperty("rawFileHashes");

            var entries = rawSources.EnumerateArray().ToList();
            await Assert.That(entries.Count).IsEqualTo(hashes.EnumerateObject().Count());

            foreach (var rs in entries)
            {
                var fileName = rs.GetProperty("fileName").GetString();
                var sha = rs.GetProperty("sha256").GetString();
                await Assert.That(fileName).IsNotNull();
                await Assert.That(sha).IsNotNull();

                if (!hashes.TryGetProperty(fileName!, out _))
                {
                    throw new InvalidOperationException($"rawSources fileName '{fileName}' not in manifest rawFileHashes keys for {dataset}");
                }

                var rawPathInTemp = FindRawFileInDatasetTree(Path.Combine(tempRepo, "data", "sources", dataset), fileName!);
                var expectedSha = SourceContentHasher.ComputeSha256(File.ReadAllText(rawPathInTemp));
                await Assert.That(sha).IsEqualTo(expectedSha);
            }
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    private static string FindRawFileInDatasetTree(string datasetSourcesRoot, string fileName)
    {
        foreach (var file in Directory.EnumerateFiles(datasetSourcesRoot, fileName, SearchOption.AllDirectories))
        {
            return file;
        }

        throw new FileNotFoundException($"Raw file '{fileName}' not under {datasetSourcesRoot}");
    }
    private static string CreateTempRepoWithManifestRawAndDirs(string repoRoot, string dataset)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-update-tests-" + Guid.NewGuid().ToString("N"));
        var srcRoot = Path.Combine(repoRoot, "data", "sources");

        CopyFile(Path.Combine(srcRoot, "manifest.json"), Path.Combine(tempRepo, "data", "sources", "manifest.json"));

        foreach (var relativeRaw in EnumerateRawFilesUnderDataset(repoRoot, dataset))
        {
            var dest = Path.Combine(tempRepo, relativeRaw);
            var src = Path.Combine(repoRoot, relativeRaw);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
        }

        return tempRepo;
    }

    private static IEnumerable<string> EnumerateRawFilesUnderDataset(string repoRoot, string dataset)
    {
        var manifestPath = Path.Combine(repoRoot, "data", "sources", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var entry = doc.RootElement.GetProperty("entries").EnumerateArray()
            .First(e => string.Equals(e.GetProperty("dataset").GetString(), dataset, StringComparison.Ordinal));
        var hashKeys = entry.GetProperty("rawFileHashes").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var rawDir = Path.Combine(repoRoot, "data", "sources", dataset);
        foreach (var file in Directory.EnumerateFiles(rawDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(repoRoot, file);
            if (rel.Contains($"{Path.DirectorySeparatorChar}raw{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                var baseName = Path.GetFileName(file);
                if (hashKeys.Contains(baseName))
                {
                    yield return rel;
                }
            }
        }
    }

    private static void CopyFile(string src, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest, overwrite: true);
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
