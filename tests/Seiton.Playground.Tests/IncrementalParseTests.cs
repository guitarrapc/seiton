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
        try
        {
            await Assert.That(result.Workflow).IsNotNull();
            await Assert.That(result.Workflow!.On).IsNotNull();
            await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
            await Assert.That(result.HasFatalError).IsFalse();
        }
        finally
        {
            result.Arena?.Dispose();
        }
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
        try
        {
            await Assert.That(result2.Workflow).IsNotNull();
            // Root sections should be valid (either reused or re-parsed)
            await Assert.That(result2.Workflow!.On.Count).IsEqualTo(1);
            await Assert.That(result2.Workflow!.Permissions).IsNotNull();
            await Assert.That(result2.Workflow!.Env).IsNotNull();
            // Jobs should be freshly parsed
            await Assert.That(result2.Workflow!.Jobs.Count).IsEqualTo(1);
        }
        finally
        {
            result2.Arena?.Dispose();
        }
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
        try
        {
            await Assert.That(result2.Workflow).IsNotNull();
            await Assert.That(result2.Workflow!.On.Count).IsEqualTo(1);
            // The event should be pull_request, not stale push
            var arena = result2.Arena!;
            var eventName = arena.GetStringValue(result2.Workflow!.On[0].EventName);
            await Assert.That(Encoding.UTF8.GetString(eventName)).IsEqualTo("pull_request");
        }
        finally
        {
            result2.Arena?.Dispose();
        }
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
        try
        {
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
        finally
        {
            result2.Arena?.Dispose();
        }
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
}
