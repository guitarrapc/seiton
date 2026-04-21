using Seiton.Update.Services;

namespace Seiton.Update.Commands;

internal static class ContextTypesCommands
{
    public static int Sync(string repoRoot)
    {
        var syncService = new ContextTypesSyncService();
        var changed = syncService.Sync(repoRoot);

        UpdateLogger.Info(changed
            ? "[sync:context-types] regenerated src/Seiton.Core/Generated/ContextTypes.g.cs"
            : "[sync:context-types] no file changes in ContextTypes.g.cs");

        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new ContextTypesSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:context-types] generated file is stale against source. run sync first.");
            return 1;
        }

        UpdateLogger.Info("[verify:context-types] generated file is up to date.");
        return 0;
    }
}
