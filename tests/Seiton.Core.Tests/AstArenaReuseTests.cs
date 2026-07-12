using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

/// <summary>
/// Regression tests for AstArena reuse across many parses on the same thread
/// (ThreadStatic cache path). Guards the NodeTable lifecycle: counts must reset
/// per parse, and releasing a backing array must never leave a stale count behind.
/// Regression: string-id list entries accumulated across reuses until the retained-capacity
/// release nulled the backing array with a non-zero count, making the next parse throw
/// IndexOutOfRangeException inside AddStringIdList (surfaced as a fatal "yaml parse failure").
/// </summary>
public sealed class AstArenaReuseTests
{
    [Test]
    public async Task Parse_RepeatedReuseWithStringLists_StaysCorrect()
    {
        // Each parse adds a few dozen string-list entries (branches, options, labels).
        // 40 reuses would push an accumulating (buggy) shared list store past any
        // retained-capacity threshold; a correct arena resets counts every parse.
        var sb = new StringBuilder();
        sb.AppendLine("name: reuse");
        sb.AppendLine("on:");
        sb.AppendLine("  push:");
        sb.AppendLine("    branches: [main, develop, 'release/**']");
        sb.AppendLine("  workflow_dispatch:");
        sb.AppendLine("    inputs:");
        sb.AppendLine("      target:");
        sb.AppendLine("        type: choice");
        sb.AppendLine("        options: [dev, stage, prod]");
        sb.AppendLine("jobs:");
        for (var j = 0; j < 8; j++)
        {
            sb.Append("  job").Append(j).AppendLine(":");
            sb.AppendLine("    runs-on: [self-hosted, linux, x64]");
            sb.Append("    needs: [job").Append(Math.Max(0, j - 1)).AppendLine("]");
            sb.AppendLine("    steps:");
            sb.AppendLine("      - run: echo hi");
        }

        var yaml = Encoding.UTF8.GetBytes(sb.ToString().Replace("\r\n", "\n"));

        for (var i = 0; i < 40; i++)
        {
            using var result = WorkflowParser.Parse(yaml, "reuse.yml");

            await Assert.That(result.HasFatalError).IsFalse().Because($"parse #{i} must not turn fatal on arena reuse");
            await Assert.That(result.Workflow.Jobs.Count).IsEqualTo(8).Because($"parse #{i} must produce all jobs");

            // Resolve list contents through the ref facade to catch stale-range corruption.
            result.Workflow.Jobs.TryGetValue("job3"u8, out var job);
            await Assert.That(job.Needs.Count).IsEqualTo(1);
            await Assert.That(job.Needs[0].ValueEquals("job2"u8)).IsTrue();
            await Assert.That(job.RunsOn.Labels.Count).IsEqualTo(3);
            await Assert.That(job.RunsOn.Labels[1].ValueEquals("linux"u8)).IsTrue();
        }
    }

    [Test]
    public async Task Arena_DoubleDispose_DoesNotPoisonThreadCache()
    {
        // Regression: a second Dispose on an arena already sitting in the ThreadStatic
        // cache took the "cache occupied" branch, returned all backing arrays to the
        // pool and nulled them — poisoning the cached arena so the next Rent+parse on
        // the same thread crashed.
        var yaml = Encoding.UTF8.GetBytes("on: push\njobs: {}\n");

        var result = WorkflowParser.ParseDirect(yaml, "double-dispose-a.yml", out var arena);
        await Assert.That(result.HasFatalError).IsFalse();

        arena!.Dispose();
        arena.Dispose(); // double dispose must be a no-op

        // Subsequent rent + parse on the same thread must still work.
        using var result2 = WorkflowParser.Parse(yaml, "double-dispose-b.yml");
        await Assert.That(result2.HasFatalError).IsFalse();
        await Assert.That(result2.Workflow.HasValue).IsTrue();
    }

    [Test]
    public async Task NodeTable_ReleaseOversized_ClearsCountWithArray()
    {
        var table = new NodeTable<int>();
        for (var i = 0; i < 100; i++)
        {
            table.Add(i);
        }

        await Assert.That(table.Count).IsEqualTo(100);

        // Backing array (>= 100) exceeds the cap → released; count must go with it.
        table.ReleaseOversized(maxRetainedCapacity: 16);
        await Assert.That(table.Count).IsEqualTo(0);

        var index = table.Add(42);
        await Assert.That(index).IsEqualTo(0);
        await Assert.That(table[0]).IsEqualTo(42);
    }
}
