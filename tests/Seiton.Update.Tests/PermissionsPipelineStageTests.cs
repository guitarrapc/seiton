using System.Text.Json;
using System.Text.Json.Nodes;
using Seiton.Update.Sources;

namespace Seiton.Update.Tests;

public sealed class PermissionsPipelineStageTests
{
    [Test]
    public async Task ParseThenMerge_ProducesPrimaryMatchingCommittedSnapshot()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRawOnly(repoRoot);

        try
        {
            var fetcher = new GitHubPermissionsFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);
            fetcher.MergeParsedSources(tempRepo);

            var actual = File.ReadAllText(
                    Path.Combine(tempRepo, "data", "sources", "permissions", "github", "permissions.json"))
                .Replace("\r\n", "\n");

            var expected = File.ReadAllText(
                    Path.Combine(repoRoot, "data", "sources", "permissions", "github", "permissions.json"))
                .Replace("\r\n", "\n");

            await Assert.That(actual).IsEqualTo(expected);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    /// <summary>
    /// The docs table dropped 'models' when GitHub Models was retired, but workflows declaring it
    /// still run. The merge stage must keep the scope so seiton does not report 'unknown permission scope'.
    /// </summary>
    [Test]
    public async Task MergeParsedSources_RetiredModelsScope_IsRetainedAsReadNone()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRawOnly(repoRoot);

        try
        {
            var fetcher = new GitHubPermissionsFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            // Simulate the docs table not listing the scope, whether or not GitHub re-adds it later.
            var parsedPath = Path.Combine(
                tempRepo, "data", "sources", "permissions", "github", "parsed", "permissions-scopes.json");
            RemoveScopeFromParsed(parsedPath, "models");

            fetcher.MergeParsedSources(tempRepo);

            var mergedPath = Path.Combine(
                tempRepo, "data", "sources", "permissions", "github", "permissions.json");
            using var mergedDoc = JsonDocument.Parse(File.ReadAllText(mergedPath));
            var models = mergedDoc.RootElement.GetProperty("scopes")
                .EnumerateArray()
                .Single(static s => s.GetProperty("name").GetString() == "models");

            var allowed = models.GetProperty("allowed")
                .EnumerateArray()
                .Select(static v => v.GetString()!)
                .ToArray();

            await Assert.That(allowed).IsEquivalentTo(new[] { "read", "none" });

            // The note drives the deprecated-permissions diagnostic message.
            var note = models.GetProperty("deprecationNote").GetString();
            await Assert.That(note).Contains("remove it from permissions");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    /// <summary>
    /// Deprecation is a property of the scope, not of the compat injection: if the docs table
    /// lists a retired scope again, the merge stage must still mark it deprecated instead of
    /// silently turning the <c>deprecated-permissions</c> rule into a no-op.
    /// </summary>
    [Test]
    public async Task MergeParsedSources_RetiredScopeAlsoListedInDocs_KeepsDeprecationNote()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRawOnly(repoRoot);

        try
        {
            var fetcher = new GitHubPermissionsFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            // Simulate the docs table listing 'models' again. Removing first keeps this a
            // single-entry setup even after GitHub really re-adds the scope.
            var parsedPath = Path.Combine(
                tempRepo, "data", "sources", "permissions", "github", "parsed", "permissions-scopes.json");
            var parsedNode = RemoveScopeFromParsed(parsedPath, "models");
            parsedNode["scopes"]!.AsArray().Add(new JsonObject
            {
                ["name"] = "models",
                ["allowed"] = new JsonArray("read", "none"),
            });
            File.WriteAllText(parsedPath, parsedNode.ToJsonString());

            fetcher.MergeParsedSources(tempRepo);

            var mergedPath = Path.Combine(
                tempRepo, "data", "sources", "permissions", "github", "permissions.json");
            using var mergedDoc = JsonDocument.Parse(File.ReadAllText(mergedPath));
            var models = mergedDoc.RootElement.GetProperty("scopes")
                .EnumerateArray()
                .Single(static s => s.GetProperty("name").GetString() == "models");

            await Assert.That(models.TryGetProperty("deprecationNote", out var note)).IsTrue();
            await Assert.That(note.GetString()).Contains("remove it from permissions");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    /// <summary>
    /// The docs table is rendered from Liquid conditionals, so the same scope can appear twice.
    /// Passing duplicates through would emit duplicate switch labels in the generated file.
    /// </summary>
    [Test]
    public async Task MergeParsedSources_DuplicateScopeInDocs_CollapsesToSingleEntry()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRawOnly(repoRoot);

        try
        {
            var fetcher = new GitHubPermissionsFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var parsedPath = Path.Combine(
                tempRepo, "data", "sources", "permissions", "github", "parsed", "permissions-scopes.json");
            var parsedNode = JsonNode.Parse(File.ReadAllText(parsedPath))!;
            parsedNode["scopes"]!.AsArray().Add(new JsonObject
            {
                ["name"] = "contents",
                ["allowed"] = new JsonArray("read", "write", "none"),
            });
            File.WriteAllText(parsedPath, parsedNode.ToJsonString());

            fetcher.MergeParsedSources(tempRepo);

            var mergedPath = Path.Combine(
                tempRepo, "data", "sources", "permissions", "github", "permissions.json");
            using var mergedDoc = JsonDocument.Parse(File.ReadAllText(mergedPath));
            var contents = mergedDoc.RootElement.GetProperty("scopes")
                .EnumerateArray()
                .Where(static s => s.GetProperty("name").GetString() == "contents")
                .ToArray();

            await Assert.That(contents).Count().IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    /// <summary>
    /// Two entries for one scope with different access values are ambiguous: picking either one
    /// silently changes what the linter accepts, so the merge must fail instead.
    /// </summary>
    [Test]
    public async Task MergeParsedSources_ConflictingDuplicateScope_Throws()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRawOnly(repoRoot);

        try
        {
            var fetcher = new GitHubPermissionsFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var parsedPath = Path.Combine(
                tempRepo, "data", "sources", "permissions", "github", "parsed", "permissions-scopes.json");
            var parsedNode = JsonNode.Parse(File.ReadAllText(parsedPath))!;
            parsedNode["scopes"]!.AsArray().Add(new JsonObject
            {
                ["name"] = "contents",
                ["allowed"] = new JsonArray("read", "none"),
            });
            File.WriteAllText(parsedPath, parsedNode.ToJsonString());

            var ex = Assert.Throws<InvalidDataException>(() => fetcher.MergeParsedSources(tempRepo));
            await Assert.That(ex!.Message).Contains("contents");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    /// <summary>Drops a scope from a parsed snapshot and rewrites the file. Returns the parsed node.</summary>
    private static JsonNode RemoveScopeFromParsed(string parsedPath, string scopeName)
    {
        var parsedNode = JsonNode.Parse(File.ReadAllText(parsedPath))!;
        var scopes = parsedNode["scopes"]!.AsArray();
        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            if (scopes[i]!["name"]!.GetValue<string>() == scopeName)
            {
                scopes.RemoveAt(i);
            }
        }

        File.WriteAllText(parsedPath, parsedNode.ToJsonString());
        return parsedNode;
    }

    private static string CreateTempRepoWithRawOnly(string repoRoot)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-perm-tests-" + Guid.NewGuid().ToString("N"));
        var srcRawDir = Path.Combine(repoRoot, "data", "sources", "permissions", "github", "raw");
        var dstRawDir = Path.Combine(tempRepo, "data", "sources", "permissions", "github", "raw");
        Directory.CreateDirectory(dstRawDir);

        foreach (var file in Directory.GetFiles(srcRawDir))
        {
            File.Copy(file, Path.Combine(dstRawDir, Path.GetFileName(file)));
        }

        // The parse/merge stages resolve source URLs through the manifest.
        var dstDataDir = Path.Combine(tempRepo, "data", "sources");
        Directory.CreateDirectory(dstDataDir);
        File.Copy(
            Path.Combine(repoRoot, "data", "sources", "manifest.json"),
            Path.Combine(dstDataDir, "manifest.json"));

        return tempRepo;
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
