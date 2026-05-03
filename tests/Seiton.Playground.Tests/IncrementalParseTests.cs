using System.Text;

namespace Seiton.Playground.Tests;

public sealed class IncrementalParseTests
{
    private const string FilePath = ".github/workflows/ci.yml";

    [Test]
    public async Task ParseIncrementally_FirstCall_ReturnsFullParseResult()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n"u8.ToArray();

        var ctx = new IncrementalParseContext();
        var result = ctx.ParseIncrementally(yaml, FilePath);

        await Assert.That(result.Workflow).IsNotNull();
        await Assert.That(result.Workflow!.On).IsNotNull();
        await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
        await Assert.That(result.HasFatalError).IsFalse();
    }

    [Test]
    public async Task ParseIncrementally_UnchangedRootSections_ProducesCorrectWorkflow()
    {
        // First call: full parse
        var yaml1 = "on: push\npermissions:\n  contents: read\nenv:\n  CI: true\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n"u8.ToArray();

        var ctx = new IncrementalParseContext();
        var result1 = ctx.ParseIncrementally(yaml1, FilePath);
        // Keep result1 alive (don't dispose - context holds previous arena)

        // Second call: only job step changed, root sections (on, permissions, env) unchanged
        var yaml2 = "on: push\npermissions:\n  contents: read\nenv:\n  CI: true\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo world\n"u8.ToArray();

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);

        await Assert.That(result2.Workflow).IsNotNull();
        // Root sections should be valid (either reused or re-parsed)
        await Assert.That(result2.Workflow!.On.Count).IsEqualTo(1);
        await Assert.That(result2.Workflow!.Permissions).IsNotNull();
        await Assert.That(result2.Workflow!.Env).IsNotNull();
        // Jobs should be freshly parsed
        await Assert.That(result2.Workflow!.Jobs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ParseIncrementally_ChangedOnSection_ReParsesOnCorrectly()
    {
        var yaml1 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n"u8.ToArray();

        var ctx = new IncrementalParseContext();
        ctx.ParseIncrementally(yaml1, FilePath);

        // Changed on: push → on: pull_request (different length → all subsequent sections shift)
        var yaml2 = "on: pull_request\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n"u8.ToArray();

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);

        await Assert.That(result2.Workflow).IsNotNull();
        await Assert.That(result2.Workflow!.On.Count).IsEqualTo(1);
        // The event should be pull_request, not stale push
        var arena = result2.Arena!;
        var eventName = arena.GetStringValue(result2.Workflow!.On[0].EventName);
        await Assert.That(Encoding.UTF8.GetString(eventName)).IsEqualTo("pull_request");
    }

    [Test]
    public async Task ParseIncrementally_SkippedSections_AreResolvableFromArena()
    {
        // Workflow with permissions and concurrency (root sections)
        var yaml1 = "on: push\npermissions:\n  contents: read\nconcurrency:\n  group: ci-${{ github.ref }}\n  cancel-in-progress: true\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n"u8.ToArray();

        var ctx = new IncrementalParseContext();
        ctx.ParseIncrementally(yaml1, FilePath);

        // Only change the job step (root sections unchanged at same offsets)
        var yaml2 = "on: push\npermissions:\n  contents: read\nconcurrency:\n  group: ci-${{ github.ref }}\n  cancel-in-progress: true\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo world\n"u8.ToArray();

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);

        await Assert.That(result2.Workflow).IsNotNull();
        var arena = result2.Arena!;

        // Permissions should resolve correctly
        var perms = result2.Workflow!.Permissions!;
        await Assert.That(perms.Scopes).IsNotNull();

        // Concurrency should resolve correctly
        var conc = result2.Workflow!.Concurrency!;
        var groupValue = arena.GetStringValue(conc.Group);
        await Assert.That(Encoding.UTF8.GetString(groupValue)).Contains("ci-");
    }

    [Test]
    public async Task ParseIncrementally_LintProducesConsistentResults()
    {
        // Verify that incremental parsing produces a Workflow that can be linted
        // by checking that the Workflow has all required fields populated
        var yaml1 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n"u8.ToArray();

        var ctx = new IncrementalParseContext();
        var result1 = ctx.ParseIncrementally(yaml1, FilePath);

        // First parse produces a valid lintable workflow
        await Assert.That(result1.Workflow).IsNotNull();
        await Assert.That(result1.Arena).IsNotNull();
        await Assert.That(result1.Workflow!.Jobs.Count).IsEqualTo(1);

        // Second call: only step changed (root sections same)
        var yaml2 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo world\n"u8.ToArray();

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);

        // Second parse should also produce a valid lintable workflow
        await Assert.That(result2.Workflow).IsNotNull();
        await Assert.That(result2.Arena).IsNotNull();
        await Assert.That(result2.Workflow!.Jobs.Count).IsEqualTo(1);
        // On section should still be resolvable from the arena
        var arena = result2.Arena!;
        var eventName = arena.GetStringValue(result2.Workflow!.On[0].EventName);
        await Assert.That(Encoding.UTF8.GetString(eventName)).IsEqualTo("push");
    }

    [Test]
    public async Task ParseIncrementally_MultipleCalls_StaysConsistent()
    {
        var ctx = new IncrementalParseContext();
        var baseYaml = "on: push\nenv:\n  CI: true\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ";

        // Simulate 10 sequential edits (appending characters)
        for (var i = 0; i < 10; i++)
        {
            var yaml = Encoding.UTF8.GetBytes(baseYaml + new string('x', i + 1) + "\n");
            var result = ctx.ParseIncrementally(yaml, FilePath);

            // Each call should produce a valid workflow
            await Assert.That(result.Workflow).IsNotNull();
            await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
            await Assert.That(result.Workflow!.Env).IsNotNull();
            await Assert.That(result.Workflow!.Jobs.Count).IsEqualTo(1);
        }
    }

    /// <summary>
    /// Regression test: verifies that reused Job objects remain valid after multiple
    /// incremental parses. This catches the use-after-free bug where disposing a retained
    /// arena calls Job.Reset() on objects still referenced by the current workflow.
    ///
    /// Scenario: only the env section changes (root section), while the job section remains
    /// byte-identical. This forces job reuse across multiple iterations. If arena lifecycle
    /// is incorrect (e.g., single-arena retention that disposes the job-owning arena),
    /// Job.RunsOn and Job.Steps become null after the arena is disposed.
    /// </summary>
    [Test]
    public async Task ParseIncrementally_ReusedJobsSurviveMultipleIncrementalParses()
    {
        var ctx = new IncrementalParseContext();

        // Full parse (warm-up): creates arena0 with Job objects in its pool
        var yaml0 = "on: push\nenv:\n  V: \"0\"\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n"u8.ToArray();
        var result0 = ctx.ParseIncrementally(yaml0, FilePath);
        await Assert.That(result0.Workflow).IsNotNull();
        await Assert.That(result0.Workflow!.Jobs.Count).IsEqualTo(1);

        // Perform multiple incremental parses where ONLY the env value changes.
        // The job section bytes are identical each time → jobs are reused from arena0.
        // With the old single-arena bug, iteration 2 would dispose arena0 (calling
        // Job.Reset()) while the workflow still references those Job objects.
        for (var i = 1; i <= 6; i++)
        {
            var yaml = Encoding.UTF8.GetBytes(
                $"on: push\nenv:\n  V: \"{i}\"\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n");
            var result = ctx.ParseIncrementally(yaml, FilePath);

            await Assert.That(result.Workflow).IsNotNull();
            await Assert.That(result.Workflow!.Jobs.Count).IsEqualTo(1);

            var job = result.Workflow!.Jobs[0];
            // If arena lifecycle is broken, these become null after Job.Reset()
            await Assert.That(job.RunsOn)
                .IsNotNull()
                .Because($"iteration {i}: Job.RunsOn must not be null (arena use-after-free)");
            await Assert.That(job.Steps)
                .IsNotNull()
                .Because($"iteration {i}: Job.Steps must not be null (arena use-after-free)");
            await Assert.That(job.Steps!.Count)
                .IsEqualTo(1)
                .Because($"iteration {i}: Job.Steps.Count must remain 1");

            // Verify the arena can still resolve string data for this job
            var arena = result.Arena!;
            var runsOnLabel = arena.GetStringValue(job.RunsOn!.Labels[0]);
            await Assert.That(Encoding.UTF8.GetString(runsOnLabel))
                .IsEqualTo("ubuntu-latest")
                .Because($"iteration {i}: RunsOn label must be resolvable from arena");
        }
    }

    /// <summary>
    /// Regression test: exercises the MaxRetainedArenas cap by performing more incremental
    /// parses than the cap allows. Verifies that the cap triggers a clean full re-parse
    /// rather than corrupting data.
    ///
    /// This test uses a different job on each "epoch" to force retention accumulation,
    /// then verifies recovery after cap is hit.
    /// </summary>
    [Test]
    public async Task ParseIncrementally_ExceedsRetentionCap_RecoversByFullReparse()
    {
        var ctx = new IncrementalParseContext();

        // Full parse with job A
        var yamlA = "on: push\nenv:\n  V: \"0\"\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo A\n"u8.ToArray();
        ctx.ParseIncrementally(yamlA, FilePath);

        // Change to job B (forces full reparse since job content changed)
        var yamlB = "on: push\nenv:\n  V: \"1\"\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo B\n"u8.ToArray();
        ctx.ParseIncrementally(yamlB, FilePath);

        // Now do many incremental parses changing only env (job B stays same → reused)
        // This accumulates retained arenas until cap triggers full re-parse
        for (var i = 2; i <= 10; i++)
        {
            var yaml = Encoding.UTF8.GetBytes(
                $"on: push\nenv:\n  V: \"{i}\"\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo B\n");
            var result = ctx.ParseIncrementally(yaml, FilePath);

            await Assert.That(result.Workflow).IsNotNull()
                .Because($"iteration {i}: workflow must be non-null after cap recovery");
            await Assert.That(result.Workflow!.Jobs.Count).IsEqualTo(1);

            var job = result.Workflow!.Jobs[0];
            await Assert.That(job.RunsOn).IsNotNull()
                .Because($"iteration {i}: Job.RunsOn must survive cap-triggered full re-parse");
            await Assert.That(job.Steps).IsNotNull()
                .Because($"iteration {i}: Job.Steps must survive cap-triggered full re-parse");
        }
    }
}
