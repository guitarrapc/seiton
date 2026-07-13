using System.Text;
using Seiton.Core.Flow;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class WorkflowFlowCollectorTests
{
    private static ParseResult Parse(string yaml, string filePath = "wf.yml")
        => WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml.Replace("\r\n", "\n")), filePath);

    [Test]
    public async Task Collect_MinimalWorkflow_CapturesMetadataJobsAndSteps()
    {
        using var result = Parse("""
            name: CI
            on: [push, pull_request]
            jobs:
              build:
                name: Build
                runs-on: ubuntu-latest
                steps:
                  - name: Checkout
                    uses: actions/checkout@v4
                  - id: compile
                    run: dotnet build
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        await Assert.That(flow).IsNotNull();
        await Assert.That(flow!.File).IsEqualTo("wf.yml");
        await Assert.That(flow.Name).IsEqualTo("CI");
        await Assert.That(flow.On.SequenceEqual(["push", "pull_request"])).IsTrue();

        await Assert.That(flow.Jobs).Count().IsEqualTo(1);
        var job = flow.Jobs[0];
        await Assert.That(job.Id).IsEqualTo("build");
        await Assert.That(job.Name).IsEqualTo("Build");
        await Assert.That(job.Kind).IsEqualTo(FlowJobKind.Job);
        await Assert.That(job.RunsOn.SequenceEqual(["ubuntu-latest"])).IsTrue();

        await Assert.That(job.Steps).Count().IsEqualTo(2);
        await Assert.That(job.Steps[0].Kind).IsEqualTo(FlowStepKind.Uses);
        await Assert.That(job.Steps[0].Name).IsEqualTo("Checkout");
        await Assert.That(job.Steps[0].Uses).IsEqualTo("actions/checkout@v4");
        await Assert.That(job.Steps[1].Kind).IsEqualTo(FlowStepKind.Run);
        await Assert.That(job.Steps[1].Id).IsEqualTo("compile");
        await Assert.That(job.Steps[1].Run).IsEqualTo("dotnet build");
    }

    [Test]
    public async Task Collect_NeedsEdges_ResolvedAsJobIds()
    {
        using var result = Parse("""
            on: push
            jobs:
              prep:
                runs-on: ubuntu-latest
                steps:
                  - run: echo prep
              build:
                runs-on: ubuntu-latest
                needs: prep
                steps:
                  - run: echo build
              deploy:
                runs-on: ubuntu-latest
                needs: [prep, build]
                steps:
                  - run: echo deploy
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        await Assert.That(flow!.Jobs).Count().IsEqualTo(3);
        await Assert.That(flow.Jobs[0].Needs).IsEmpty();
        await Assert.That(flow.Jobs[1].Needs.SequenceEqual(["prep"])).IsTrue();
        await Assert.That(flow.Jobs[2].Needs.SequenceEqual(["prep", "build"])).IsTrue();
    }

    [Test]
    public async Task Collect_JobAndStepIfConditions_KeepRawExpressions()
    {
        using var result = Parse("""
            on: push
            jobs:
              deploy:
                runs-on: ubuntu-latest
                if: github.ref == 'refs/heads/main'
                steps:
                  - run: echo deploy
                    if: ${{ success() }}
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var job = flow!.Jobs[0];
        await Assert.That(job.If).IsEqualTo("github.ref == 'refs/heads/main'");
        await Assert.That(job.Steps[0].If).IsEqualTo("${{ success() }}");
    }

    [Test]
    public async Task Collect_ParallelStep_PreservesBoundary()
    {
        // Nested `parallel` inside `parallel` is rejected by the parser
        // (see ParserTests.ParallelSteps), so boundaries are single-level.
        using var result = Parse("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo before
                  - parallel:
                    - run: npm run build-app1
                    - run: npm run build-app2
                    - uses: actions/upload-artifact@v4
                  - run: echo after
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var steps = flow!.Jobs[0].Steps;
        await Assert.That(steps).Count().IsEqualTo(3);
        await Assert.That(steps[0].Kind).IsEqualTo(FlowStepKind.Run);
        await Assert.That(steps[2].Kind).IsEqualTo(FlowStepKind.Run);

        var parallel = steps[1];
        await Assert.That(parallel.Kind).IsEqualTo(FlowStepKind.Parallel);
        await Assert.That(parallel.Steps).Count().IsEqualTo(3);
        await Assert.That(parallel.Steps[0].Kind).IsEqualTo(FlowStepKind.Run);
        await Assert.That(parallel.Steps[0].Run).IsEqualTo("npm run build-app1");
        await Assert.That(parallel.Steps[2].Kind).IsEqualTo(FlowStepKind.Uses);
        await Assert.That(parallel.Steps[2].Uses).IsEqualTo("actions/upload-artifact@v4");
    }

    [Test]
    public async Task Collect_WaitAndCancelSteps_KeepTargets()
    {
        using var result = Parse("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - id: build-frontend
                    run: npm run build-frontend
                    background: true
                  - id: build-backend
                    run: npm run build-backend
                    background: true
                  - wait: [build-frontend, build-backend]
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var steps = flow!.Jobs[0].Steps;
        await Assert.That(steps).Count().IsEqualTo(3);
        await Assert.That(steps[2].Kind).IsEqualTo(FlowStepKind.Wait);
        await Assert.That(steps[2].WaitTargets.SequenceEqual(["build-frontend", "build-backend"])).IsTrue();
    }

    [Test]
    public async Task Collect_JobsAndSteps_CarrySourceLineRanges()
    {
        using var result = Parse("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo a
                  - run: echo b
              deploy:
                runs-on: ubuntu-latest
                needs: build
                steps:
                  - run: echo deploy
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var build = flow!.Jobs[0];
        // The job range must cover its steps so diagnostics can be mapped by line.
        await Assert.That(build.Line).IsGreaterThan(0);
        await Assert.That(build.Line).IsLessThanOrEqualTo(6);
        await Assert.That(build.EndLine).IsGreaterThanOrEqualTo(7);

        await Assert.That(build.Steps[0].Line).IsEqualTo(6);
        await Assert.That(build.Steps[1].Line).IsEqualTo(7);

        var deploy = flow.Jobs[1];
        await Assert.That(deploy.Line).IsGreaterThanOrEqualTo(8);
        await Assert.That(deploy.Steps[0].Line).IsEqualTo(12);
    }

    [Test]
    public async Task Collect_JobRuntimeSettings_CapturesTimeoutPermissionsAndEnvironment()
    {
        using var result = Parse("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                timeout-minutes: 15
                permissions:
                  contents: read
                  id-token: write
                environment: production
                steps:
                  - run: echo build
                    timeout-minutes: 5
                  - run: echo cleanup
                    continue-on-error: true
              scan:
                runs-on: ubuntu-latest
                permissions: read-all
                steps:
                  - run: echo scan
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var build = flow!.Jobs[0];
        await Assert.That(build.TimeoutMinutes).IsEqualTo(15d);
        await Assert.That(build.Permissions!.SequenceEqual(["contents: read", "id-token: write"])).IsTrue();
        await Assert.That(build.Environment).IsEqualTo("production");
        await Assert.That(build.Steps[0].TimeoutMinutes).IsEqualTo(5d);
        await Assert.That(build.Steps[0].ContinueOnError).IsFalse();
        await Assert.That(build.Steps[1].TimeoutMinutes).IsNull();
        await Assert.That(build.Steps[1].ContinueOnError).IsTrue();

        var scan = flow.Jobs[1];
        await Assert.That(scan.Permissions!.SequenceEqual(["read-all"])).IsTrue();
        await Assert.That(scan.TimeoutMinutes).IsNull();
        await Assert.That(scan.Environment).IsNull();
    }

    [Test]
    public async Task Collect_JobWithoutRuntimeSettings_HasNullPermissions()
    {
        using var result = Parse("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hi
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        await Assert.That(flow!.Jobs[0].Permissions).IsNull();
        await Assert.That(flow.Jobs[0].TimeoutMinutes).IsNull();
    }

    [Test]
    public async Task Collect_BackgroundStep_SetsBackgroundFlag()
    {
        using var result = Parse("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - id: server
                    run: npm run serve
                    background: true
                  - run: npm test
                  - wait: [server]
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var steps = flow!.Jobs[0].Steps;
        await Assert.That(steps[0].Background).IsTrue();
        await Assert.That(steps[1].Background).IsFalse();
        await Assert.That(steps[2].Background).IsFalse();
    }

    [Test]
    public async Task Collect_StaticMatrix_ExpandsCrossProductInDeclarationOrder()
    {
        using var result = Parse("""
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: [ubuntu, windows]
                    node: [18, 20]
                steps:
                  - run: npm test
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var combos = flow!.Jobs[0].Strategy!.Combinations;
        await Assert.That(combos).Count().IsEqualTo(4);
        await Assert.That(Render(combos[0])).IsEqualTo("os=ubuntu,node=18");
        await Assert.That(Render(combos[1])).IsEqualTo("os=ubuntu,node=20");
        await Assert.That(Render(combos[2])).IsEqualTo("os=windows,node=18");
        await Assert.That(Render(combos[3])).IsEqualTo("os=windows,node=20");
    }

    [Test]
    public async Task Collect_MatrixExclude_RemovesSubsetMatches()
    {
        using var result = Parse("""
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: [ubuntu, windows]
                    node: [18, 20]
                    exclude:
                      - os: windows
                        node: 18
                      - os: macos
                steps:
                  - run: npm test
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var combos = flow!.Jobs[0].Strategy!.Combinations;
        // "os: macos" matches nothing and must not remove anything.
        await Assert.That(combos).Count().IsEqualTo(3);
        await Assert.That(combos.Any(c => Render(c) == "os=windows,node=18")).IsFalse();
        await Assert.That(combos.Any(c => Render(c) == "os=windows,node=20")).IsTrue();
    }

    [Test]
    public async Task Collect_MatrixInclude_ExtendsMatchingAndAppendsNonMatching()
    {
        using var result = Parse("""
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: [ubuntu, windows]
                    include:
                      - os: ubuntu
                        experimental: true
                      - os: macos
                steps:
                  - run: npm test
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var combos = flow!.Jobs[0].Strategy!.Combinations;
        await Assert.That(combos).Count().IsEqualTo(3);
        await Assert.That(combos.Any(c => Render(c) == "os=ubuntu,experimental=true")).IsTrue();
        await Assert.That(combos.Any(c => Render(c) == "os=windows")).IsTrue();
        await Assert.That(combos.Any(c => Render(c) == "os=macos")).IsTrue();
    }

    [Test]
    public async Task Collect_MatrixIncludeOnly_EachEntryBecomesCombination()
    {
        using var result = Parse("""
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    include:
                      - os: ubuntu
                        node: 18
                      - os: windows
                        node: 20
                steps:
                  - run: npm test
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var combos = flow!.Jobs[0].Strategy!.Combinations;
        await Assert.That(combos).Count().IsEqualTo(2);
        await Assert.That(Render(combos[0])).IsEqualTo("os=ubuntu,node=18");
        await Assert.That(Render(combos[1])).IsEqualTo("os=windows,node=20");
    }

    [Test]
    public async Task Collect_MatrixWithDynamicRow_IsNotExpanded()
    {
        using var result = Parse("""
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: ${{ fromJSON(needs.prep.outputs.os) }}
                    node: [18, 20]
                steps:
                  - run: npm test
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var strategy = flow!.Jobs[0].Strategy!;
        await Assert.That(strategy.HasMatrix).IsTrue();
        await Assert.That(strategy.Combinations).IsEmpty();
    }

    private static string Render(KeyValuePair<string, string>[] combination)
        => string.Join(",", combination.Select(p => $"{p.Key}={p.Value}"));

    [Test]
    public async Task Collect_ReusableWorkflowJob_OpaqueLeafWithUses()
    {
        using var result = Parse("""
            on: push
            jobs:
              call-deploy:
                uses: octo-org/repo/.github/workflows/deploy.yml@v1
                with:
                  environment: production
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var job = flow!.Jobs[0];
        await Assert.That(job.Kind).IsEqualTo(FlowJobKind.Reusable);
        await Assert.That(job.Uses).IsEqualTo("octo-org/repo/.github/workflows/deploy.yml@v1");
        await Assert.That(job.Steps).IsEmpty();
    }

    [Test]
    public async Task Collect_MatrixStrategy_DeclarationOnly()
    {
        using var result = Parse("""
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: [ubuntu-latest, windows-latest]
                    node: [18, 20]
                steps:
                  - run: npm test
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var strategy = flow!.Jobs[0].Strategy;
        await Assert.That(strategy).IsNotNull();
        await Assert.That(strategy!.HasMatrix).IsTrue();
        await Assert.That(strategy.MatrixKeys.SequenceEqual(["os", "node"])).IsTrue();
        await Assert.That(strategy.MatrixIsExpression).IsFalse();
    }

    [Test]
    public async Task Collect_MatrixExpression_MarkedAsExpression()
    {
        using var result = Parse("""
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                strategy:
                  matrix: ${{ fromJSON(needs.prepare.outputs.matrix) }}
                steps:
                  - run: npm test
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        var strategy = flow!.Jobs[0].Strategy;
        await Assert.That(strategy).IsNotNull();
        await Assert.That(strategy!.HasMatrix).IsTrue();
        await Assert.That(strategy.MatrixIsExpression).IsTrue();
        await Assert.That(strategy.MatrixKeys).IsEmpty();
    }

    [Test]
    public async Task Collect_JobWithoutStrategy_HasNullStrategy()
    {
        using var result = Parse("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hi
            """);

        var flow = WorkflowFlowCollector.Collect(result, "wf.yml");

        await Assert.That(flow!.Jobs[0].Strategy).IsNull();
        await Assert.That(flow.Jobs[0].If).IsNull();
        await Assert.That(flow.Jobs[0].Uses).IsNull();
    }

    [Test]
    public async Task Collect_ActionMetadataDocument_ReturnsNull()
    {
        using var result = Parse("""
            name: My Action
            description: does things
            runs:
              using: composite
              steps:
                - run: echo hi
                  shell: bash
            """, "action.yml");

        var flow = WorkflowFlowCollector.Collect(result, "action.yml");

        await Assert.That(flow).IsNull();
    }
}
