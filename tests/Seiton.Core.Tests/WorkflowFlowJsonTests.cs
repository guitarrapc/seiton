using System.Text;
using System.Text.Json;
using Seiton.Core.Flow;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class WorkflowFlowJsonTests
{
    private static WorkflowFlow CollectFlow(string yaml, string filePath = "wf.yml")
    {
        using var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml.Replace("\r\n", "\n")), filePath);
        var flow = WorkflowFlowCollector.Collect(result, filePath);
        return flow!;
    }

    [Test]
    public async Task Serialize_MinimalWorkflow_MatchesContractShape()
    {
        var flow = CollectFlow("""
            name: CI
            on: [push]
            jobs:
              build:
                runs-on: ubuntu-latest
                needs: []
                steps:
                  - uses: actions/checkout@v4
                  - run: dotnet build
            """);

        var json = WorkflowFlowJson.Serialize(flow);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("version").GetInt32()).IsEqualTo(1);

        var workflows = root.GetProperty("workflows");
        await Assert.That(workflows.GetArrayLength()).IsEqualTo(1);

        var workflow = workflows[0];
        await Assert.That(workflow.GetProperty("file").GetString()).IsEqualTo("wf.yml");
        await Assert.That(workflow.GetProperty("name").GetString()).IsEqualTo("CI");
        await Assert.That(workflow.GetProperty("on")[0].GetString()).IsEqualTo("push");

        var job = workflow.GetProperty("jobs")[0];
        await Assert.That(job.GetProperty("id").GetString()).IsEqualTo("build");
        await Assert.That(job.GetProperty("kind").GetString()).IsEqualTo("job");
        await Assert.That(job.GetProperty("needs").GetArrayLength()).IsEqualTo(0);
        await Assert.That(job.GetProperty("runsOn")[0].GetString()).IsEqualTo("ubuntu-latest");

        var steps = job.GetProperty("steps");
        await Assert.That(steps.GetArrayLength()).IsEqualTo(2);
        await Assert.That(steps[0].GetProperty("kind").GetString()).IsEqualTo("uses");
        await Assert.That(steps[0].GetProperty("uses").GetString()).IsEqualTo("actions/checkout@v4");
        await Assert.That(steps[1].GetProperty("kind").GetString()).IsEqualTo("run");
        await Assert.That(steps[1].GetProperty("run").GetString()).IsEqualTo("dotnet build");
    }

    [Test]
    public async Task Serialize_AbsentOptionalFields_AreOmitted()
    {
        var flow = CollectFlow("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hi
            """);

        var json = WorkflowFlowJson.Serialize(flow);
        using var doc = JsonDocument.Parse(json);
        var workflow = doc.RootElement.GetProperty("workflows")[0];

        await Assert.That(workflow.TryGetProperty("name", out _)).IsFalse();

        var job = workflow.GetProperty("jobs")[0];
        await Assert.That(job.TryGetProperty("if", out _)).IsFalse();
        await Assert.That(job.TryGetProperty("uses", out _)).IsFalse();
        await Assert.That(job.TryGetProperty("strategy", out _)).IsFalse();

        var step = job.GetProperty("steps")[0];
        await Assert.That(step.TryGetProperty("id", out _)).IsFalse();
        await Assert.That(step.TryGetProperty("name", out _)).IsFalse();
        await Assert.That(step.TryGetProperty("if", out _)).IsFalse();
        await Assert.That(step.TryGetProperty("uses", out _)).IsFalse();
        await Assert.That(step.TryGetProperty("steps", out _)).IsFalse();
    }

    [Test]
    public async Task Serialize_ParallelBoundary_NestsChildSteps()
    {
        var flow = CollectFlow("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - parallel:
                    - run: npm run a
                    - run: npm run b
            """);

        var json = WorkflowFlowJson.Serialize(flow);
        using var doc = JsonDocument.Parse(json);
        var step = doc.RootElement.GetProperty("workflows")[0].GetProperty("jobs")[0].GetProperty("steps")[0];

        await Assert.That(step.GetProperty("kind").GetString()).IsEqualTo("parallel");
        var children = step.GetProperty("steps");
        await Assert.That(children.GetArrayLength()).IsEqualTo(2);
        await Assert.That(children[0].GetProperty("run").GetString()).IsEqualTo("npm run a");
    }

    [Test]
    public async Task Serialize_ReusableJobAndStrategy_EmittedPerContract()
    {
        var flow = CollectFlow("""
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                if: github.event_name == 'push'
                strategy:
                  matrix:
                    os: [ubuntu-latest, windows-latest]
                steps:
                  - run: npm test
              call:
                needs: test
                uses: octo/repo/.github/workflows/deploy.yml@v1
            """);

        var json = WorkflowFlowJson.Serialize(flow);
        using var doc = JsonDocument.Parse(json);
        var jobs = doc.RootElement.GetProperty("workflows")[0].GetProperty("jobs");

        var test = jobs[0];
        await Assert.That(test.GetProperty("if").GetString()).IsEqualTo("github.event_name == 'push'");
        var strategy = test.GetProperty("strategy");
        await Assert.That(strategy.GetProperty("hasMatrix").GetBoolean()).IsTrue();
        await Assert.That(strategy.GetProperty("matrixKeys")[0].GetString()).IsEqualTo("os");
        await Assert.That(strategy.GetProperty("matrixIsExpression").GetBoolean()).IsFalse();

        var call = jobs[1];
        await Assert.That(call.GetProperty("kind").GetString()).IsEqualTo("reusable");
        await Assert.That(call.GetProperty("uses").GetString()).IsEqualTo("octo/repo/.github/workflows/deploy.yml@v1");
        await Assert.That(call.GetProperty("needs")[0].GetString()).IsEqualTo("test");
        await Assert.That(call.GetProperty("steps").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Serialize_BackgroundStepAndMatrixCombinations_EmittedPerContract()
    {
        var flow = CollectFlow("""
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: [ubuntu, windows]
                steps:
                  - id: server
                    run: npm run serve
                    background: true
                  - run: npm test
            """);

        var json = WorkflowFlowJson.Serialize(flow);
        using var doc = JsonDocument.Parse(json);
        var job = doc.RootElement.GetProperty("workflows")[0].GetProperty("jobs")[0];

        var combos = job.GetProperty("strategy").GetProperty("combinations");
        await Assert.That(combos.GetArrayLength()).IsEqualTo(2);
        await Assert.That(combos[0].GetProperty("os").GetString()).IsEqualTo("ubuntu");
        await Assert.That(combos[1].GetProperty("os").GetString()).IsEqualTo("windows");

        var steps = job.GetProperty("steps");
        await Assert.That(steps[0].GetProperty("background").GetBoolean()).IsTrue();
        // background is omitted when false.
        await Assert.That(steps[1].TryGetProperty("background", out _)).IsFalse();

        // Source line ranges for diagnostics mapping.
        await Assert.That(job.GetProperty("line").GetInt32()).IsGreaterThan(0);
        await Assert.That(job.GetProperty("endLine").GetInt32()).IsGreaterThanOrEqualTo(job.GetProperty("line").GetInt32());
        await Assert.That(steps[0].GetProperty("line").GetInt32()).IsGreaterThan(0);
        await Assert.That(steps[0].GetProperty("endLine").GetInt32()).IsGreaterThanOrEqualTo(steps[0].GetProperty("line").GetInt32());
    }

    [Test]
    public async Task Serialize_RuntimeSettings_EmittedPerContract()
    {
        var flow = CollectFlow("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                timeout-minutes: 15
                permissions:
                  contents: read
                environment: production
                steps:
                  - run: echo build
                    timeout-minutes: 5
                    continue-on-error: true
                  - run: echo plain
            """);

        var json = WorkflowFlowJson.Serialize(flow);
        using var doc = JsonDocument.Parse(json);
        var job = doc.RootElement.GetProperty("workflows")[0].GetProperty("jobs")[0];

        await Assert.That(job.GetProperty("timeoutMinutes").GetDouble()).IsEqualTo(15d);
        await Assert.That(job.GetProperty("permissions")[0].GetString()).IsEqualTo("contents: read");
        await Assert.That(job.GetProperty("environment").GetString()).IsEqualTo("production");

        var steps = job.GetProperty("steps");
        await Assert.That(steps[0].GetProperty("timeoutMinutes").GetDouble()).IsEqualTo(5d);
        await Assert.That(steps[0].GetProperty("continueOnError").GetBoolean()).IsTrue();
        // Absent settings are omitted.
        await Assert.That(steps[1].TryGetProperty("timeoutMinutes", out _)).IsFalse();
        await Assert.That(steps[1].TryGetProperty("continueOnError", out _)).IsFalse();
    }

    [Test]
    public async Task Serialize_MultipleWorkflows_WrappedInSingleDocument()
    {
        var flowA = CollectFlow("""
            on: push
            jobs:
              a:
                runs-on: ubuntu-latest
                steps:
                  - run: echo a
            """, "a.yml");
        var flowB = CollectFlow("""
            on: push
            jobs:
              b:
                runs-on: ubuntu-latest
                steps:
                  - run: echo b
            """, "b.yml");

        var json = WorkflowFlowJson.Serialize([flowA, flowB]);
        using var doc = JsonDocument.Parse(json);
        var workflows = doc.RootElement.GetProperty("workflows");

        await Assert.That(workflows.GetArrayLength()).IsEqualTo(2);
        await Assert.That(workflows[0].GetProperty("file").GetString()).IsEqualTo("a.yml");
        await Assert.That(workflows[1].GetProperty("file").GetString()).IsEqualTo("b.yml");
    }
}
