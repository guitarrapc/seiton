using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

public sealed partial class ParserTests
{
    [Test]
    public async Task Parse_ParallelSteps_ok_background_run_same_step()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - id: build
                    run: npm run build
                    background: true
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out var arena);
        await Assert.That(result.Diagnostics).IsEmpty();
        var step = result.Workflow!.Jobs.Values().First().Steps![0];
        await Assert.That(step.Background.HasValue).IsTrue();
        await Assert.That(arena!.GetBoolValue(step.Background)).IsTrue();
        await Assert.That(step.Exec).IsTypeOf<ExecRun>();
    }

    [Test]
    public async Task Parse_ParallelSteps_ok_background_wait_sequence()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - id: build-frontend
                    run: npm run build
                    background: true
                  - id: build-backend
                    run: npm run build
                    background: true
                  - wait: [build-frontend, build-backend]
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parse_ParallelSteps_ok_wait_array()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - id: a
                    run: echo a
                    background: true
                  - id: b
                    run: echo b
                    background: true
                  - wait: [a, b]
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out var arena);
        await Assert.That(result.Diagnostics).IsEmpty();
        var waitStep = result.Workflow!.Jobs.Values().First().Steps![2];
        await Assert.That(waitStep.Exec).IsTypeOf<ExecWait>();
        var targets = ((ExecWait)waitStep.Exec).Targets;
        await Assert.That(targets!.Count).IsEqualTo(2);
        await Assert.That(Encoding.UTF8.GetString(arena!.GetStringValue(targets[0]))).IsEqualTo("a");
    }

    [Test]
    public async Task Parse_ParallelSteps_ok_wait_all_null()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hi
                    background: true
                  - wait-all: null
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics).IsEmpty();
        var waitAllStep = result.Workflow!.Jobs.Values().First().Steps![1];
        await Assert.That(waitAllStep.Exec).IsTypeOf<ExecWaitAll>();
    }

    [Test]
    public async Task Parse_ParallelSteps_ok_cancel()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - id: monitor
                    run: ./monitor.sh
                    background: true
                  - cancel: monitor
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out var arena);
        await Assert.That(result.Diagnostics).IsEmpty();
        var cancelStep = result.Workflow!.Jobs.Values().First().Steps![1];
        await Assert.That(cancelStep.Exec).IsTypeOf<ExecCancel>();
        await Assert.That(Encoding.UTF8.GetString(arena!.GetStringValue(((ExecCancel)cancelStep.Exec).Target))).IsEqualTo("monitor");
    }

    [Test]
    public async Task Parse_ParallelSteps_ok_parallel_nested()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - parallel:
                    - run: npm run build-app1
                    - run: npm run build-app2
                    - run: npm run build-app3
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics).IsEmpty();
        var parallel = (ExecParallel)result.Workflow!.Jobs.Values().First().Steps![0].Exec;
        await Assert.That(parallel.Steps!.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Parse_ParallelSteps_ok_background_uses()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                    background: true
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out var arena);
        await Assert.That(result.Diagnostics).IsEmpty();
        var step = result.Workflow!.Jobs.Values().First().Steps![0];
        await Assert.That(step.Background.HasValue).IsTrue();
        await Assert.That(arena!.GetBoolValue(step.Background)).IsTrue();
        await Assert.That(step.Exec).IsTypeOf<ExecAction>();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_action_metadata_background()
    {
        var yaml = """
            name: My action
            description: test
            runs:
              using: composite
              steps:
                - run: echo hi
                  background: true
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "action.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"background\"", StringComparison.Ordinal)
            && d.Message.Contains("composite action", StringComparison.Ordinal))).IsTrue();
        var step = result.ActionMetadata!.Runs!.Steps![0];
        await Assert.That(step.Background.HasValue).IsFalse();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_parallel_child_nested_parallel()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - parallel:
                    - run: echo a
                    - parallel:
                      - run: echo b
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"parallel\"", StringComparison.Ordinal)
            && d.Message.Contains("parallel group", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_parallel_child_wait_all()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - parallel:
                    - wait-all: null
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"wait-all\"", StringComparison.Ordinal)
            && d.Message.Contains("parallel group", StringComparison.Ordinal))).IsTrue();
        var child = ((ExecParallel)result.Workflow!.Jobs.Values().First().Steps![0].Exec).Steps![0];
        await Assert.That(child.Exec).IsNotTypeOf<ExecWaitAll>();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_parallel_child_wait()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - parallel:
                    - wait: other
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"wait\"", StringComparison.Ordinal)
            && d.Message.Contains("parallel group", StringComparison.Ordinal))).IsTrue();
        var child = ((ExecParallel)result.Workflow!.Jobs.Values().First().Steps![0].Exec).Steps![0];
        await Assert.That(child.Exec).IsNotTypeOf<ExecWait>();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_parallel_child_cancel()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - parallel:
                    - cancel: other
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"cancel\"", StringComparison.Ordinal)
            && d.Message.Contains("parallel group", StringComparison.Ordinal))).IsTrue();
        var child = ((ExecParallel)result.Workflow!.Jobs.Values().First().Steps![0].Exec).Steps![0];
        await Assert.That(child.Exec).IsNotTypeOf<ExecCancel>();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_parallel_child_background()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - parallel:
                    - run: echo hi
                      background: true
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"background\"", StringComparison.Ordinal)
            && d.Message.Contains("parallel group", StringComparison.Ordinal))).IsTrue();
        var child = ((ExecParallel)result.Workflow!.Jobs.Values().First().Steps![0].Exec).Steps![0];
        await Assert.That(child.Background.HasValue).IsFalse();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_action_metadata_parallel()
    {
        var yaml = """
            name: My action
            description: test
            runs:
              using: composite
              steps:
                - parallel:
                  - run: echo hi
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "action.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"parallel\"", StringComparison.Ordinal)
            && d.Message.Contains("composite action", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.ActionMetadata!.Runs!.Steps![0].Exec).IsNotTypeOf<ExecParallel>();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_action_metadata_wait_all()
    {
        var yaml = """
            name: My action
            description: test
            runs:
              using: composite
              steps:
                - wait-all: null
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "action.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"wait-all\"", StringComparison.Ordinal)
            && d.Message.Contains("composite action", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_ParallelSteps_ok_wait_scalar()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - id: a
                    run: echo a
                    background: true
                  - wait: a
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out var arena);
        await Assert.That(result.Diagnostics).IsEmpty();
        var waitStep = result.Workflow!.Jobs.Values().First().Steps![1];
        await Assert.That(waitStep.Exec).IsTypeOf<ExecWait>();
        var targets = ((ExecWait)waitStep.Exec).Targets;
        await Assert.That(targets!.Count).IsEqualTo(1);
        await Assert.That(Encoding.UTF8.GetString(arena!.GetStringValue(targets[0]))).IsEqualTo("a");
    }

    [Test]
    public async Task Parse_ParallelSteps_ok_wait_all_true()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hi
                    background: true
                  - wait-all: true
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow!.Jobs.Values().First().Steps![1].Exec).IsTypeOf<ExecWaitAll>();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_background_expression()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hi
                    background: ${{ true }}
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("background must be bool", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_action_metadata_cancel()
    {
        var yaml = """
            name: My action
            description: test
            runs:
              using: composite
              steps:
                - cancel: other
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "action.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"cancel\"", StringComparison.Ordinal)
            && d.Message.Contains("composite action", StringComparison.Ordinal))).IsTrue();
        var step = result.ActionMetadata!.Runs!.Steps![0];
        await Assert.That(step.Exec).IsNotTypeOf<ExecCancel>();
    }

    [Test]
    public async Task Parse_ParallelSteps_ok_parallel_child_uses()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - parallel:
                    - uses: actions/checkout@v4
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics).IsEmpty();
        var child = ((ExecParallel)result.Workflow!.Jobs.Values().First().Steps![0].Exec).Steps![0];
        await Assert.That(child.Exec).IsTypeOf<ExecAction>();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_action_metadata_wait_scalar()
    {
        var yaml = """
            name: My action
            description: test
            runs:
              using: composite
              steps:
                - wait: other
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "action.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"wait\"", StringComparison.Ordinal)
            && d.Message.Contains("composite action", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.ActionMetadata!.Runs!.Steps![0].Exec).IsNotTypeOf<ExecWait>();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_parallel_child_missing_primary_message()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - parallel:
                    - name: empty child
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("must run script with \"run\" section or run action with \"uses\" section", StringComparison.Ordinal)
            && d.Message.Contains("parallel[1]", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_no_primary()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - name: empty
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("must have step execution key", StringComparison.Ordinal)
            || d.Message.Contains("must run script", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_run_and_wait_same_step()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hi
                    wait: other
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"run\"", StringComparison.Ordinal)
            && d.Message.Contains("step to wait for background steps", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_background_on_wait_step()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - wait: other
                    background: true
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"background\"", StringComparison.Ordinal)
            && d.Message.Contains("step to wait for background steps", StringComparison.Ordinal))).IsTrue();
        var step = result.Workflow!.Jobs.Values().First().Steps![0];
        await Assert.That(step.Background.HasValue).IsFalse();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_parallel_empty()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - parallel: []
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("parallel must be non-empty", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_wait_empty_array()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - wait: []
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("wait must be string or non-empty sequence", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_parallel_step_if()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - parallel:
                    - run: echo a
                    if: ${{ true }}
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"if\"", StringComparison.Ordinal)
            && d.Message.Contains("not supported on parallel, wait, wait-all, or cancel steps", StringComparison.Ordinal))).IsTrue();
        var step = result.Workflow!.Jobs.Values().First().Steps![0];
        await Assert.That(step.If.HasValue).IsFalse();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_wait_step_if()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - id: svc
                    run: echo hi
                    background: true
                  - wait: [svc]
                    if: ${{ true }}
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        var ifDiagnostics = result.Diagnostics.Where(d =>
            d.Message.Contains("unexpected key \"if\"", StringComparison.Ordinal)
            && d.Message.Contains("not supported on parallel, wait, wait-all, or cancel steps", StringComparison.Ordinal)).ToArray();
        await Assert.That(ifDiagnostics.Length).IsEqualTo(1);
        var waitStep = result.Workflow!.Jobs.Values().First().Steps![1];
        await Assert.That(waitStep.If.HasValue).IsFalse();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_wait_all_step_if()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hi
                    background: true
                  - wait-all:
                    if: ${{ true }}
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"if\"", StringComparison.Ordinal)
            && d.Message.Contains("not supported on parallel, wait, wait-all, or cancel steps", StringComparison.Ordinal))).IsTrue();
        var waitAllStep = result.Workflow!.Jobs.Values().First().Steps![1];
        await Assert.That(waitAllStep.If.HasValue).IsFalse();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_cancel_step_if()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - id: svc
                    run: echo hi
                    background: true
                  - cancel: svc
                    if: ${{ true }}
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"if\"", StringComparison.Ordinal)
            && d.Message.Contains("not supported on parallel, wait, wait-all, or cancel steps", StringComparison.Ordinal))).IsTrue();
        var cancelStep = result.Workflow!.Jobs.Values().First().Steps![1];
        await Assert.That(cancelStep.If.HasValue).IsFalse();
    }

    [Test]
    public async Task Parse_ParallelSteps_ng_wait_step_if_before_primary()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - id: svc
                    run: echo hi
                    background: true
                  - if: ${{ true }}
                    wait: [svc]
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out _);
        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unexpected key \"if\"", StringComparison.Ordinal)
            && d.Message.Contains("not supported on parallel, wait, wait-all, or cancel steps", StringComparison.Ordinal))).IsTrue();
        var waitStep = result.Workflow!.Jobs.Values().First().Steps![1];
        await Assert.That(waitStep.If.HasValue).IsFalse();
    }

    [Test]
    public async Task Parse_ok_bare_jobs_at_eof_without_trailing_newline_does_not_hang()
    {
        var bytes = Encoding.UTF8.GetBytes("on: push\njobs:");
        await Assert.That(bytes[^1]).IsNotEqualTo((byte)'\n');

        using var parseCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (result, arena) = await Task.Run(() =>
        {
            var r = WorkflowParser.ParseDirect(bytes, "wf.yml", out var a);
            return (r, a);
        }).WaitAsync(parseCts.Token);

        try
        {
            await Assert.That(result.HasFatalError).IsFalse();
            await Assert.That(result.Diagnostics.Any(d =>
                d.Message.Contains("jobs must be object", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            arena?.Dispose();
        }
    }

    [Test]
    public async Task Parse_ParallelSteps_ok_bare_wait_all_at_eof_without_trailing_newline()
    {
        const string yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hi
                    background: true
                  - wait-all:
            """;
        var bytes = Encoding.UTF8.GetBytes(yaml.TrimEnd('\n', '\r'));
        await Assert.That(bytes[^1]).IsNotEqualTo((byte)'\n');

        using var parseCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (result, arena) = await Task.Run(() =>
        {
            var r = WorkflowParser.ParseDirect(bytes, "wf.yml", out var a);
            return (r, a);
        }).WaitAsync(parseCts.Token);

        try
        {
            await Assert.That(result.HasFatalError).IsFalse();
            var steps = result.Workflow!.Jobs.Values().First().Steps!;
            await Assert.That(steps.Count).IsEqualTo(2);
            await Assert.That(steps[1].Exec).IsTypeOf<ExecWaitAll>();
            await Assert.That(bytes[^1]).IsNotEqualTo((byte)'\n');
        }
        finally
        {
            arena?.Dispose();
        }
    }

    [Test]
    public async Task Parse_ParallelSteps_ok_bare_wait_all_at_eof_without_trailing_newline_incremental()
    {
        const string yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hi
                    background: true
                  - wait-all:
            """;
        var bytes = Encoding.UTF8.GetBytes(yaml.TrimEnd('\n', '\r'));
        await Assert.That(bytes[^1]).IsNotEqualTo((byte)'\n');

        using var parseCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (result, arena) = await Task.Run(() =>
        {
            var a = AstArena.Rent(bytes);
            try
            {
                var r = WorkflowParser.ParseIncremental(bytes, "wf.yml", a, rootSkipMask: 0);
                return (r, a);
            }
            catch
            {
                a.Dispose();
                throw;
            }
        }).WaitAsync(parseCts.Token);

        try
        {
            await Assert.That(result.HasFatalError).IsFalse();
            var steps = result.Workflow!.Jobs.Values().First().Steps!;
            await Assert.That(steps[1].Exec).IsTypeOf<ExecWaitAll>();
        }
        finally
        {
            arena.Dispose();
        }
    }

    [Test]
    public async Task Parse_ParallelSteps_ok_parallel_child_if()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - parallel:
                    - if: ${{ github.ref == 'refs/heads/main' }}
                      run: echo hi
            """;

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(yaml), "wf.yml", out var arena);
        await Assert.That(result.Diagnostics).IsEmpty();
        var child = ((ExecParallel)result.Workflow!.Jobs.Values().First().Steps![0].Exec).Steps![0];
        await Assert.That(child.If.HasValue).IsTrue();
    }
}
