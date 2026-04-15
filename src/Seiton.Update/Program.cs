using ConsoleAppFramework;
using Seiton.Update.Commands;
using Seiton.Update;

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

// Fetch official raw source files -> parse local files -> merge snapshot (+ manifest update).
app.Add("fetch-webhooks", async (bool excludeSchemaOnly = false) =>
{
    var code = await WebhookCommands.Fetch(repoRoot, excludeSchemaOnly);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-webhooks failed with code {code}");
    }
});

// Download and store raw official source files under data/sources/webhooks/github/raw/.
app.Add("fetch-webhooks-sources", async () =>
{
    var code = await WebhookCommands.FetchSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-webhooks-sources failed with code {code}");
    }
});

// Parse local raw files under data/sources/webhooks/github/raw/ into parsed snapshots.
app.Add("parse-webhooks-sources", () =>
{
    var code = WebhookCommands.ParseSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parse-webhooks-sources failed with code {code}");
    }
});

// Merge parsed snapshots into webhook_types.json.
app.Add("merge-webhooks-sources", (bool excludeSchemaOnly = false) =>
{
    var code = WebhookCommands.MergeSources(repoRoot, excludeSchemaOnly);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"merge-webhooks-sources failed with code {code}");
    }
});

// Compare local snapshot against actionlint reference (parity check only, no staleness check).
app.Add("parity-webhooks", () =>
{
    var code = WebhookCommands.ParityCheck(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parity-webhooks failed with code {code}");
    }
});

try
{
    await app.RunAsync(args);
    return Environment.ExitCode;
}
catch
{
    return Environment.ExitCode == 0 ? 1 : Environment.ExitCode;
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
