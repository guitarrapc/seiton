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
