using Seiton.Update.Parsers;

namespace Seiton.Update.Tests;

public sealed class GitHubWorkflowStepSchemaParserTests
{
    [Test]
    public async Task Parse_CommittedSchema_ExtractsRunAndUsesForms()
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

        var parser = new GitHubWorkflowStepSchemaParser();
        var model = parser.ParseFile(schemaPath);

        await Assert.That(model.Forms).Count().IsEqualTo(6);
        await Assert.That(model.Forms.Select(static f => f.Id)).Contains("run");
        await Assert.That(model.Forms.Select(static f => f.Id)).Contains("uses");
        await Assert.That(model.Forms.Select(static f => f.Id)).Contains("wait");
        await Assert.That(model.Forms.Select(static f => f.Id)).Contains("parallel");
    }

    [Test]
    public async Task Parse_CommittedSchema_InfersSharedPropertyKinds()
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

        var parser = new GitHubWorkflowStepSchemaParser();
        var model = parser.ParseFile(schemaPath);

        await Assert.That(model.Properties["continue-on-error"].ValueKind).IsEqualTo("boolOrExpression");
        await Assert.That(model.Properties["timeout-minutes"].ValueKind).IsEqualTo("floatOrExpression");
        await Assert.That(model.Properties["env"].ValueKind).IsEqualTo("envMapping");
        await Assert.That(model.Properties["if"].ValueKind).IsEqualTo("stepIf");
    }

    [Test]
    public async Task Parse_CommittedSchema_ExtractsRunDependencies()
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

        var parser = new GitHubWorkflowStepSchemaParser();
        var model = parser.ParseFile(schemaPath);

        var shell = model.KeyDependencies.FirstOrDefault(static d => d.Key == "shell");
        await Assert.That(shell).IsNotNull();
        await Assert.That(shell!.RequiresPrimary).IsEqualTo("run");
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
