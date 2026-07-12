using System.Text;

namespace Seiton.Playground.Tests;

/// <summary>
/// Tests for D-5d: Lint result cache.
/// When a job is unchanged (reused via D-5c), its lint diagnostics from the previous run
/// are reused without re-running lint rules on that job.
/// </summary>
[NotInParallel(PlaygroundTestParallelism.AssemblyLockKey)]
public sealed class IncrementalLintCacheTests
{
    private const string FilePath = ".github/workflows/ci.yml";

    /// <summary>
    /// In the 2-job test YAMLs, the <c>deploy:</c> key is on line 7 (1-indexed).
    /// Job-level rules (e.g. job-timeout-minutes-required) report at the job id line.
    /// </summary>
    private const int DeployJobStartLine = 7;

    [Test]
    public async Task LintIncrementally_UnchangedJob_ReusesCachedDiagnostics()
    {
        // 2 jobs: "build" (missing timeout → diagnostic) and "deploy" (missing timeout → diagnostic).
        // On second call, only "build" changes but "deploy" is identical.
        // The deploy job's diagnostics should be returned from cache (not re-linted).
        var yaml1 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n";
        var yaml2 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo world\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n";

        var ctx = new IncrementalParseContext();
        // First call: full lint — establishes cache
        var result1 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml1), FilePath);

        // Deploy job starts at the `deploy:` key on line 7 in these YAMLs.
        // Job-level lint rules report at the job id line, so include line 7.
        var deployDiags1 = result1.Where(d => d.GetProperty("line").GetInt32() >= DeployJobStartLine).ToArray();

        // Second call: "build" changes, "deploy" is identical (same offset + hash)
        var result2 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        var deployDiags2 = result2.Where(d => d.GetProperty("line").GetInt32() >= DeployJobStartLine).ToArray();

        // Deploy diagnostics should be identical (reused from cache)
        await Assert.That(deployDiags2.Length).IsEqualTo(deployDiags1.Length);
        for (var i = 0; i < deployDiags1.Length; i++)
        {
            await Assert.That(deployDiags2[i].GetProperty("message").GetString())
                .IsEqualTo(deployDiags1[i].GetProperty("message").GetString());
        }
    }

    [Test]
    public async Task LintIncrementally_ChangedJob_ProducesFreshDiagnostics()
    {
        // "build" has a fixable issue. After edit, the issue is fixed in build but
        // deploy (unchanged) should still show its diagnostics.
        var yaml1 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n";
        // Fix build by adding persist-credentials: false
        var yaml2 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n        with:\n          persist-credentials: false\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n";

        var ctx = new IncrementalParseContext();
        var result1 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml1), FilePath);
        var result2 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // After fix, build's checkout diagnostic should be gone
        var buildCheckoutDiags1 = result1.Where(d =>
            d.GetProperty("message").GetString()!.Contains("persist-credentials")).ToArray();
        var buildCheckoutDiags2 = result2.Where(d =>
            d.GetProperty("message").GetString()!.Contains("persist-credentials")).ToArray();

        await Assert.That(buildCheckoutDiags1.Length).IsGreaterThan(0);
        await Assert.That(buildCheckoutDiags2.Length).IsEqualTo(0);
    }

    [Test]
    public async Task LintIncrementally_ConsistentWithFullLint()
    {
        // Verify that incremental lint produces the exact same diagnostics as a fresh full lint
        var yaml1 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo build\n  test:\n    runs-on: ubuntu-latest\n    needs: [build]\n    steps:\n      - run: echo test1\n  deploy:\n    runs-on: ubuntu-latest\n    needs: [test]\n    steps:\n      - run: echo deploy\n";
        var yaml2 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo build\n  test:\n    runs-on: ubuntu-latest\n    needs: [build]\n    steps:\n      - run: echo test2\n  deploy:\n    runs-on: ubuntu-latest\n    needs: [test]\n    steps:\n      - run: echo deploy\n";

        // Incremental path
        var ctx = new IncrementalParseContext();
        ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml1), FilePath);
        var incrementalResult = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // Fresh full lint (new context → no cache)
        var freshCtx = new IncrementalParseContext();
        var freshResult = freshCtx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // Diagnostic counts must match
        await Assert.That(incrementalResult.Length).IsEqualTo(freshResult.Length);

        // Each diagnostic must match by full content (sorted by line/column/ruleId/message)
        await AssertDiagnosticsEquivalent(incrementalResult, freshResult);
    }

    [Test]
    public async Task LintIncrementally_FirstCall_PerformsFullLint()
    {
        // First call (no previous state) should produce normal lint results
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n";

        var ctx = new IncrementalParseContext();
        var result = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml), FilePath);

        // Should have diagnostics (at minimum: job-timeout-minutes-required, job-permissions-required)
        await Assert.That(result.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task LintIncrementally_DifferentLengthEdit_CacheStillWorks()
    {
        // Edit that changes total source length — deploy at different offset should NOT be cached
        // (since its bytes are at a different offset in the new source)
        var yaml1 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo short\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n";
        var yaml2 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo much-longer-text-here\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n";

        var ctx = new IncrementalParseContext();
        ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml1), FilePath);
        var result2 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // Should still produce valid diagnostics (full re-lint for shifted jobs)
        await Assert.That(result2.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task LintIncrementally_WorkflowPostDiagnostics_NoDuplicatesWhenJobSkipped()
    {
        // Regression: NeedsGraphRule.DetectCycles runs in VisitWorkflowPost (always runs),
        // emitting a diagnostic at a skipped job's location. MergeDiagnosticsWithCache
        // must not duplicate it with the same diagnostic from the cache.
        // Scenario: jobs a and b have a cycle. On 2nd call only "b" changes.
        var yaml1 = "on: push\njobs:\n  a:\n    needs: b\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo a\n  b:\n    needs: a\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo b\n";
        var yaml2 = "on: push\njobs:\n  a:\n    needs: b\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo a\n  b:\n    needs: a\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo b2\n";

        var ctx = new IncrementalParseContext();
        var result1 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml1), FilePath);
        var result2 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // Fresh lint of yaml2 (no cache) to compare
        var freshCtx = new IncrementalParseContext();
        var freshResult = freshCtx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // Cycle diagnostics must not be duplicated
        var cycleDiags2 = result2.Where(d =>
            d.TryGetProperty("ruleId", out var r) && r.GetString() == "needs-graph").ToArray();
        var cycleDiagsFresh = freshResult.Where(d =>
            d.TryGetProperty("ruleId", out var r) && r.GetString() == "needs-graph").ToArray();

        await Assert.That(cycleDiags2.Length).IsEqualTo(cycleDiagsFresh.Length);

        // Total diagnostic count must also match fresh lint
        await Assert.That(result2.Length).IsEqualTo(freshResult.Length);
    }

    [Test]
    public async Task LintIncrementally_CrossJobDependency_InvalidatesDependentJob()
    {
        // Regression: job A depends on job B (needs: b). When B's ID is renamed,
        // A is byte-identical at the same offset but its "needs: b" reference is now invalid.
        // A must NOT be skipped — its cached diagnostics would be stale.
        // Use same-length IDs so byte offsets don't shift.
        var yaml1 = "on: push\njobs:\n  a:\n    needs: b\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo a\n  b:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo b\n";
        // Rename job "b" to "c" (same length) — job A's bytes/offset are identical
        var yaml2 = "on: push\njobs:\n  a:\n    needs: b\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo a\n  c:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo b\n";

        var ctx = new IncrementalParseContext();
        var result1 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml1), FilePath);
        var result2 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // Fresh lint of yaml2 (no cache) to compare
        var freshCtx = new IncrementalParseContext();
        var freshResult = freshCtx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // yaml2 should have "unknown job 'b'" diagnostic for job A
        var unknownJobFresh = freshResult
            .Where(d => d.GetProperty("message").GetString()!.Contains("unknown job"))
            .ToArray();
        await Assert.That(unknownJobFresh.Length).IsGreaterThan(0);

        // Incremental result must also have the same "unknown job" diagnostic
        var unknownJobIncremental = result2
            .Where(d => d.GetProperty("message").GetString()!.Contains("unknown job"))
            .ToArray();
        await Assert.That(unknownJobIncremental.Length).IsEqualTo(unknownJobFresh.Length);
    }

    [Test]
    public async Task LintIncrementally_SkippedJobDiagnosticsHaveCorrectOffsets()
    {
        // Verify that cached diagnostics have correct line/column (same offset = same line)
        var yaml1 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n";
        var yaml2 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo world\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n";

        var ctx = new IncrementalParseContext();
        var result1 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml1), FilePath);
        var result2 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // Deploy job starts at the `deploy:` key on line 7 — filter by line range
        var deployDiags1 = result1
            .Where(d => d.GetProperty("line").GetInt32() >= DeployJobStartLine)
            .OrderBy(d => d.GetProperty("line").GetInt32())
            .ToArray();
        var deployDiags2 = result2
            .Where(d => d.GetProperty("line").GetInt32() >= DeployJobStartLine)
            .OrderBy(d => d.GetProperty("line").GetInt32())
            .ToArray();

        await Assert.That(deployDiags2.Length).IsEqualTo(deployDiags1.Length);
        for (var i = 0; i < deployDiags1.Length; i++)
        {
            await Assert.That(deployDiags2[i].GetProperty("line").GetInt32())
                .IsEqualTo(deployDiags1[i].GetProperty("line").GetInt32());
        }
    }

    [Test]
    public async Task LintIncrementally_RepeatedSameCountEdits_DiagnosticsRemainCorrect()
    {
        // P-3: Verify correctness when CacheJobDiagnostics is called repeatedly
        // with the same diagnostic count per job (the reuse scenario).
        // 3 iterations with same-length edits: diagnostic counts per job stay constant.
        var ctx = new IncrementalParseContext();

        var yaml1 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo edit0\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n";
        var result1 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml1), FilePath);

        // Repeated same-length edits on build job only
        for (var i = 1; i <= 3; i++)
        {
            var yaml = $"on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo edit{i}\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n";
            var result = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml), FilePath);

            // Each iteration should produce the same number of diagnostics
            await Assert.That(result.Length).IsEqualTo(result1.Length);

            // Deploy diagnostics should be present and correct (from cache)
            var deployDiags = result.Where(d => d.GetProperty("line").GetInt32() >= DeployJobStartLine).ToArray();
            await Assert.That(deployDiags.Length).IsGreaterThan(0);

            // Verify against fresh full lint
            var freshCtx = new IncrementalParseContext();
            var freshResult = freshCtx.LintIncrementally(Encoding.UTF8.GetBytes(yaml), FilePath);
            await Assert.That(result.Length).IsEqualTo(freshResult.Length);
            await AssertDiagnosticsEquivalent(result, freshResult);
        }
    }

    [Test]
    public async Task LintIncrementally_RenameCreatesDuplicateJobKey_LaterJobDiagnosticsPreserved()
    {
        // Regression: _lastReusedJobs was recorded in REGISTRY order (byte-scan of job keys)
        // but consumed in JOBS-MAP-ENTRY order. When a job key is dropped as a duplicate
        // during incremental parse (registry counts the line; jobs map has no entry), the
        // orders diverge and the WRONG job is skipped in lint — one job's diagnostics vanish.
        // 4 jobs a,b,c,d (each missing timeout → per-job diagnostics). Rename b: → a:
        // (same byte length, so c/d offsets are unchanged).
        var yaml1 = "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo a\n  b:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo b\n  c:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo c\n  d:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo d\n";
        var yaml2 = yaml1.Replace("  b:\n", "  a:\n");

        var ctx = new IncrementalParseContext();
        ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml1), FilePath);
        var incremental = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        var fresh = new IncrementalParseContext().LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // Job d starts at line 15 — its diagnostics must not silently vanish.
        await Assert.That(incremental.Any(d => d.GetProperty("line").GetInt32() >= 15)).IsTrue();
        await AssertDiagnosticsEquivalent(incremental, fresh);
    }

    private static async Task AssertDiagnosticsEquivalent(System.Text.Json.JsonElement[] actual, System.Text.Json.JsonElement[] expected)
    {
        var actualOrdered = (System.Text.Json.JsonElement[])actual.Clone();
        var expectedOrdered = (System.Text.Json.JsonElement[])expected.Clone();
        Array.Sort(actualOrdered, CompareDiagnosticElements);
        Array.Sort(expectedOrdered, CompareDiagnosticElements);
        await Assert.That(actualOrdered.Length).IsEqualTo(expectedOrdered.Length);
        for (var i = 0; i < actualOrdered.Length; i++)
        {
            await Assert.That(actualOrdered[i].GetRawText()).IsEqualTo(expectedOrdered[i].GetRawText());
        }
    }

    private static int CompareDiagnosticElements(System.Text.Json.JsonElement left, System.Text.Json.JsonElement right)
    {
        var cmp = GetIntProperty(left, "line").CompareTo(GetIntProperty(right, "line"));
        if (cmp != 0) return cmp;
        cmp = GetIntProperty(left, "column").CompareTo(GetIntProperty(right, "column"));
        if (cmp != 0) return cmp;
        cmp = string.CompareOrdinal(GetStringProperty(left, "ruleId"), GetStringProperty(right, "ruleId"));
        if (cmp != 0) return cmp;
        cmp = string.CompareOrdinal(GetStringProperty(left, "message"), GetStringProperty(right, "message"));
        if (cmp != 0) return cmp;
        cmp = string.CompareOrdinal(GetStringProperty(left, "severity"), GetStringProperty(right, "severity"));
        if (cmp != 0) return cmp;
        cmp = GetBoolProperty(left, "fixable").CompareTo(GetBoolProperty(right, "fixable"));
        if (cmp != 0) return cmp;
        cmp = string.CompareOrdinal(GetStringProperty(left, "fixDescription"), GetStringProperty(right, "fixDescription"));
        if (cmp != 0) return cmp;
        // Final total-order fallback.
        return string.CompareOrdinal(left.GetRawText(), right.GetRawText());
    }

    private static int GetIntProperty(System.Text.Json.JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) ? prop.GetInt32() : 0;

    private static string GetStringProperty(System.Text.Json.JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) ? prop.GetString() ?? string.Empty : string.Empty;

    private static bool GetBoolProperty(System.Text.Json.JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) && prop.ValueKind is System.Text.Json.JsonValueKind.True;
}
