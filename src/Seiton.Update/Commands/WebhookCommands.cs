using System.Text;
using Seiton.Update;
using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Commands;

internal static class WebhookCommands
{
    public static async Task<int> Fetch(string repoRoot, bool excludeSchemaOnly = false)
    {
        var fetcher = new GitHubWebhookFetcher();
        var entry = await fetcher.FetchAsync(repoRoot, excludeSchemaOnly);

        var manifestService = new WebhookManifestService();
        var manifest = manifestService.Load(repoRoot);
        manifest = manifestService.Upsert(manifest, entry);
        manifestService.Save(repoRoot, manifest);

        UpdateLogger.Info($"[fetch:webhooks] manifest updated ({entry.ContentHash[..24]}...) excludeSchemaOnly={excludeSchemaOnly}");
        return 0;
    }

    public static int ParityCheck(string repoRoot)
    {
        var checker = new WebhookParityChecker();
        if (!checker.TryCompare(repoRoot, out var diff))
        {
            UpdateLogger.Info("[parity:webhooks] skipped (actionlint reference source not found).");
            return 0;
        }

        WriteDiffReport(repoRoot, diff, "parity");
        if (!diff.HasDifferences)
        {
            UpdateLogger.Info("[parity:webhooks] parity check passed.");
            return 0;
        }

        UpdateLogger.Error($"[parity:webhooks] differences detected. missing={diff.MissingInSeiton.Count}, extra={diff.ExtraInSeiton.Count}");
        return 4;
    }


    public static int Sync(string repoRoot)
    {
        var syncService = new WebhookSyncService();
        var changed = syncService.Sync(repoRoot);

        UpdateLogger.Info(changed
            ? "[sync:webhooks] regenerated src/Seiton.Core/Generated/WebhookTypes.g.cs"
            : "[sync:webhooks] no file changes in WebhookTypes.g.cs");

        var checker = new WebhookParityChecker();
        if (!checker.TryCompare(repoRoot, out var diff))
        {
            UpdateLogger.Info("[sync:webhooks] actionlint parity skipped (reference source not found).");
            return 0;
        }

        WriteDiffReport(repoRoot, diff, "sync");
        UpdateLogger.Info("[sync:webhooks] actionlint diff report generated.");
        if (diff.HasDifferences)
        {
            UpdateLogger.Info($"[sync:webhooks] actionlint differences detected. missing={diff.MissingInSeiton.Count}, extra={diff.ExtraInSeiton.Count}");
        }
        else
        {
            UpdateLogger.Info("[sync:webhooks] actionlint parity has no differences.");
        }

        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new WebhookSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:webhooks] generated file is stale against GitHub primary source. run sync first.");
            return 4;
        }

        UpdateLogger.Info("[verify:webhooks] generated file is up to date with GitHub primary source.");
        return 0;
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
