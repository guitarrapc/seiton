using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

/// <summary>
/// Tests for the readonly-struct Ref facade layer (WorkflowRef / JobRef / StepRef / ...)
/// that rules and tests use to read the AST without touching AstArena directly.
/// </summary>
public class AstRefTests
{
    private static readonly string WorkflowYaml = """
        name: ref-test
        on:
          push:
            branches: [main, 'release/**']
          schedule:
            - cron: '0 0 * * *'
        permissions:
          contents: read
        env:
          GLOBAL: value
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - name: Run
                if: ${{ success() }}
                run: echo hello
              - uses: actions/checkout@v4
                with:
                  fetch-depth: '0'
          deploy:
            needs: [build]
            runs-on: ubuntu-latest
            steps:
              - run: echo deploy
        """;

    private static ParseResult Parse(string yaml)
        => WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml.Replace("\r\n", "\n")), "test.yml");

    [Test]
    public async Task Workflow_ReturnsRefWithScalars()
    {
        using var result = Parse(WorkflowYaml);
        var workflow = result.Workflow;

        await Assert.That(workflow.HasValue).IsTrue();
        await Assert.That(workflow.Name.HasValue).IsTrue();
        await Assert.That(workflow.Name.Decode()).IsEqualTo("ref-test");
        await Assert.That(workflow.Name.ValueEquals("ref-test"u8)).IsTrue();
        await Assert.That(workflow.RunName.HasValue).IsFalse();
        await Assert.That(workflow.Range.StartLine).IsEqualTo(1);
    }

    [Test]
    public async Task Jobs_MapAccessAndEnumeration()
    {
        using var result = Parse(WorkflowYaml);
        var jobs = result.Workflow.Jobs;

        await Assert.That(jobs.Count).IsEqualTo(2);
        await Assert.That(jobs.TryGetValue("build"u8, out var build)).IsTrue();
        await Assert.That(build.Id.Decode()).IsEqualTo("build");
        await Assert.That(jobs.TryGetValue("missing"u8, out _)).IsFalse();

        var keys = new List<string>();
        foreach (var entry in jobs)
        {
            keys.Add(entry.Key.Decode());
            await Assert.That(entry.Value.HasValue).IsTrue();
        }

        await Assert.That(keys).IsEquivalentTo(["build", "deploy"]);
    }

    [Test]
    public async Task Job_NeedsAndRunsOn()
    {
        using var result = Parse(WorkflowYaml);
        result.Workflow.Jobs.TryGetValue("deploy"u8, out var deploy);

        await Assert.That(deploy.Needs.Count).IsEqualTo(1);
        await Assert.That(deploy.Needs[0].Decode()).IsEqualTo("build");
        await Assert.That(deploy.RunsOn.HasValue).IsTrue();
        await Assert.That(deploy.RunsOn.Labels.Count).IsEqualTo(1);
        await Assert.That(deploy.RunsOn.Labels[0].ValueEquals("ubuntu-latest"u8)).IsTrue();

        // build has no needs — default list is empty and safe
        result.Workflow.Jobs.TryGetValue("build"u8, out var build);
        await Assert.That(build.Needs.Count).IsEqualTo(0);
        foreach (var _ in build.Needs)
        {
            Assert.Fail("empty list must not enumerate");
        }
    }

    [Test]
    public async Task Steps_ExecKindDispatch()
    {
        using var result = Parse(WorkflowYaml);
        result.Workflow.Jobs.TryGetValue("build"u8, out var build);

        await Assert.That(build.Steps.Count).IsEqualTo(2);

        var run = build.Steps[0];
        await Assert.That(run.Name.Decode()).IsEqualTo("Run");
        await Assert.That(run.If.HasValue).IsTrue();
        await Assert.That(run.Exec.Kind).IsEqualTo(StepExecKind.Run);
        await Assert.That(run.Exec.AsRun().Run.Decode()).IsEqualTo("echo hello");

        var uses = build.Steps[1];
        await Assert.That(uses.Exec.Kind).IsEqualTo(StepExecKind.Action);
        var action = uses.Exec.AsAction();
        await Assert.That(action.Uses.Decode()).IsEqualTo("actions/checkout@v4");
        await Assert.That(action.Inputs.HasValue).IsTrue();
        await Assert.That(action.Inputs.TryGetValue("fetch-depth"u8, out var depth)).IsTrue();
        await Assert.That(depth.Decode()).IsEqualTo("0");
    }

    [Test]
    public async Task Events_KindDispatch()
    {
        using var result = Parse(WorkflowYaml);
        var on = result.Workflow.On;

        await Assert.That(on.Count).IsEqualTo(2);
        await Assert.That(on[0].Kind).IsEqualTo(EventKind.Webhook);
        var webhook = on[0].AsWebhook();
        await Assert.That(webhook.Hook.Decode()).IsEqualTo("push");
        await Assert.That(webhook.Branches.HasValue).IsTrue();
        await Assert.That(webhook.Branches.Values.Count).IsEqualTo(2);

        await Assert.That(on[1].Kind).IsEqualTo(EventKind.Scheduled);
        var schedule = on[1].AsScheduled();
        await Assert.That(schedule.Schedules.Count).IsEqualTo(1);
        await Assert.That(schedule.Schedules[0].Cron.Decode()).IsEqualTo("0 0 * * *");
    }

    [Test]
    public async Task Sections_PermissionsAndEnv()
    {
        using var result = Parse(WorkflowYaml);
        var workflow = result.Workflow;

        await Assert.That(workflow.Permissions.HasValue).IsTrue();
        await Assert.That(workflow.Permissions.Scopes.HasValue).IsTrue();
        await Assert.That(workflow.Permissions.Scopes.Count).IsEqualTo(1);

        await Assert.That(workflow.Env.HasValue).IsTrue();
        await Assert.That(workflow.Env.Vars.HasValue).IsTrue();
        await Assert.That(workflow.Env.Vars.TryGetValue("GLOBAL"u8, out var envVar)).IsTrue();
        await Assert.That(envVar.Value.Decode()).IsEqualTo("value");

        // Absent sections are default refs, safe to chain
        await Assert.That(workflow.Defaults.HasValue).IsFalse();
        await Assert.That(workflow.Defaults.Run.Shell.HasValue).IsFalse();
        await Assert.That(workflow.Concurrency.HasValue).IsFalse();
    }

    [Test]
    public async Task DefaultRefs_AreSafeEverywhere()
    {
        var job = default(JobRef);
        await Assert.That(job.HasValue).IsFalse();
        await Assert.That(job.Steps.Count).IsEqualTo(0);
        await Assert.That(job.Name.HasValue).IsFalse();
        await Assert.That(job.Name.Decode()).IsEqualTo(string.Empty);
        await Assert.That(job.Name.Value.Length).IsEqualTo(0);

        var step = default(StepRef);
        await Assert.That(step.HasValue).IsFalse();
        await Assert.That(step.Exec.HasValue).IsFalse();

        var map = default(StringRefMap);
        await Assert.That(map.HasValue).IsFalse();
        await Assert.That(map.Count).IsEqualTo(0);
        await Assert.That(map.TryGetValue("x"u8, out _)).IsFalse();
    }

    [Test]
    public async Task StepRef_EqualityIsStableWithinParse()
    {
        using var result = Parse(WorkflowYaml);
        result.Workflow.Jobs.TryGetValue("build"u8, out var build);

        var a = build.Steps[0];
        var b = build.Steps[0];
        var c = build.Steps[1];

        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
        await Assert.That(a.Equals(c)).IsFalse();

        // Usable as dictionary key (identity within one parse)
        var dict = new Dictionary<StepRef, int> { [a] = 1, [c] = 2 };
        await Assert.That(dict[b]).IsEqualTo(1);
    }

    [Test]
    public async Task ParallelStep_ExposesChildSteps()
    {
        using var result = Parse("""
            name: p
            on: push
            jobs:
              j:
                runs-on: ubuntu-latest
                steps:
                  - parallel:
                      - run: echo a
                      - run: echo b
            """);
        result.Workflow.Jobs.TryGetValue("j"u8, out var job);

        var parallel = job.Steps[0].Exec;
        await Assert.That(parallel.Kind).IsEqualTo(StepExecKind.Parallel);
        await Assert.That(parallel.AsParallel().Steps.Count).IsEqualTo(2);
        await Assert.That(parallel.AsParallel().Steps[1].Exec.AsRun().Run.Decode()).IsEqualTo("echo b");
    }

    [Test]
    public async Task Matrix_RawYamlKindDispatch()
    {
        using var result = Parse("""
            name: m
            on: push
            jobs:
              j:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: [ubuntu-latest, windows-latest]
                    include:
                      - os: macos-latest
                steps:
                  - run: echo ${{ matrix.os }}
            """);
        result.Workflow.Jobs.TryGetValue("j"u8, out var job);

        var matrix = job.Strategy.Matrix;
        await Assert.That(matrix.HasValue).IsTrue();
        await Assert.That(matrix.Rows.HasValue).IsTrue();
        await Assert.That(matrix.Rows.TryGetValue("os"u8, out var osRow)).IsTrue();
        await Assert.That(osRow.Values.Count).IsEqualTo(2);
        await Assert.That(osRow.Values[0].Kind).IsEqualTo(RawYamlKind.String);
        await Assert.That(osRow.Values[0].Scalar.Decode()).IsEqualTo("ubuntu-latest");

        await Assert.That(matrix.Include.Count).IsEqualTo(1);
        var entry = matrix.Include[0].Entries[0];
        await Assert.That(entry.TryGetValue("os"u8, out var os)).IsTrue();
        await Assert.That(os.Kind).IsEqualTo(RawYamlKind.String);
    }

    [Test]
    public async Task ActionMetadata_RefAccess()
    {
        using var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes("""
            name: my-action
            description: does things
            inputs:
              token:
                description: a token
                required: true
            runs:
              using: composite
              steps:
                - run: echo hi
                  shell: bash
            """.Replace("\r\n", "\n")), "action.yml");

        var metadata = result.ActionMetadata;
        await Assert.That(metadata.HasValue).IsTrue();
        await Assert.That(metadata.Name.Decode()).IsEqualTo("my-action");
        await Assert.That(metadata.Inputs.HasValue).IsTrue();
        await Assert.That(metadata.Inputs.TryGetValue("token"u8, out var token)).IsTrue();
        await Assert.That(token.Required.HasValue).IsTrue();
        await Assert.That(token.Required.Value).IsTrue();
        await Assert.That(metadata.Runs.HasValue).IsTrue();
        await Assert.That(metadata.Runs.Steps.Count).IsEqualTo(1);
    }
}
