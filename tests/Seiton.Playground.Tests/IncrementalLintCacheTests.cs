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

    [Test]
    public async Task LintIncrementally_RemoveLineFromLastJob_DiagnosticsDoNotDecrease()
    {
        // Regression: removing a line from the last job should not cause diagnostics for earlier jobs to vanish.
        // The earlier job (test) is at the same offset and hash → reusable. Its cached diagnostics
        // must be merged correctly with the fresh lint of the modified job (foo).
        var yaml1 = """
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo test
              foo:
                permissions: {}
                needs: [test]
                runs-on: ubuntu-24.04
                timeout-minutes: 15
                steps:
                  - name: foobar
                    run: echo hello world
                  - name: piyopiyo
                    run: echo piyoyoooo
            """.Replace("            ", "");

        // Remove "permissions: {}" line from foo
        var yaml2 = """
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo test
              foo:
                needs: [test]
                runs-on: ubuntu-24.04
                timeout-minutes: 15
                steps:
                  - name: foobar
                    run: echo hello world
                  - name: piyopiyo
                    run: echo piyoyoooo
            """.Replace("            ", "");

        var ctx = new IncrementalParseContext();
        // Call 1: establishes cache
        var result1 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml1), FilePath);
        // Call 2: remove permissions line from foo → foo re-parsed, test reused from cache
        var result2 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // Fresh lint (no cache) for yaml2
        var freshCtx = new IncrementalParseContext();
        var freshResult = freshCtx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // Incremental result must match fresh result exactly
        await Assert.That(result2.Length).IsEqualTo(freshResult.Length);

        var sortedIncremental = result2
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
    public async Task LintIncrementally_RemoveTimeoutFromLastJob_DiagnosticsDoNotDecrease()
    {
        // Regression: removing timeout-minutes from the last job should not affect earlier job diagnostics.
        var yaml1 = """
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo test
              foo:
                permissions: {}
                needs: [test]
                runs-on: ubuntu-24.04
                timeout-minutes: 15
                steps:
                  - name: foobar
                    run: echo hello world
                  - name: piyopiyo
                    run: echo piyoyoooo
            """.Replace("            ", "");

        // Remove "timeout-minutes: 15" line from foo
        var yaml2 = """
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo test
              foo:
                permissions: {}
                needs: [test]
                runs-on: ubuntu-24.04
                steps:
                  - name: foobar
                    run: echo hello world
                  - name: piyopiyo
                    run: echo piyoyoooo
            """.Replace("            ", "");

        var ctx = new IncrementalParseContext();
        var result1 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml1), FilePath);
        var result2 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // Fresh lint for yaml2
        var freshCtx = new IncrementalParseContext();
        var freshResult = freshCtx.LintIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);

        // Incremental result must match fresh result exactly
        await Assert.That(result2.Length).IsEqualTo(freshResult.Length);

        var sortedIncremental = result2
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
    public async Task LintIncrementally_ThreeCallSequence_AddJobThenRemoveLine_DiagnosticsDoNotDecrease()
    {
        // Reproduce the user's exact scenario:
        // 1. Start with default workflow (just test job)
        // 2. Add foo job → correct
        // 3. Remove permissions line from foo → should still be correct
        var yamlDefault = """
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - run: echo test
            """.Replace("            ", "");

        var yamlWithFoo = """
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - run: echo test
              foo:
                permissions: {}
                needs: [test]
                runs-on: ubuntu-24.04
                timeout-minutes: 15
                steps:
                  - name: foobar
                    run: echo hello world
                  - name: piyopiyo
                    run: echo piyoyoooo
            """.Replace("            ", "");

        var yamlWithFooNoPerms = """
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - run: echo test
              foo:
                needs: [test]
                runs-on: ubuntu-24.04
                timeout-minutes: 15
                steps:
                  - name: foobar
                    run: echo hello world
                  - name: piyopiyo
                    run: echo piyoyoooo
            """.Replace("            ", "");

        var ctx = new IncrementalParseContext();
        // Call 1: default (establishes first state)
        ctx.LintIncrementally(Encoding.UTF8.GetBytes(yamlDefault), FilePath);
        // Call 2: add foo job (job count changes → full re-parse)
        ctx.LintIncrementally(Encoding.UTF8.GetBytes(yamlWithFoo), FilePath);
        // Call 3: remove permissions from foo (foo changes, test reused)
        var result3 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yamlWithFooNoPerms), FilePath);

        // Fresh lint for the final YAML
        var freshCtx = new IncrementalParseContext();
        var freshResult = freshCtx.LintIncrementally(Encoding.UTF8.GetBytes(yamlWithFooNoPerms), FilePath);

        // Incremental result must match fresh result
        await Assert.That(result3.Length).IsEqualTo(freshResult.Length);

        var sortedIncremental = result3
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
    public async Task LintIncrementally_PlaygroundDefault_AddJobThenRemoveLine_DiagnosticsDoNotDecrease()
    {
        // Reproduce using the actual Playground default content
        var yamlDefault = "# Paste your workflow YAML to this code editor\n\non:\n  push:\n    branch: main\n    tags:\n      - 'v\\\\d+'\njobs:\n  test:\n    strategy:\n      matrix:\n        os: [macos-latest, linux-latest]\n    runs-on: ${{ matrix.os }}\n    steps:\n      - run: echo \"Checking commit '${{ github.event.head_commit.message }}'\"\n      - uses: actions/checkout@v4\n      - uses: actions/setup-node@v4\n        with:\n          node_version: 18.x\n      - uses: actions/cache@v4\n        with:\n          path: ~/.npm\n          key: ${{ matrix.platform }}-node-${{ hashFiles('**/package-lock.json') }}\n        if: ${{ github.repository.permissions.admin == true }}\n      - run: npm install && npm test\n";

        var yamlWithFoo = yamlDefault +
            "  foo:\n    permissions: {}\n    needs: [test]\n    runs-on: ubuntu-24.04\n    timeout-minutes: 15\n    steps:\n      - name: foobar\n        run: echo hello world\n      - name: piyopiyo\n        run: echo piyoyoooo\n";

        var yamlWithFooNoPerms = yamlDefault +
            "  foo:\n    needs: [test]\n    runs-on: ubuntu-24.04\n    timeout-minutes: 15\n    steps:\n      - name: foobar\n        run: echo hello world\n      - name: piyopiyo\n        run: echo piyoyoooo\n";

        var ctx = new IncrementalParseContext();
        // Call 1: Playground default
        ctx.LintIncrementally(Encoding.UTF8.GetBytes(yamlDefault), FilePath);
        // Call 2: add foo job
        var result2 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yamlWithFoo), FilePath);
        // Call 3: remove permissions from foo
        var result3 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yamlWithFooNoPerms), FilePath);

        // Fresh lint for the final YAML
        var freshCtx = new IncrementalParseContext();
        var freshResult = freshCtx.LintIncrementally(Encoding.UTF8.GetBytes(yamlWithFooNoPerms), FilePath);

        // Incremental result must match fresh result
        await Assert.That(result3.Length).IsEqualTo(freshResult.Length)
            .Because($"Incremental: {result3.Length} diagnostics, Fresh: {freshResult.Length} diagnostics");

        var sortedIncremental = result3
            .OrderBy(d => d.GetProperty("line").GetInt32())
            .ThenBy(d => d.GetProperty("message").GetString())
            .ToArray();
        var sortedFresh = freshResult
            .OrderBy(d => d.GetProperty("line").GetInt32())
            .ThenBy(d => d.GetProperty("message").GetString())
            .ToArray();

        for (var i = 0; i < Math.Min(sortedFresh.Length, sortedIncremental.Length); i++)
        {
            await Assert.That(sortedIncremental[i].GetProperty("message").GetString())
                .IsEqualTo(sortedFresh[i].GetProperty("message").GetString());
            await Assert.That(sortedIncremental[i].GetProperty("line").GetInt32())
                .IsEqualTo(sortedFresh[i].GetProperty("line").GetInt32());
        }
    }

    [Test]
    public async Task LintIncrementally_SixStepSequence_ParseDiagnosticsPreservedThroughout()
    {
        // Regression: Root section parse diagnostics (like "on.push does not support option: branch")
        // must be preserved through all incremental steps, not lost when the section bytes are unchanged.
        // This tests the full user scenario: default → add foo → remove perms → undo → remove perms → remove foo.
        var yamlDefault = "# Paste your workflow YAML to this code editor\n\non:\n  push:\n    branch: main\n    tags:\n      - 'v\\\\d+'\njobs:\n  test:\n    strategy:\n      matrix:\n        os: [macos-latest, linux-latest]\n    runs-on: ${{ matrix.os }}\n    steps:\n      - run: echo \"Checking commit '${{ github.event.head_commit.message }}'\"\n      - uses: actions/checkout@v4\n      - uses: actions/setup-node@v4\n        with:\n          node_version: 18.x\n      - uses: actions/cache@v4\n        with:\n          path: ~/.npm\n          key: ${{ matrix.platform }}-node-${{ hashFiles('**/package-lock.json') }}\n        if: ${{ github.repository.permissions.admin == true }}\n      - run: npm install && npm test\n";

        var yamlWithFoo = yamlDefault +
            "  foo:\n    permissions: {}\n    needs: [test]\n    runs-on: ubuntu-24.04\n    timeout-minutes: 15\n    steps:\n      - name: foobar\n        run: echo hello world\n      - name: piyopiyo\n        run: echo piyoyoooo\n";

        var yamlWithFooNoPerms = "# Paste your workflow YAML to this code editor\n\non:\n  push:\n    branch: main\n    tags:\n      - 'v\\\\d+'\njobs:\n  test:\n    strategy:\n      matrix:\n        os: [macos-latest, linux-latest]\n    runs-on: ${{ matrix.os }}\n    steps:\n      - run: echo \"Checking commit '${{ github.event.head_commit.message }}'\"\n      - uses: actions/checkout@v4\n      - uses: actions/setup-node@v4\n        with:\n          node_version: 18.x\n      - uses: actions/cache@v4\n        with:\n          path: ~/.npm\n          key: ${{ matrix.platform }}-node-${{ hashFiles('**/package-lock.json') }}\n        if: ${{ github.repository.permissions.admin == true }}\n      - run: npm install && npm test\n\n  foo:\n    needs: [test]\n    runs-on: ubuntu-24.04\n    timeout-minutes: 15\n    steps:\n      - name: foobar\n        run: echo hello world\n      - name: piyopiyo\n        run: echo piyoyoooo\n";

        var ctx = new IncrementalParseContext();

        // Step 1: Default
        var r1 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yamlDefault), FilePath);
        // Step 2: Add foo
        var r2 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yamlWithFoo), FilePath);
        // Step 3: Remove permissions
        var r3 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yamlWithFooNoPerms), FilePath);
        // Step 4: Undo (restore permissions)
        var r4 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yamlWithFoo), FilePath);
        // Step 5: Remove permissions again
        var r5 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yamlWithFooNoPerms), FilePath);
        // Step 6: Remove foo
        var r6 = ctx.LintIncrementally(Encoding.UTF8.GetBytes(yamlDefault), FilePath);

        // Fresh comparisons
        var freshDefault = new IncrementalParseContext().LintIncrementally(Encoding.UTF8.GetBytes(yamlDefault), FilePath);
        var freshWithFoo = new IncrementalParseContext().LintIncrementally(Encoding.UTF8.GetBytes(yamlWithFoo), FilePath);
        var freshNoPerms = new IncrementalParseContext().LintIncrementally(Encoding.UTF8.GetBytes(yamlWithFooNoPerms), FilePath);

        // Every step must match its corresponding fresh result
        await Assert.That(r1.Length).IsEqualTo(freshDefault.Length).Because("Step 1 must match fresh default");
        await Assert.That(r2.Length).IsEqualTo(freshWithFoo.Length).Because("Step 2 must match fresh with foo");
        await Assert.That(r3.Length).IsEqualTo(freshNoPerms.Length).Because("Step 3 must match fresh no perms");
        await Assert.That(r4.Length).IsEqualTo(freshWithFoo.Length).Because("Step 4 must match fresh with foo");
        await Assert.That(r5.Length).IsEqualTo(freshNoPerms.Length).Because("Step 5 must match fresh no perms");
        await Assert.That(r6.Length).IsEqualTo(freshDefault.Length).Because("Step 6 must match fresh default");

        // Specifically verify the "on.push branch" parse diagnostic is never lost
        static bool HasBranchDiag(System.Text.Json.JsonElement[] results) =>
            results.Any(d => d.GetProperty("message").GetString()!.Contains("on.push does not support option: branch"));

        await Assert.That(HasBranchDiag(r1)).IsTrue().Because("Step 1: on.push branch diagnostic");
        await Assert.That(HasBranchDiag(r2)).IsTrue().Because("Step 2: on.push branch diagnostic");
        await Assert.That(HasBranchDiag(r3)).IsTrue().Because("Step 3: on.push branch diagnostic");
        await Assert.That(HasBranchDiag(r4)).IsTrue().Because("Step 4: on.push branch diagnostic");
        await Assert.That(HasBranchDiag(r5)).IsTrue().Because("Step 5: on.push branch diagnostic");
        await Assert.That(HasBranchDiag(r6)).IsTrue().Because("Step 6: on.push branch diagnostic");
    }

    [Test]
    public async Task ParseIncrementally_RootSectionDiagnosticsPreserved_WhenSectionBytesUnchanged()
    {
        // Regression: ParseIncrementally must NOT skip root sections that had parse diagnostics.
        // The "on:" section with "branch: main" (should be "branches") produces a diagnostic.
        // When a job is appended (on: section unchanged), the diagnostic must still appear.
        var yaml1 = "on:\n  push:\n    branch: main\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo test\n";
        var yaml2 = "on:\n  push:\n    branch: main\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo test\n  foo:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo foo\n";

        var ctx = new IncrementalParseContext();
        // First parse: on: section has diagnostic
        var r1 = ctx.ParseIncrementally(Encoding.UTF8.GetBytes(yaml1), FilePath);
        var hasDiag1 = r1.Diagnostics.Any(d => d.Message.Contains("branch"));
        await Assert.That(hasDiag1).IsTrue().Because("First parse must have 'branch' diagnostic");

        // Second parse: on: section bytes unchanged, but foo job added → incremental path
        var r2 = ctx.ParseIncrementally(Encoding.UTF8.GetBytes(yaml2), FilePath);
        var hasDiag2 = r2.Diagnostics.Any(d => d.Message.Contains("branch"));
        await Assert.That(hasDiag2).IsTrue().Because("Incremental parse must preserve 'branch' diagnostic when on: section is unchanged");
    }
}
