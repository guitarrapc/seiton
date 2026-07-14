using System.Text.Json;

namespace Seiton.Playground.Tests;

[NotInParallel(PlaygroundTestParallelism.AssemblyLockKey)]
public sealed class PlaygroundFlowRunnerTests
{
    [Test]
    public async Task RunFlowToJson_Workflow_ReturnsFlowDocument()
    {
        const string yaml = """
            name: CI
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - parallel:
                    - run: npm run a
                    - run: npm run b
              deploy:
                runs-on: ubuntu-latest
                needs: build
                steps:
                  - run: echo deploy
            """;

        var json = PlaygroundFlowRunner.RunFlowToJsonUtf8(yaml, ".github/workflows/ci.yml");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("version").GetInt32()).IsEqualTo(1);
        var workflow = root.GetProperty("workflows")[0];
        await Assert.That(workflow.GetProperty("name").GetString()).IsEqualTo("CI");

        var jobs = workflow.GetProperty("jobs");
        await Assert.That(jobs.GetArrayLength()).IsEqualTo(2);
        await Assert.That(jobs[0].GetProperty("id").GetString()).IsEqualTo("build");
        await Assert.That(jobs[0].GetProperty("steps")[1].GetProperty("kind").GetString()).IsEqualTo("parallel");
        await Assert.That(jobs[0].GetProperty("steps")[1].GetProperty("steps").GetArrayLength()).IsEqualTo(2);
        await Assert.That(jobs[1].GetProperty("needs")[0].GetString()).IsEqualTo("build");
    }

    [Test]
    public async Task RunFlowToJson_ActionMetadata_ReturnsEmptyWorkflows()
    {
        const string yaml = """
            name: My Action
            description: does things
            runs:
              using: composite
              steps:
                - run: echo hi
                  shell: bash
            """;

        var json = PlaygroundFlowRunner.RunFlowToJsonUtf8(yaml, "action.yml");
        using var doc = JsonDocument.Parse(json);

        await Assert.That(doc.RootElement.GetProperty("workflows").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task RunFlowToJson_IdenticalReferenceInput_ReturnsCachedOutput()
    {
        const string yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """;

        var first = PlaygroundFlowRunner.RunFlowToJsonUtf8(yaml, ".github/workflows/ci.yml");
        var second = PlaygroundFlowRunner.RunFlowToJsonUtf8(yaml, ".github/workflows/ci.yml");

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task RunFlowToMermaid_Workflow_ReturnsFlowchart()
    {
        const string yaml = """
            name: CI
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - run: npm test
              deploy:
                runs-on: ubuntu-latest
                needs: build
                steps:
                  - run: echo deploy
            """;

        var mermaid = System.Text.Encoding.UTF8.GetString(
            PlaygroundFlowRunner.RunFlowToMermaidUtf8(yaml, ".github/workflows/ci.yml"));

        await Assert.That(mermaid).Contains("flowchart LR");
        await Assert.That(mermaid).Contains("subgraph j0[\"build\"]");
        await Assert.That(mermaid).Contains("j0 --> j1");
    }

    [Test]
    public async Task RunFlowToMermaid_ActionMetadata_ReturnsMinimalDiagram()
    {
        const string yaml = """
            name: My Action
            description: does things
            runs:
              using: composite
              steps:
                - run: echo hi
                  shell: bash
            """;

        var mermaid = System.Text.Encoding.UTF8.GetString(
            PlaygroundFlowRunner.RunFlowToMermaidUtf8(yaml, "action.yml"));

        await Assert.That(mermaid).Contains("flowchart LR");
        await Assert.That(mermaid.Contains("subgraph j0", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task RunFlowToJson_RepeatedCalls_ProducesConsistentOutput()
    {
        const string yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """;

        byte[]? firstJson = null;
        for (var i = 0; i < 10; i++)
        {
            // New string instance each iteration defeats the identity cache on purpose.
            var json = PlaygroundFlowRunner.RunFlowToJsonUtf8(new string(yaml.AsSpan()), ".github/workflows/ci.yml");
            firstJson ??= json;
            await Assert.That(json).IsEquivalentTo(firstJson);
        }
    }
}
