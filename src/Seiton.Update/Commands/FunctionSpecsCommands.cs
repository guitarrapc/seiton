using Seiton.Update.Services;

namespace Seiton.Update.Commands;

internal static class FunctionSpecsCommands
{
    public static int Sync(string repoRoot)
    {
        var syncService = new FunctionSpecsSyncService();
        var changed = syncService.Sync(repoRoot);

        UpdateLogger.Info(changed
            ? "[sync:function-specs] regenerated src/Seiton.Core/Generated/FunctionSpecs.g.cs"
            : "[sync:function-specs] no file changes in FunctionSpecs.g.cs");

        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new FunctionSpecsSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:function-specs] generated file is stale against source. run sync first.");
            return 1;
        }

        UpdateLogger.Info("[verify:function-specs] generated file is up to date.");
        return 0;
    }
}
