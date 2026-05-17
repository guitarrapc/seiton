using Seiton.Update.Services;

namespace Seiton.Update.Commands;

internal static class BotActorsCommands
{
    public static int Sync(string repoRoot)
    {
        var syncService = new BotActorsSyncService();
        var changed = syncService.Sync(repoRoot);
        UpdateLogger.Info(changed
            ? "[sync:bot-actors] regenerated src/Seiton.Core/Generated/BotActors.g.cs"
            : "[sync:bot-actors] no file changes in BotActors.g.cs");
        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new BotActorsSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:bot-actors] generated file is stale. run: dotnet run --project src/Seiton.Update -- sync-bot-actors");
            return 4;
        }

        UpdateLogger.Info("[verify:bot-actors] generated file is up to date.");
        return 0;
    }
}
