using System.Text;
using System.Text.Json;
using Seiton.Core.Generated;

namespace Seiton.Core.Tests;

public sealed class ExpectedKeysJobMappingKeyTests
{
    [Test]
    public async Task JobMappingKeyTable_MatchesCommittedExpectedKeysJobSection()
    {
        var repoRoot = FindRepoRoot();
        var canonicalPath = Path.Combine(
            repoRoot,
            "data",
            "sources",
            "expected-keys",
            "github",
            "expected-keys.json");

        using var doc = JsonDocument.Parse(File.ReadAllText(canonicalPath));
        var expected = doc.RootElement.GetProperty("sections")
            .EnumerateArray()
            .First(static section => section.GetProperty("name").GetString() == "job")
            .GetProperty("keys")
            .EnumerateArray()
            .Select(static key => key.GetString()!)
            .ToList();

        await Assert.That(ExpectedKeys.JobMappingKeyTable.KeyCount).IsEqualTo(expected.Count);

        for (var i = 0; i < expected.Count; i++)
        {
            var utf8Key = ExpectedKeys.JobMappingKeyTable.Utf8Key(i);
            var keyName = Encoding.UTF8.GetString(utf8Key);
            var isKnown = ExpectedKeys.IsKnownJobKey(utf8Key);
            await Assert.That(keyName).IsEqualTo(expected[i]);
            await Assert.That(isKnown).IsTrue();
        }
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
