using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Tests;

public sealed class StepSchemaMergeTests
{
    [Test]
    public async Task Merge_Supplemental_AddsParallelFormsAndBackgroundModifier()
    {
        var repoRoot = FindRepoRoot();
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
        var merged = new StepSchemaMerger().Merge(parsed, supplemental);

        await Assert.That(merged.Forms).Count().IsEqualTo(6);
        await Assert.That(merged.Forms.Select(static f => f.Id)).Contains("cancel");
        await Assert.That(merged.Forms.Select(static f => f.Id)).Contains("parallel");
        await Assert.That(merged.Forms.Select(static f => f.Id)).Contains("run");
        await Assert.That(merged.Forms.Select(static f => f.Id)).Contains("uses");
        await Assert.That(merged.Forms.Select(static f => f.Id)).Contains("wait");
        await Assert.That(merged.Forms.Select(static f => f.Id)).Contains("wait-all");

        var runForm = merged.Forms.First(static f => f.Id == "run");
        await Assert.That(runForm.AllowedKeys).Contains("background");
        await Assert.That(runForm.AllowedKeys).DoesNotContain("wait");

        var usesForm = merged.Forms.First(static f => f.Id == "uses");
        await Assert.That(usesForm.AllowedKeys).Contains("with");
        await Assert.That(usesForm.AllowedKeys).DoesNotContain("run");

        var waitForm = merged.Forms.First(static f => f.Id == "wait");
        await Assert.That(waitForm.AllowedKeys).Contains("wait");
        await Assert.That(waitForm.AllowedKeys).DoesNotContain("run");
        await Assert.That(waitForm.AllowedKeys).DoesNotContain("background");
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
