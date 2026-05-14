using System.Text;
using Seiton.Core.Parsing;

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
        var arena = ctx.Arena!;
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
        var arena = ctx.Arena!;

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
        await Assert.That(ctx.Arena).IsNotNull();
        await Assert.That(result1.Workflow!.Jobs.Count).IsEqualTo(1);

        // Second call: only step changed (root sections same)
        var yaml2 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo world\n"u8.ToArray();

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);

        // Second parse should also produce a valid lintable workflow
        await Assert.That(result2.Workflow).IsNotNull();
        await Assert.That(ctx.Arena).IsNotNull();
        await Assert.That(result2.Workflow!.Jobs.Count).IsEqualTo(1);
        // On section should still be resolvable from the arena
        var arena = ctx.Arena!;
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

            var job = result.Workflow!.Jobs.Entries[0].Value;
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
            var arena = ctx.Arena!;
            var runsOnLabel = arena.GetStringValue(job.RunsOn!.Labels![0]);
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

            var job = result.Workflow!.Jobs.Entries[0].Value;
            await Assert.That(job.RunsOn).IsNotNull()
                .Because($"iteration {i}: Job.RunsOn must survive cap-triggered full re-parse");
            await Assert.That(job.Steps).IsNotNull()
                .Because($"iteration {i}: Job.Steps must survive cap-triggered full re-parse");
        }
    }

    /// <summary>
    /// Regression test: verifies that changing filePath forces a full re-parse
    /// even when the YAML bytes are identical. Without filePath tracking, stale
    /// diagnostics with wrong file paths would be returned.
    /// </summary>
    [Test]
    public async Task ParseIncrementally_FilePathChanges_ForcesFullReparse()
    {
        var ctx = new IncrementalParseContext();
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n"u8.ToArray();

        // First parse under path A
        var result1 = ctx.ParseIncrementally(yaml, ".github/workflows/a.yml");
        await Assert.That(result1.Workflow).IsNotNull();

        // Same bytes, different filePath — must NOT return cached result
        var yamlCopy = yaml.ToArray(); // new array instance with same content
        var result2 = ctx.ParseIncrementally(yamlCopy, ".github/workflows/b.yml");

        await Assert.That(result2.Workflow).IsNotNull();
        // The returned parse result must be a fresh parse (not ReferenceEquals to previous)
        await Assert.That(ReferenceEquals(result1.Workflow, result2.Workflow)).IsFalse()
            .Because("different filePath must produce a new parse, not reuse cached workflow");
    }

    /// <summary>
    /// Regression test: exercises arena retention when the previous incremental arena
    /// owns a job that is reused in the next iteration. The old heuristic
    /// (oldArena.JobCount > arena.JobCount) would incorrectly dispose the old arena
    /// when both have the same JobCount, causing use-after-free.
    ///
    /// Scenario: Full parse (3 jobs) → Incremental 1 (changes job A, reuses B/C)
    /// → Incremental 2 (changes job B, reuses A from Incremental 1 arena + C from Full arena).
    /// The second incremental parse must retain arena from Incremental 1 since it owns the reused A.
    /// </summary>
    [Test]
    public async Task ParseIncrementally_RetainsIntermediateArenaOwningReusedJob()
    {
        var ctx = new IncrementalParseContext();

        // Full parse: 3 jobs
        var yaml0 = "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo A0\n  b:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo B0\n  c:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo C0\n"u8.ToArray();
        var r0 = ctx.ParseIncrementally(yaml0, FilePath);
        await Assert.That(r0.Workflow!.Jobs.Count).IsEqualTo(3);

        // Incremental 1: change job A only (B and C reused from full parse arena)
        var yaml1 = "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo A1\n  b:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo B0\n  c:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo C0\n"u8.ToArray();
        var r1 = ctx.ParseIncrementally(yaml1, FilePath);
        await Assert.That(r1.Workflow!.Jobs.Count).IsEqualTo(3);

        // Incremental 2: change job B only (A reused from Incremental 1 arena, C from full arena)
        var yaml2 = "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo A1\n  b:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo B1\n  c:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo C0\n"u8.ToArray();
        var r2 = ctx.ParseIncrementally(yaml2, FilePath);
        await Assert.That(r2.Workflow!.Jobs.Count).IsEqualTo(3);

        // The reused job A (from Incremental 1 arena) must still be valid
        var jobA = r2.Workflow!.Jobs.Entries[0].Value;
        await Assert.That(jobA.RunsOn).IsNotNull()
            .Because("Job A is reused from intermediate arena — must not be disposed");
        await Assert.That(jobA.Steps).IsNotNull()
            .Because("Job A steps must survive intermediate arena retention");
        await Assert.That(jobA.Steps!.Count).IsEqualTo(1);

        // Verify job A's data is resolvable from the current arena
        var arena = ctx.Arena!;
        var runsOnLabel = arena.GetStringValue(jobA.RunsOn!.Labels![0]);
        await Assert.That(Encoding.UTF8.GetString(runsOnLabel)).IsEqualTo("ubuntu-latest");
    }

    /// <summary>
    /// Regression test: workflows with more than 64 jobs must be scanned correctly.
    /// The ScanJobSections method must not silently truncate jobs beyond 64.
    /// </summary>
    [Test]
    public async Task ParseIncrementally_WorkflowWith65Jobs_AllJobsParsed()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("on: push");
        sb.AppendLine("jobs:");
        for (var i = 0; i < 65; i++)
        {
            sb.AppendLine($"  job{i:D3}:");
            sb.AppendLine("    runs-on: ubuntu-latest");
            sb.AppendLine("    steps:");
            sb.AppendLine("      - run: echo ok");
        }

        var yaml = Encoding.UTF8.GetBytes(sb.ToString());

        var ctx = new IncrementalParseContext();
        var result = ctx.ParseIncrementally(yaml, FilePath);

        await Assert.That(result.Workflow).IsNotNull();
        await Assert.That(result.Workflow!.Jobs.Count).IsEqualTo(65);

        // The registry must capture all 65 jobs (not truncated to 64)
        await Assert.That(ctx.Registry.JobCount).IsEqualTo(65)
            .Because("ScanJobSections must not truncate at 64 jobs");

        // Second parse with one job changed — verify incremental still works for >64 jobs
        sb.Clear();
        sb.AppendLine("on: push");
        sb.AppendLine("jobs:");
        for (var i = 0; i < 65; i++)
        {
            sb.AppendLine($"  job{i:D3}:");
            sb.AppendLine("    runs-on: ubuntu-latest");
            sb.AppendLine("    steps:");
            // Change only job064 step
            sb.AppendLine(i == 64 ? "      - run: echo changed" : "      - run: echo ok");
        }

        var yaml2 = Encoding.UTF8.GetBytes(sb.ToString());
        var result2 = ctx.ParseIncrementally(yaml2, FilePath);

        await Assert.That(result2.Workflow).IsNotNull();
        await Assert.That(result2.Workflow!.Jobs.Count).IsEqualTo(65);
    }

    /// <summary>
    /// Regression test: verifies that repeated root section changes don't cause
    /// unbounded arena entry growth. After many iterations, the context should
    /// eventually trigger a full re-parse to reset the baseline.
    /// </summary>
    [Test]
    public async Task ParseIncrementally_RepeatedRootChanges_DoesNotGrowUnbounded()
    {
        var ctx = new IncrementalParseContext();

        // Full parse
        var yaml0 = "on: push\nenv:\n  V: \"0\"\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo test\n"u8.ToArray();
        ctx.ParseIncrementally(yaml0, FilePath);

        // Repeatedly change env (root section) while keeping jobs identical.
        // This exercises the base count growth path. After enough iterations,
        // the context must either stay bounded or force a full re-parse.
        ParseResult? lastResult = null;
        for (var i = 1; i <= 20; i++)
        {
            var yaml = Encoding.UTF8.GetBytes(
                $"on: push\nenv:\n  V: \"{i}\"\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo test\n");
            lastResult = ctx.ParseIncrementally(yaml, FilePath);

            await Assert.That(lastResult!.Workflow).IsNotNull()
                .Because($"iteration {i}: must produce valid workflow");
            await Assert.That(lastResult.Workflow!.Jobs.Count).IsEqualTo(2);
        }

        // Verify last result is fully functional (no corruption from growth)
        var job = lastResult!.Workflow!.Jobs.Entries[0].Value;
        await Assert.That(job.RunsOn).IsNotNull();
        var arena = ctx.Arena!;
        var label = arena.GetStringValue(job.RunsOn!.Labels![0]);
        await Assert.That(Encoding.UTF8.GetString(label)).IsEqualTo("ubuntu-latest");
    }

    /// <summary>
    /// Regression test: verifies that the IsSourceIdentical fast-path works correctly
    /// when the caller reuses the same buffer (overwrites previous content).
    /// The context must store its own copy or update the reference so that a reused
    /// buffer doesn't cause stale results.
    /// </summary>
    [Test]
    public async Task ParseIncrementally_ReusedBuffer_DoesNotReturnStaleResult()
    {
        var ctx = new IncrementalParseContext();
        var yaml1Content = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n";
        var yaml2Content = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo world\n";

        // Simulate double-buffer pattern: two buffers that alternate
        var bufA = Encoding.UTF8.GetBytes(yaml1Content);
        var bufB = new byte[bufA.Length]; // same size

        // First parse with buffer A (yaml1)
        var result1 = ctx.ParseIncrementally(bufA, FilePath);
        await Assert.That(result1.Workflow).IsNotNull();

        // Second parse with buffer B containing same content as bufA (fast path)
        Array.Copy(bufA, bufB, bufA.Length);
        var result2 = ctx.ParseIncrementally(bufB, FilePath);
        await Assert.That(result2.Workflow).IsNotNull();
        await Assert.That(ReferenceEquals(result1.Workflow, result2.Workflow)).IsTrue()
            .Because("identical content should use fast path");

        // Now overwrite buffer A with DIFFERENT content (same length)
        Encoding.UTF8.GetBytes(yaml2Content, bufA);

        // Third parse with mutated buffer A — must NOT return stale result
        var result3 = ctx.ParseIncrementally(bufA, FilePath);
        await Assert.That(result3.Workflow).IsNotNull();
        await Assert.That(ReferenceEquals(result1.Workflow, result3.Workflow)).IsFalse()
            .Because("mutated buffer must not be considered identical to previous content");
    }
}
