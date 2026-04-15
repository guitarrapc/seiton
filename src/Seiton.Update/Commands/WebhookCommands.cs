using System.Text;
using Seiton.Update.Services;

namespace Seiton.Update.Commands;

internal static class WebhookCommands
{
    public static int Sync(string repoRoot)
    {
        var syncService = new WebhookSyncService();
        var changed = syncService.Sync(repoRoot);

        var checker = new WebhookParityChecker();
        var diff = checker.Compare(repoRoot);
        WriteDiffReport(repoRoot, diff, "sync");

        Console.WriteLine(changed
            ? "[sync:webhooks] regenerated src/Seiton.Core/Generated/WebhookTypes.g.cs"
            : "[sync:webhooks] no file changes in WebhookTypes.g.cs");
        Console.WriteLine("[sync:webhooks] diff report generated.");
        if (diff.HasDifferences)
        {
            Console.WriteLine($"[sync:webhooks] differences detected. missing={diff.MissingInSeiton.Count}, extra={diff.ExtraInSeiton.Count}");
        }
        else
        {
            Console.WriteLine("[sync:webhooks] no differences detected.");
        }

        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new WebhookSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            Console.WriteLine("[verify:webhooks] generated file is stale. run sync first.");
            return 4;
        }

        var checker = new WebhookParityChecker();
        var diff = checker.Compare(repoRoot);
        WriteDiffReport(repoRoot, diff, "verify");

        if (!diff.HasDifferences)
        {
            Console.WriteLine("[verify:webhooks] parity check passed.");
            return 0;
        }

        Console.WriteLine($"[verify:webhooks] parity check failed. missing={diff.MissingInSeiton.Count}, extra={diff.ExtraInSeiton.Count}");
        return 4;
    }

    static void WriteDiffReport(string repoRoot, Model.WebhookDiffResult diff, string mode)
    {
        var reportDir = Path.Combine(repoRoot, "data", "sources", "reports");
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir, "actionlint-diff-webhooks.md");

        var sb = new StringBuilder();
        sb.AppendLine("# actionlint Diff Report: webhooks");
        sb.AppendLine();
        sb.AppendLine($"- mode: {mode}");
        sb.AppendLine($"- generated-at-utc: {DateTime.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("## Missing In Seiton");
        if (diff.MissingInSeiton.Count == 0)
        {
            sb.AppendLine("- none");
        }
        else
        {
            foreach (var item in diff.MissingInSeiton)
            {
                sb.AppendLine($"- {item}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Extra In Seiton");
        if (diff.ExtraInSeiton.Count == 0)
        {
            sb.AppendLine("- none");
        }
        else
        {
            foreach (var item in diff.ExtraInSeiton)
            {
                sb.AppendLine($"- {item}");
            }
        }

        File.WriteAllText(reportPath, sb.ToString().Replace("\r\n", "\n"));
    }
}
