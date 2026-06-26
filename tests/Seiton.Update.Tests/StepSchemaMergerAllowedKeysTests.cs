using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Tests;

public sealed class StepSchemaMergerAllowedKeysTests
{
    private readonly StepSchemaMerger merger = new();
    private readonly string schemaPath;
    private readonly string supplementalPath;

    public StepSchemaMergerAllowedKeysTests()
    {
        var repoRoot = FindRepoRoot();
        schemaPath = Path.Combine(repoRoot, "data", "sources", "step-schema", "github", "raw", "github-workflow.schema.json");
        supplementalPath = Path.Combine(repoRoot, "data", "sources", "step-schema", "github", "supplemental-step-schema.json");
    }

    [Test]
    public async Task Merge_RunForm_AllowsShellAndBackground()
    {
        var merged = MergeCommitted();
        var run = merged.Forms.First(static f => f.Id == "run");
        await Assert.That(run.AllowedKeys).Contains("shell");
        await Assert.That(run.AllowedKeys).Contains("background");
    }

    [Test]
    public async Task Merge_UsesForm_DoesNotAllowShellOrRun()
    {
        var merged = MergeCommitted();
        var uses = merged.Forms.First(static f => f.Id == "uses");
        await Assert.That(uses.AllowedKeys).DoesNotContain("shell");
        await Assert.That(uses.AllowedKeys).DoesNotContain("run");
        await Assert.That(uses.AllowedKeys).DoesNotContain("working-directory");
    }

    [Test]
    public async Task Merge_UsesForm_AllowsWithAndBackground()
    {
        var merged = MergeCommitted();
        var uses = merged.Forms.First(static f => f.Id == "uses");
        await Assert.That(uses.AllowedKeys).Contains("with");
        await Assert.That(uses.AllowedKeys).Contains("background");
    }

    [Test]
    public async Task Merge_RunForm_DoesNotAllowWith()
    {
        var merged = MergeCommitted();
        var run = merged.Forms.First(static f => f.Id == "run");
        await Assert.That(run.AllowedKeys).DoesNotContain("with");
    }

    [Test]
    public async Task Merge_WaitForm_AllowsWaitOnlyNotRunOrBackground()
    {
        var merged = MergeCommitted();
        var wait = merged.Forms.First(static f => f.Id == "wait");
        await Assert.That(wait.AllowedKeys).Contains("wait");
        await Assert.That(wait.AllowedKeys).DoesNotContain("run");
        await Assert.That(wait.AllowedKeys).DoesNotContain("background");
        await Assert.That(wait.AllowedKeys).DoesNotContain("parallel");
    }

    [Test]
    public async Task Merge_ParallelForm_DoesNotAllowRunOrUses()
    {
        var merged = MergeCommitted();
        var parallel = merged.Forms.First(static f => f.Id == "parallel");
        await Assert.That(parallel.AllowedKeys).Contains("parallel");
        await Assert.That(parallel.AllowedKeys).DoesNotContain("run");
        await Assert.That(parallel.AllowedKeys).DoesNotContain("uses");
        await Assert.That(parallel.AllowedKeys).DoesNotContain("background");
    }

    [Test]
    public async Task Merge_CancelForm_DoesNotAllowWaitKeys()
    {
        var merged = MergeCommitted();
        var cancel = merged.Forms.First(static f => f.Id == "cancel");
        await Assert.That(cancel.AllowedKeys).Contains("cancel");
        await Assert.That(cancel.AllowedKeys).DoesNotContain("wait");
        await Assert.That(cancel.AllowedKeys).DoesNotContain("wait-all");
    }

    private StepSchemaModel MergeCommitted()
    {
        var parsed = new GitHubWorkflowStepSchemaParser().ParseFile(schemaPath);
        var supplemental = new StepSchemaSourceParser().ParseSupplemental(supplementalPath);
        return merger.Merge(parsed, supplemental);
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
