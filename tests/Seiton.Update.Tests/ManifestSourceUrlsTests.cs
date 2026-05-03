using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Services;

namespace Seiton.Update.Tests;

public sealed class ManifestSourceUrlsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Test]
    public async Task Resolve_WhenDatasetMissing_Throws()
    {
        var dir = NewTempDir();
        try
        {
            WriteManifest(dir, []);
            await Assert.That(() => ManifestSourceUrls.Resolve(dir, "availability", 1))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Resolve_WhenSourceUrlsEmpty_Throws()
    {
        var dir = NewTempDir();
        try
        {
            WriteManifest(dir,
            [
                new SourceManifestEntry
                {
                    Dataset = "availability",
                    SourceUrls = [],
                    FetchedAtUtc = "2026-01-01T00:00:00+00:00",
                    RawFileHashes = [],
                },
            ]);
            await Assert.That(() => ManifestSourceUrls.Resolve(dir, "availability", 1))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Resolve_WhenExpectedCountMismatch_Throws()
    {
        var dir = NewTempDir();
        try
        {
            WriteManifest(dir,
            [
                new SourceManifestEntry
                {
                    Dataset = "webhooks",
                    SourceUrls =
                    [
                        "https://example.com/a",
                        "https://example.com/b",
                    ],
                    FetchedAtUtc = "2026-01-01T00:00:00+00:00",
                    RawFileHashes = [],
                },
            ]);
            await Assert.That(() => ManifestSourceUrls.Resolve(dir, "webhooks", 1))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Resolve_WhenSingleUrl_ReturnsFirst()
    {
        var dir = NewTempDir();
        try
        {
            const string url = "https://raw.githubusercontent.com/github/docs/main/example.md";
            WriteManifest(dir,
            [
                new SourceManifestEntry
                {
                    Dataset = "availability",
                    SourceUrls = [url],
                    FetchedAtUtc = "2026-01-01T00:00:00+00:00",
                    RawFileHashes = [],
                },
            ]);
            var actual = ManifestSourceUrls.ResolveSingle(dir, "availability");
            await Assert.That(actual).IsEqualTo(url);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Resolve_WhenEntriesNullInJson_Throws()
    {
        var dir = NewTempDir();
        try
        {
            WriteRawManifest(dir, """
                {
                  "schemaVersion": 1,
                  "entries": null
                }
                """);
            await Assert.That(() => ManifestSourceUrls.Resolve(dir, "availability", 1))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Resolve_WhenSourceUrlsNullInJson_Throws()
    {
        var dir = NewTempDir();
        try
        {
            WriteRawManifest(dir, """
                {
                  "schemaVersion": 1,
                  "entries": [
                    {
                      "dataset": "availability",
                      "sourceUrls": null,
                      "fetchedAtUtc": "2026-01-01T00:00:00+00:00",
                      "rawFileHashes": {}
                    }
                  ]
                }
                """);
            await Assert.That(() => ManifestSourceUrls.Resolve(dir, "availability", 1))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Resolve_WhenSourceUrlsContainsBlankSlot_Throws()
    {
        var dir = NewTempDir();
        try
        {
            WriteManifest(dir,
            [
                new SourceManifestEntry
                {
                    Dataset = "webhooks",
                    SourceUrls =
                    [
                        "https://example.com/a",
                        " ",
                        "https://example.com/b",
                    ],
                    FetchedAtUtc = "2026-01-01T00:00:00+00:00",
                    RawFileHashes = [],
                },
            ]);
            await Assert.That(() => ManifestSourceUrls.Resolve(dir, "webhooks", 2))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Resolve_WhenUrlUsesHttp_Throws()
    {
        var dir = NewTempDir();
        try
        {
            WriteManifest(dir,
            [
                new SourceManifestEntry
                {
                    Dataset = "availability",
                    SourceUrls = ["http://raw.githubusercontent.com/github/docs/main/x.md"],
                    FetchedAtUtc = "2026-01-01T00:00:00+00:00",
                    RawFileHashes = [],
                },
            ]);
            await Assert.That(() => ManifestSourceUrls.Resolve(dir, "availability", 1))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Resolve_WhenUrlNotAbsolute_Throws()
    {
        var dir = NewTempDir();
        try
        {
            WriteManifest(dir,
            [
                new SourceManifestEntry
                {
                    Dataset = "availability",
                    SourceUrls = ["/relative/path"],
                    FetchedAtUtc = "2026-01-01T00:00:00+00:00",
                    RawFileHashes = [],
                },
            ]);
            await Assert.That(() => ManifestSourceUrls.Resolve(dir, "availability", 1))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), "seiton-manifest-url-tests-" + Guid.NewGuid().ToString("N"));

    private static void WriteManifest(string repoRoot, List<SourceManifestEntry> entries)
    {
        var path = Path.Combine(repoRoot, "data", "sources", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var manifest = new SourceManifest
        {
            SchemaVersion = 1,
            Entries = entries,
        };
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(path, json.Replace("\r\n", "\n"));
    }

    private static void WriteRawManifest(string repoRoot, string json)
    {
        var path = Path.Combine(repoRoot, "data", "sources", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json.Trim().Replace("\r\n", "\n"));
    }
}
