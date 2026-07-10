using Seiton.Update.Generators;
using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Tests;

public sealed class StepSchemaCSharpGeneratorTests
{
    [Test]
    public async Task Generate_MergedModel_EmitsFormKeyConstants()
    {
        var repoRoot = FindRepoRoot();
        var canonicalPath = Path.Combine(
            repoRoot,
            "data",
            "sources",
            "step-schema",
            "github",
            "step-schema.json");

        StepSchemaModel model;
        if (File.Exists(canonicalPath))
        {
            model = new StepSchemaSourceParser().Parse(canonicalPath);
        }
        else
        {
            var schemaPath = Path.Combine(
                repoRoot,
                "data",
                "sources",
                "step-schema",
                "github",
                "raw",
                "github-workflow.schema.json");
            var supplementalPath = Path.Combine(
                repoRoot,
                "data",
                "sources",
                "step-schema",
                "github",
                "supplemental-step-schema.json");
            var parsed = new GitHubWorkflowStepSchemaParser().ParseFile(schemaPath);
            var supplemental = new StepSchemaSourceParser().ParseSupplemental(supplementalPath);
            model = new StepSchemaMerger().Merge(parsed, supplemental);
        }

        var output = new StepSchemaCSharpGenerator().Generate(model);

        await Assert.That(output).Contains("internal const string WaitStepKeys");
        await Assert.That(output).Contains("\\\"wait\\\"");
        await Assert.That(output).DoesNotContain("\\\"run\\\"\", \\\"wait\\\"");
        await Assert.That(output).Contains("internal const string ActionStepKeys = UsesStepKeys;");
        await Assert.That(output).Contains("internal static bool IsModifierAllowed(FormId formId, ReadOnlySpan<byte> keyUtf8)");
        await Assert.That(output).Contains("keyUtf8.SequenceEqual(\"background\"u8)");
        await Assert.That(output).DoesNotContain("IsBackgroundModifierAllowed");
    }

    [Test]
    public async Task Generate_MergedModel_EmitsMappingKeyTableAndMetadata()
    {
        var model = LoadCommittedModel();
        var output = new StepSchemaCSharpGenerator().Generate(model);

        await Assert.That(output).Contains("internal enum MappingKey : byte");
        await Assert.That(output).Contains("internal readonly struct MappingKeyTable : global::Seiton.Core.Parsing.IUtf8OrderedKeyTable");
        await Assert.That(output).Contains("public static int KeyCount => 16;");
        await Assert.That(output).Contains("MappingKey.WaitAll =");
        await Assert.That(output).Contains("internal static bool IsKnownMappingKey(ReadOnlySpan<byte> keyUtf8)");
        await Assert.That(output).Contains("internal static bool IsPrimaryMappingKey(MappingKey key)");
        await Assert.That(output).Contains("internal static FormId PrimaryFormForMappingKey(MappingKey key)");
        await Assert.That(output).Contains("internal static string GetExpectedKeys(FormId formId)");
    }

    [Test]
    public async Task Generate_MergedModel_MappingKeyTableMatchesAllowedKeyUnion()
    {
        var model = LoadCommittedModel();
        var expected = model.Forms
            .SelectMany(static f => f.AllowedKeys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static k => k, StringComparer.Ordinal)
            .ToList();

        var output = new StepSchemaCSharpGenerator().Generate(model);
        foreach (var key in expected)
        {
            await Assert.That(output).Contains($"\"{key}\"u8");
        }
    }

    private static StepSchemaModel LoadCommittedModel()
    {
        var repoRoot = FindRepoRoot();
        var canonicalPath = Path.Combine(
            repoRoot,
            "data",
            "sources",
            "step-schema",
            "github",
            "step-schema.json");

        if (File.Exists(canonicalPath))
        {
            return new StepSchemaSourceParser().Parse(canonicalPath);
        }

        var schemaPath = Path.Combine(
            repoRoot,
            "data",
            "sources",
            "step-schema",
            "github",
            "raw",
            "github-workflow.schema.json");
        var supplementalPath = Path.Combine(
            repoRoot,
            "data",
            "sources",
            "step-schema",
            "github",
            "supplemental-step-schema.json");
        var parsed = new GitHubWorkflowStepSchemaParser().ParseFile(schemaPath);
        var supplemental = new StepSchemaSourceParser().ParseSupplemental(supplementalPath);
        return new StepSchemaMerger().Merge(parsed, supplemental);
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
