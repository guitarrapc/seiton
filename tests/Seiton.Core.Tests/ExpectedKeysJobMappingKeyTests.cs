using System.Text;
using System.Text.Json;
using Seiton.Core.Generated;
using Seiton.Core.Parsing;

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

    [Test]
    public async Task JobKeys_ConstString_MatchesJobMappingKeyTableOrdinals()
    {
        var fromConst = ExpectedKeys.JobKeys
            .Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static quoted => quoted.Trim('"'))
            .ToList();

        await Assert.That(fromConst.Count).IsEqualTo(ExpectedKeys.JobMappingKeyTable.KeyCount);

        for (var i = 0; i < fromConst.Count; i++)
        {
            var tableKey = Encoding.UTF8.GetString(ExpectedKeys.JobMappingKeyTable.Utf8Key(i));
            await Assert.That(tableKey).IsEqualTo(fromConst[i]);
        }
    }

    [Test]
    public async Task IsKnownJobKey_DispatchTableAndClassifier_AgreeOnKnownKeys()
    {
        for (var i = 0; i < ExpectedKeys.JobMappingKeyTable.KeyCount; i++)
        {
            var keyBytes = ExpectedKeys.JobMappingKeyTable.Utf8Key(i).ToArray();
            var dispatchMatch = Utf8MappingDispatch.TryMatchFirstOrdered<ExpectedKeys.JobMappingKeyTable>(keyBytes, out var ordinal);
            await Assert.That(dispatchMatch).IsTrue();
            await Assert.That(ordinal).IsEqualTo(i);
            await Assert.That(ExpectedKeys.IsKnownJobKey(keyBytes)).IsTrue();
        }
    }

    [Test]
    public async Task IsKnownJobKey_UnknownArbitraryKey_ReturnsFalse()
    {
        await Assert.That(ExpectedKeys.IsKnownJobKey("not-a-job-key"u8)).IsFalse();
    }

    [Test]
    public async Task IsKnownJobKey_ShorterPrefixOfKnownKey_ReturnsFalse()
    {
        await Assert.That(ExpectedKeys.IsKnownJobKey("runs"u8)).IsFalse();
        await Assert.That(ExpectedKeys.IsKnownJobKey("continue-on"u8)).IsFalse();
        await Assert.That(ExpectedKeys.IsKnownJobKey("timeout"u8)).IsFalse();
    }

    [Test]
    public async Task IsKnownJobKey_LongerSupersetOfKnownKey_ReturnsFalse()
    {
        await Assert.That(ExpectedKeys.IsKnownJobKey("runs-on-extra"u8)).IsFalse();
        await Assert.That(ExpectedKeys.IsKnownJobKey("steps-extra"u8)).IsFalse();
        await Assert.That(ExpectedKeys.IsKnownJobKey("continue-on-error-now"u8)).IsFalse();
    }

    [Test]
    public async Task IsKnownJobKey_CaseMismatch_ReturnsFalse()
    {
        await Assert.That(ExpectedKeys.IsKnownJobKey("Runs-On"u8)).IsFalse();
        await Assert.That(ExpectedKeys.IsKnownJobKey("STEPS"u8)).IsFalse();
        await Assert.That(ExpectedKeys.IsKnownJobKey("USES"u8)).IsFalse();
    }

    [Test]
    public async Task IsKnownJobKey_KeyFromOtherSection_ReturnsFalse()
    {
        await Assert.That(ExpectedKeys.IsKnownJobKey("cron"u8)).IsFalse();
        await Assert.That(ExpectedKeys.IsKnownJobKey("branches"u8)).IsFalse();
        await Assert.That(ExpectedKeys.IsKnownJobKey("matrix"u8)).IsFalse();
    }

    [Test]
    public async Task IsKnownJobKey_EmptySpan_ReturnsFalse()
    {
        await Assert.That(ExpectedKeys.IsKnownJobKey(ReadOnlySpan<byte>.Empty)).IsFalse();
    }

    [Test]
    public async Task ParseJob_UnknownKey_EmitsUnexpectedKeyDiagnostic()
    {
        var yaml = TestHelper.NormalizeEol("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                not-a-job-key: true
                steps:
                  - run: echo ok
            """);

        using var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-unknown-key.yml");
        await Assert.That(result.Diagnostics.Any(static d =>
            d.Message.Contains("jobs.'build' has unexpected key \"not-a-job-key\" for \"job\" section", StringComparison.Ordinal))).IsTrue();
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
