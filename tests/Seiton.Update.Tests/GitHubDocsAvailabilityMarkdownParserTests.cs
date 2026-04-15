using Seiton.Update.Parsers;

namespace Seiton.Update.Tests;

public sealed class GitHubDocsAvailabilityMarkdownParserTests
{
    [Test]
    public async Task ParseWorkflowKeyContexts_ValidTable_ParsesRows()
    {
        var markdown = """
            ## Available contexts

            ### Context availability

            | Workflow key | Context | Special functions |
            | ---- | ------- | ----------------- |
            | `run-name` | `github, inputs, vars` | None |
            | `jobs.<job_id>.concurrency` | `github, needs, strategy, matrix, inputs, vars` | None |
            | `jobs.<job_id>.steps.run` | `github, needs, strategy, matrix, job, runner, env, vars, secrets, steps, inputs` | `hashFiles` |

            ### Example section
            """;

        var parser = new GitHubDocsAvailabilityMarkdownParser();
        var map = parser.ParseWorkflowKeyContexts(markdown);

        await Assert.That(map.ContainsKey("run-name")).IsTrue();
        await Assert.That(map["run-name"]).Contains("github");
        await Assert.That(map["run-name"]).Contains("inputs");
        await Assert.That(map["run-name"]).Contains("vars");

        await Assert.That(map.ContainsKey("jobs.<job_id>.concurrency")).IsTrue();
        await Assert.That(map["jobs.<job_id>.concurrency"]).Contains("needs");
        await Assert.That(map["jobs.<job_id>.concurrency"]).Contains("strategy");

        await Assert.That(map.ContainsKey("jobs.<job_id>.steps.run")).IsTrue();
        await Assert.That(map["jobs.<job_id>.steps.run"]).Contains("secrets");
        await Assert.That(map["jobs.<job_id>.steps.run"]).Contains("steps");
    }

    [Test]
    public async Task ParseWorkflowKeyContexts_NoContextAvailabilitySection_ReturnsEmpty()
    {
        var parser = new GitHubDocsAvailabilityMarkdownParser();
        var map = parser.ParseWorkflowKeyContexts("# no table");

        await Assert.That(map.Count).IsEqualTo(0);
    }
}
