using Seiton.Update.Services;

namespace Seiton.Update.Commands;

internal static class ShellsCommands
{
    public static int Sync(string repoRoot)
    {
        var syncService = new ShellsSyncService();
        var changed = syncService.Sync(repoRoot);
        UpdateLogger.Info(changed
            ? "[sync:shells] regenerated src/Seiton.Core/Generated/Shells.g.cs"
            : "[sync:shells] no file changes in Shells.g.cs");
        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new ShellsSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:shells] generated file is stale. run: dotnet run --project src/Seiton.Update -- sync-shells");
            return 4;
        }

        UpdateLogger.Info("[verify:shells] generated file is up to date.");
        return 0;
    }
}
