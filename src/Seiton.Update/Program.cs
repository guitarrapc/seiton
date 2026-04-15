using ConsoleAppFramework;
using Seiton.Update.Commands;

namespace Seiton.Update;

internal static class Program
{
    static int Main(string[] args)
    {
        var repoRoot = FindRepoRoot(Environment.CurrentDirectory);
        if (repoRoot is null)
        {
            UpdateLogger.Error("Repository root not found (seiton.slnx).");
            return 1;
        }

        var app = ConsoleApp.Create();

        app.Add("sync", (string dataset = "all") =>
        {
            var code = RunSync(repoRoot, dataset);
            if (code != 0)
            {
                Environment.ExitCode = code;
                throw new InvalidOperationException($"sync failed with code {code}");
            }
        });

        app.Add("verify", (string dataset = "all") =>
        {
            var code = RunVerify(repoRoot, dataset);
            if (code != 0)
            {
                Environment.ExitCode = code;
                throw new InvalidOperationException($"verify failed with code {code}");
            }
        });

        // Convenience aliases to avoid option handling for the most common workflow.
        app.Add("sync-webhooks", () =>
        {
            var code = WebhookCommands.Sync(repoRoot);
            if (code != 0)
            {
                Environment.ExitCode = code;
                throw new InvalidOperationException($"sync-webhooks failed with code {code}");
            }
        });

        app.Add("verify-webhooks", () =>
        {
            var code = WebhookCommands.Verify(repoRoot);
            if (code != 0)
            {
                Environment.ExitCode = code;
                throw new InvalidOperationException($"verify-webhooks failed with code {code}");
            }
        });

        try
        {
            app.Run(args);
            return Environment.ExitCode;
        }
        catch
        {
            return Environment.ExitCode == 0 ? 1 : Environment.ExitCode;
        }
    }

    static int RunSync(string repoRoot, string dataset)
    {
        if (dataset is "all" or "webhooks")
        {
            return WebhookCommands.Sync(repoRoot);
        }

        UpdateLogger.Error($"Unsupported sync dataset: {dataset}");
        return 1;
    }

    static int RunVerify(string repoRoot, string dataset)
    {
        if (dataset is "all" or "webhooks")
        {
            return WebhookCommands.Verify(repoRoot);
        }

        UpdateLogger.Error($"Unsupported verify dataset: {dataset}");
        return 1;
    }

    static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var marker = Path.Combine(dir.FullName, "seiton.slnx");
            if (File.Exists(marker))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
