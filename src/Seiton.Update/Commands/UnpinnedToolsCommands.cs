using Seiton.Update.Services;

namespace Seiton.Update.Commands;

internal static class UnpinnedToolsCommands
{
    public static int Sync(string repoRoot)
    {
        var syncService = new UnpinnedToolsSyncService();
        var changed = syncService.Sync(repoRoot);
        UpdateLogger.Info(changed
            ? "[sync:unpinned-tools] regenerated src/Seiton.Core/Generated/UnpinnedToolsActions.g.cs"
            : "[sync:unpinned-tools] no file changes in UnpinnedToolsActions.g.cs");
        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new UnpinnedToolsSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:unpinned-tools] generated file is stale. run: dotnet run --project src/Seiton.Update -- sync-unpinned-tools");
            return 4;
        }

        UpdateLogger.Info("[verify:unpinned-tools] generated file is up to date.");
        return 0;
    }
}
