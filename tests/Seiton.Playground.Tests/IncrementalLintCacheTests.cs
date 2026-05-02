using System.Text;

namespace Seiton.Playground.Tests;

/// <summary>
/// Tests for D-5d: Lint result cache.
/// When a job is unchanged (reused via D-5c), its lint diagnostics from the previous run
/// are reused without re-running lint rules on that job.
/// </summary>
public sealed class IncrementalLintCacheTests
{
    private const string FilePath = ".github/workflows/ci.yml";

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

        // Count diagnostics that belong to the "deploy" job (by checking message contains "deploy")
        var deployDiags1 = result1.Where(d => d.GetProperty("message").GetString()!.Contains("'deploy'")).ToArray();

        // Second call: "build" changes, "deploy" is identical (same offset + hash)
        var result2 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        var deployDiags2 = result2.Where(d => d.GetProperty("message").GetString()!.Contains("'deploy'")).ToArray();

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

        // Each diagnostic message must match (sorted by offset for determinism)
        var sortedIncremental = incrementalResult
            .OrderBy(d => d.GetProperty("line").GetInt32())
            .ThenBy(d => d.GetProperty("message").GetString())
            .ToArray();
        var sortedFresh = freshResult
            .OrderBy(d => d.GetProperty("line").GetInt32())
            .ThenBy(d => d.GetProperty("message").GetString())
            .ToArray();

        for (var i = 0; i < sortedFresh.Length; i++)
        {
            await Assert.That(sortedIncremental[i].GetProperty("message").GetString())
                .IsEqualTo(sortedFresh[i].GetProperty("message").GetString());
            await Assert.That(sortedIncremental[i].GetProperty("line").GetInt32())
                .IsEqualTo(sortedFresh[i].GetProperty("line").GetInt32());
        }
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
    public async Task LintIncrementally_SkippedJobDiagnosticsHaveCorrectOffsets()
    {
        // Verify that cached diagnostics have correct line/column (same offset = same line)
        var yaml1 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n";
        var yaml2 = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo world\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo deploy\n";

        var ctx = new IncrementalParseContext();
        var result1 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml1), FilePath);
        var result2 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // Deploy diagnostics should have same line numbers in both runs
        var deployDiags1 = result1
            .Where(d => d.GetProperty("message").GetString()!.Contains("'deploy'"))
            .OrderBy(d => d.GetProperty("line").GetInt32())
            .ToArray();
        var deployDiags2 = result2
            .Where(d => d.GetProperty("message").GetString()!.Contains("'deploy'"))
            .OrderBy(d => d.GetProperty("line").GetInt32())
            .ToArray();

        await Assert.That(deployDiags2.Length).IsEqualTo(deployDiags1.Length);
        for (var i = 0; i < deployDiags1.Length; i++)
        {
            await Assert.That(deployDiags2[i].GetProperty("line").GetInt32())
                .IsEqualTo(deployDiags1[i].GetProperty("line").GetInt32());
        }
    }
}
