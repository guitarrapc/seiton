using Seiton.Update.Services;

namespace Seiton.Update.Commands;

internal static class PermissionsCommands
{
    public static int Sync(string repoRoot)
    {
        var syncService = new PermissionsSyncService();
        var changed = syncService.Sync(repoRoot);
        UpdateLogger.Info(changed
            ? "[sync:permissions] regenerated src/Seiton.Core/Generated/PermissionScopes.g.cs"
            : "[sync:permissions] no file changes in PermissionScopes.g.cs");
        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new PermissionsSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:permissions] generated file is stale. run: dotnet run --project src/Seiton.Update -- sync-permissions");
            return 4;
        }

        UpdateLogger.Info("[verify:permissions] generated file is up to date.");
        return 0;
    }
}
