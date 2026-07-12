using System.Text;

namespace Seiton.Playground.Tests;

/// <summary>
/// Tests for D-5c: Job-level selective skip.
/// When individual jobs are unchanged between incremental calls, their AST nodes are reused.
/// </summary>
[NotInParallel(PlaygroundTestParallelism.AssemblyLockKey)]
public sealed class IncrementalParseJobSkipTests
{
    private const string FilePath = ".github/workflows/ci.yml";

    [Test]
    public async Task ParseIncrementally_MultiJob_UnchangedJobReused()
    {
        // 2 jobs: build and deploy. Only build step changes (same length edit).
        var yaml1 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n");

        var ctx = new IncrementalParseContext();
        var result1 = ctx.ParseIncrementally(yaml1, FilePath);
        await Assert.That(result1.Workflow.Jobs.Count).IsEqualTo(2);

        // Change only the build job (same length: "hello" → "world")
        var yaml2 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo world\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n");

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);

        await Assert.That(result2.Workflow.HasValue).IsTrue();
        await Assert.That(result2.Workflow.Jobs.Count).IsEqualTo(2);

        // deploy job should be reused (ID-based reuse via BulkImportFrom), build re-parsed
        await Assert.That(ctx.LastReusedJobs).IsNotNull();
        await Assert.That(ctx.LastReusedJobs![0]).IsFalse();
        await Assert.That(ctx.LastReusedJobs![1]).IsTrue();

        // and the reused deploy job must still resolve correctly in the new arena
        var deployJob2 = result2.Workflow.Jobs.GetAt(1).Value;
        await Assert.That(deployJob2.Steps[0].Exec.AsRun().Run.Decode()).IsEqualTo("echo deploy");
    }

    [Test]
    public async Task ParseIncrementally_MultiJob_ChangedJobReParsed()
    {
        var yaml1 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n");

        var ctx = new IncrementalParseContext();
        ctx.ParseIncrementally(yaml1, FilePath);

        // Change the deploy job step ("deploy" → "finish", same length)
        var yaml2 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo finish\n");

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);

        await Assert.That(result2.Workflow.HasValue).IsTrue();
        await Assert.That(result2.Workflow.Jobs.Count).IsEqualTo(2);

        // deploy job's step should contain "finish"
        var deployJob = result2.Workflow.Jobs.GetAt(1).Value;
        var step = deployJob.Steps[0];
        var exec = step.Exec.AsRun();
        var runValue = exec.Run.Decode();
        await Assert.That(runValue).Contains("finish");
    }

    [Test]
    public async Task ParseIncrementally_MultiJob_ReusedJobResolvesFromArena()
    {
        // Verify that reused job's StringNodeId references resolve correctly in the new arena
        var yaml1 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo build1\n  deploy:\n    runs-on: ubuntu-latest\n    needs: [build]\n    steps:\n      - run: echo deploy\n");

        var ctx = new IncrementalParseContext();
        ctx.ParseIncrementally(yaml1, FilePath);

        // Change only build step ("build1" → "build2", same length)
        var yaml2 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo build2\n  deploy:\n    runs-on: ubuntu-latest\n    needs: [build]\n    steps:\n      - run: echo deploy\n");

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);

        // deploy job is reused — verify its needs and step resolve correctly
        var deployJob = result2.Workflow.Jobs.GetAt(1).Value;
        await Assert.That(deployJob.Needs.HasValue).IsTrue();
        await Assert.That(deployJob.Needs.Count).IsEqualTo(1);

        // The deploy step should still resolve
        var step = deployJob.Steps[0];
        var exec = step.Exec.AsRun();
        var runValue = exec.Run.Decode();
        await Assert.That(runValue).IsEqualTo("echo deploy");
    }

    [Test]
    public async Task ParseIncrementally_MultiJob_DifferentLengthEdit_FirstJobUnchanged()
    {
        // First job unchanged, second job has different-length edit
        var yaml1 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo short\n");

        var ctx = new IncrementalParseContext();
        var result1 = ctx.ParseIncrementally(yaml1, FilePath);
        await Assert.That(result1.Workflow.Jobs.Count).IsEqualTo(2);

        // deploy changes to longer text — different total length
        var yaml2 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo much-longer-text\n");

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);

        await Assert.That(result2.Workflow.HasValue).IsTrue();
        await Assert.That(result2.Workflow.Jobs.Count).IsEqualTo(2);

        // build job should be reused (it's before the edit, same bytes at same offset)
        await Assert.That(ctx.LastReusedJobs).IsNotNull();
        await Assert.That(ctx.LastReusedJobs![0]).IsTrue();
        var buildJob2 = result2.Workflow.Jobs.GetAt(0).Value;
        await Assert.That(buildJob2.Steps[0].Exec.AsRun().Run.Decode()).IsEqualTo("echo ok");
    }

    [Test]
    public async Task ParseIncrementally_MultiJob_LintProducesConsistentResults()
    {
        // Verify lint works correctly with job-level skip (via PlaygroundLintRunner)
        var yaml1 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo test1\n";
        var yaml2 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo test2\n";

        var result1 = PlaygroundLintRunner.RunToJsonUtf8(yaml1, FilePath);
        var result2 = PlaygroundLintRunner.RunToJsonUtf8(yaml2, FilePath);

        // Both should produce valid JSON with same diagnostic count
        using var doc1 = System.Text.Json.JsonDocument.Parse(result1);
        using var doc2 = System.Text.Json.JsonDocument.Parse(result2);
        await Assert.That(doc1.RootElement.GetArrayLength()).IsEqualTo(doc2.RootElement.GetArrayLength());
    }

    [Test]
    public async Task ParseIncrementally_ThreeJobs_MiddleJobChanged()
    {
        // 3 jobs: build, test, deploy. Only test job changes.
        var yaml1 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo build\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo test1\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n");

        var ctx = new IncrementalParseContext();
        var result1 = ctx.ParseIncrementally(yaml1, FilePath);
        await Assert.That(result1.Workflow.Jobs.Count).IsEqualTo(3);

        // Change only test job (same length: "test1" → "test2")
        var yaml2 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo build\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo test2\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n");

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);

        await Assert.That(result2.Workflow.HasValue).IsTrue();
        await Assert.That(result2.Workflow.Jobs.Count).IsEqualTo(3);

        // build and deploy should be reused (same bytes at same offsets); test re-parsed
        await Assert.That(ctx.LastReusedJobs).IsNotNull();
        await Assert.That(ctx.LastReusedJobs![0]).IsTrue();
        await Assert.That(ctx.LastReusedJobs![1]).IsFalse();
        await Assert.That(ctx.LastReusedJobs![2]).IsTrue();

        var buildJob2 = result2.Workflow.Jobs.GetAt(0).Value;
        var deployJob2 = result2.Workflow.Jobs.GetAt(2).Value;
        await Assert.That(buildJob2.Steps[0].Exec.AsRun().Run.Decode()).IsEqualTo("echo build");
        await Assert.That(deployJob2.Steps[0].Exec.AsRun().Run.Decode()).IsEqualTo("echo deploy");
    }

    [Test]
    public async Task ParseIncrementally_JobAdded_AllJobsReParsed()
    {
        // Adding a job changes job count → all jobs re-parsed
        var yaml1 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo build\n");

        var ctx = new IncrementalParseContext();
        var result1 = ctx.ParseIncrementally(yaml1, FilePath);
        await Assert.That(result1.Workflow.Jobs.Count).IsEqualTo(1);

        // Add a second job
        var yaml2 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo build\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n");

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);

        await Assert.That(result2.Workflow.HasValue).IsTrue();
        await Assert.That(result2.Workflow.Jobs.Count).IsEqualTo(2);

        // build job should NOT be reused (job count changed → full parse, no reuse recorded)
        await Assert.That(ctx.LastReusedJobs).IsNull();
    }

    // ─────────────────────────────────────────────────────────────────────
    // P-1: Single-job early exit — skip scan pipeline when job changed
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ParseIncrementally_SingleJob_SameLengthEdit_ProducesCorrectResult()
    {
        // Single job: same-length edit should take P-1 early exit and still produce correct AST
        var yaml1 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n");

        var ctx = new IncrementalParseContext();
        var result1 = ctx.ParseIncrementally(yaml1, FilePath);
        await Assert.That(result1.Workflow.HasValue).IsTrue();
        await Assert.That(result1.Workflow.Jobs.Count).IsEqualTo(1);

        // Same-length edit: "hello" → "world" (P-1 detects jobs hash change → FullParseAndStore)
        var yaml2 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo world\n");

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);
        await Assert.That(result2.Workflow.HasValue).IsTrue();
        await Assert.That(result2.Workflow.Jobs.Count).IsEqualTo(1);

        // Verify the step content was updated
        var step = result2.Workflow.Jobs.GetAt(0).Value.Steps[0];
        var exec = step.Exec.AsRun();
        var runValue = exec.Run.Decode();
        await Assert.That(runValue).Contains("world");
    }

    [Test]
    public async Task ParseIncrementally_SingleJob_RepeatedSameLengthEdits_AllCorrect()
    {
        // Simulates the PartialChange benchmark pattern: repeated same-length edits on single job
        var ctx = new IncrementalParseContext();

        var yaml0 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo edit0\n");
        var r0 = ctx.ParseIncrementally(yaml0, FilePath);
        await Assert.That(r0.Workflow.HasValue).IsTrue();

        // Iterate through same-length edits (edit0 → edit1 → edit2 → ...)
        for (var i = 1; i <= 5; i++)
        {
            var yaml = Encoding.UTF8.GetBytes(
                $"on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo edit{i}\n");
            var result = ctx.ParseIncrementally(yaml, FilePath);
            await Assert.That(result.Workflow.HasValue).IsTrue();
            await Assert.That(result.Workflow.Jobs.Count).IsEqualTo(1);

            var step = result.Workflow.Jobs.GetAt(0).Value.Steps[0];
            var exec = step.Exec.AsRun();
            var runValue = exec.Run.Decode();
            await Assert.That(runValue).Contains($"edit{i}");
        }
    }

    [Test]
    public async Task ParseIncrementally_SingleJob_UnchangedContent_SkipsReparse()
    {
        // Single job, same-length source, jobs section unchanged → should NOT take P-1 exit
        // (only root section name changed, jobs identical)
        var yaml1 = Encoding.UTF8.GetBytes(
            "name: AA\non: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n");

        var ctx = new IncrementalParseContext();
        var result1 = ctx.ParseIncrementally(yaml1, FilePath);
        await Assert.That(result1.Workflow.HasValue).IsTrue();

        // Change only name (same length: "AA" → "BB"), jobs section unchanged
        var yaml2 = Encoding.UTF8.GetBytes(
            "name: BB\non: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n");

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);
        await Assert.That(result2.Workflow.HasValue).IsTrue();
        await Assert.That(result2.Workflow.Jobs.Count).IsEqualTo(1);

        // Job should still be correct
        var step = result2.Workflow.Jobs.GetAt(0).Value.Steps[0];
        var exec = step.Exec.AsRun();
        var runValue = exec.Run.Decode();
        await Assert.That(runValue).Contains("echo ok");
    }

    [Test]
    public async Task ParseIncrementally_SingleJob_DifferentLengthEdit_ProducesCorrectResult()
    {
        // Single job with different-length edit → source length differs → P-1 does not apply
        // (falls through to normal path which also goes to FullParseAndStore)
        var yaml1 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo short\n");

        var ctx = new IncrementalParseContext();
        ctx.ParseIncrementally(yaml1, FilePath);

        var yaml2 = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo much-longer-text\n");

        var result2 = ctx.ParseIncrementally(yaml2, FilePath);
        await Assert.That(result2.Workflow.HasValue).IsTrue();
        await Assert.That(result2.Workflow.Jobs.Count).IsEqualTo(1);

        var step = result2.Workflow.Jobs.GetAt(0).Value.Steps[0];
        var exec = step.Exec.AsRun();
        var runValue = exec.Run.Decode();
        await Assert.That(runValue).Contains("much-longer-text");
    }
}
