using Seiton.Update.Generators;
using Seiton.Update.Model;
using Seiton.Update.Parsers;

namespace Seiton.Update.Tests;

public sealed class ExpectedKeysCSharpGeneratorTests
{
    [Test]
    public async Task Generate_CommittedModel_EmitsJobMappingKeyTableAndMetadata()
    {
        var model = LoadCommittedModel();
        var output = new ExpectedKeysCSharpGenerator().Generate(model);

        await Assert.That(output).Contains("internal enum JobMappingKey : byte");
        await Assert.That(output).Contains("internal readonly struct JobMappingKeyTable : global::Seiton.Core.Parsing.IUtf8OrderedKeyTable");
        await Assert.That(output).Contains("public static int KeyCount => 20;");
        await Assert.That(output).Contains("RunsOn = 11,");
        await Assert.That(output).Contains("internal static bool IsKnownJobKey(ReadOnlySpan<byte> keyUtf8)");
        await Assert.That(output).Contains("keyUtf8.SequenceEqual(\"runs-on\"u8)");
    }

    [Test]
    public async Task Generate_CommittedModel_JobMappingKeyTableMatchesJobSectionKeys()
    {
        var model = LoadCommittedModel();
        var jobSection = model.Sections.First(static s => s.Name == "job");
        var output = new ExpectedKeysCSharpGenerator().Generate(model);

        foreach (var key in jobSection.Keys)
        {
            await Assert.That(output).Contains($"\"{key}\"u8");
        }
    }

    [Test]
    public async Task Generate_ModelWithoutJobSection_OmitsJobMappingKeyArtifacts()
    {
        var model = new ExpectedKeysModel(
        [
            new ExpectedKeySection("workflow", "Top-level workflow keys", ["name", "on", "jobs"]),
        ]);

        var output = new ExpectedKeysCSharpGenerator().Generate(model);

        await Assert.That(output).DoesNotContain("JobMappingKeyTable");
        await Assert.That(output).DoesNotContain("IsKnownJobKey");
        await Assert.That(output).Contains("internal const string WorkflowKeys");
    }

    private static ExpectedKeysModel LoadCommittedModel()
    {
        var repoRoot = FindRepoRoot();
        var canonicalPath = Path.Combine(
            repoRoot,
            "data",
            "sources",
            "expected-keys",
            "github",
            "expected-keys.json");

        return new ExpectedKeysSourceParser().Parse(canonicalPath);
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
