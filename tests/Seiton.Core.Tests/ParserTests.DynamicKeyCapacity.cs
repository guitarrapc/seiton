using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed partial class ParserTests
{
    [Test]
    public async Task Parse_EnvDuplicateKeyBeyond64Keys_ReportsDuplicate()
    {
        // Regression: duplicate detection silently stopped recording keys once the
        // 64-entry stackalloc key store was full, so a duplicate of the 65th+ key
        // produced no diagnostic.
        var sb = new StringBuilder();
        sb.Append("on: push\nenv:\n");
        for (var i = 1; i <= 65; i++)
        {
            sb.Append("  VAR_").Append(i.ToString("D3")).Append(": v").Append(i).Append('\n');
        }

        // Duplicate of the 65th key (beyond the 64-entry stackalloc capacity)
        sb.Append("  VAR_065: dup\n");
        sb.Append("jobs: {}\n");

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(sb.ToString()), "env-overflow.yml", out _);

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("key \"VAR_065\" is duplicated in \"env\" section", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_EnvDuplicateOfEarlyKeyWithManyKeys_StillReportsDuplicate()
    {
        // Positive control: a duplicate of an early key must stay detected even when
        // the mapping has more than 64 keys in total.
        var sb = new StringBuilder();
        sb.Append("on: push\nenv:\n");
        for (var i = 1; i <= 70; i++)
        {
            sb.Append("  VAR_").Append(i.ToString("D3")).Append(": v").Append(i).Append('\n');
        }

        sb.Append("  VAR_001: dup\n");
        sb.Append("jobs: {}\n");

        var result = WorkflowParser.ParseDirect(Encoding.UTF8.GetBytes(sb.ToString()), "env-overflow-early.yml", out _);

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("key \"VAR_001\" is duplicated in \"env\" section", StringComparison.Ordinal))).IsTrue();
    }
}
