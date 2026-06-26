using System.Text;
using System.Text.Json;
using Seiton.Core.Generated;

namespace Seiton.Core.Tests;

public sealed class StepSchemaMappingKeyTests
{
    [Test]
    public async Task MappingKeyTable_MatchesCommittedStepSchemaAllowedKeyUnion()
    {
        var repoRoot = FindRepoRoot();
        var canonicalPath = Path.Combine(
            repoRoot,
            "data",
            "sources",
            "step-schema",
            "github",
            "step-schema.json");

        using var doc = JsonDocument.Parse(File.ReadAllText(canonicalPath));
        var expected = doc.RootElement.GetProperty("forms")
            .EnumerateArray()
            .SelectMany(static form => form.GetProperty("allowedKeys").EnumerateArray())
            .Select(static key => key.GetString()!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static k => k, StringComparer.Ordinal)
            .ToList();

        await Assert.That(StepSchema.MappingKeyTable.KeyCount).IsEqualTo(expected.Count);

        for (var i = 0; i < expected.Count; i++)
        {
            var utf8Key = StepSchema.MappingKeyTable.Utf8Key(i);
            var keyName = Encoding.UTF8.GetString(utf8Key);
            var isKnown = StepSchema.IsKnownMappingKey(utf8Key);
            await Assert.That(keyName).IsEqualTo(expected[i]);
            await Assert.That(isKnown).IsTrue();
        }
    }

    [Test]
    public async Task PrimaryFormForMappingKey_MapsAllPrimaryKeys()
    {
        await Assert.That(StepSchema.PrimaryFormForMappingKey(StepSchema.MappingKey.Run)).IsEqualTo(StepSchema.FormId.Run);
        await Assert.That(StepSchema.PrimaryFormForMappingKey(StepSchema.MappingKey.WaitAll)).IsEqualTo(StepSchema.FormId.WaitAll);
        await Assert.That(StepSchema.IsPrimaryMappingKey(StepSchema.MappingKey.Background)).IsFalse();
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
