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

app.Add("fetch", async (string dataset = "all", bool excludeSchemaOnly = false) =>
{
    var code = await RunFetch(repoRoot, dataset, excludeSchemaOnly);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch failed with code {code}");
    }
});

// Orchestrator: full upstream refresh + codegen + verify gate (same dataset order as sync/verify all).
app.Add("update", async (bool excludeSchemaOnly = false) =>
{
    var fetchCode = await RunFetch(repoRoot, "all", excludeSchemaOnly);
    if (fetchCode != 0)
    {
        Environment.ExitCode = fetchCode;
        throw new InvalidOperationException($"update failed at fetch with code {fetchCode}");
    }

    var syncCode = RunSync(repoRoot, "all");
    if (syncCode != 0)
    {
        Environment.ExitCode = syncCode;
        throw new InvalidOperationException($"update failed at sync with code {syncCode}");
    }

    var verifyCode = RunVerify(repoRoot, "all");
    if (verifyCode != 0)
    {
        Environment.ExitCode = verifyCode;
        throw new InvalidOperationException($"update failed at verify with code {verifyCode}");
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

app.Add("sync-availability", () =>
{
    var code = AvailabilityCommands.Sync(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"sync-availability failed with code {code}");
    }
});

app.Add("verify-availability", () =>
{
    var code = AvailabilityCommands.Verify(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"verify-availability failed with code {code}");
    }
});

app.Add("sync-popular-actions", () =>
{
    var code = PopularActionsCommands.Sync(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"sync-popular-actions failed with code {code}");
    }
});

app.Add("verify-popular-actions", () =>
{
    var code = PopularActionsCommands.Verify(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"verify-popular-actions failed with code {code}");
    }
});

app.Add("sync-runner-labels", () =>
{
    var code = RunnerLabelsCommands.Sync(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"sync-runner-labels failed with code {code}");
    }
});

app.Add("verify-runner-labels", () =>
{
    var code = RunnerLabelsCommands.Verify(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"verify-runner-labels failed with code {code}");
    }
});

app.Add("sync-context-types", () =>
{
    var code = ContextTypesCommands.Sync(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"sync-context-types failed with code {code}");
    }
});

app.Add("verify-context-types", () =>
{
    var code = ContextTypesCommands.Verify(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"verify-context-types failed with code {code}");
    }
});

app.Add("fetch-event-payload-types", async () =>
{
    var code = await EventPayloadTypesCommands.Fetch(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-event-payload-types failed with code {code}");
    }
});

app.Add("fetch-event-payload-types-sources", async () =>
{
    var code = await EventPayloadTypesCommands.FetchSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-event-payload-types-sources failed with code {code}");
    }
});

app.Add("parse-event-payload-types-sources", () =>
{
    var code = EventPayloadTypesCommands.ParseSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parse-event-payload-types-sources failed with code {code}");
    }
});

app.Add("sync-event-payload-types", () =>
{
    var code = EventPayloadTypesCommands.Sync(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"sync-event-payload-types failed with code {code}");
    }
});

app.Add("verify-event-payload-types", () =>
{
    var code = EventPayloadTypesCommands.Verify(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"verify-event-payload-types failed with code {code}");
    }
});

app.Add("sync-function-specs", () =>
{
    var code = FunctionSpecsCommands.Sync(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"sync-function-specs failed with code {code}");
    }
});

app.Add("verify-function-specs", () =>
{
    var code = FunctionSpecsCommands.Verify(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"verify-function-specs failed with code {code}");
    }
});

app.Add("sync-permissions", () =>
{
    var code = PermissionsCommands.Sync(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"sync-permissions failed with code {code}");
    }
});

app.Add("verify-permissions", () =>
{
    var code = PermissionsCommands.Verify(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"verify-permissions failed with code {code}");
    }
});

app.Add("fetch-permissions", async () =>
{
    var code = await PermissionsCommands.Fetch(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-permissions failed with code {code}");
    }
});

app.Add("fetch-permissions-sources", async () =>
{
    var code = await PermissionsCommands.FetchSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-permissions-sources failed with code {code}");
    }
});

app.Add("parse-permissions-sources", () =>
{
    var code = PermissionsCommands.ParseSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parse-permissions-sources failed with code {code}");
    }
});

app.Add("merge-permissions-sources", () =>
{
    var code = PermissionsCommands.MergeSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"merge-permissions-sources failed with code {code}");
    }
});

app.Add("fetch-function-specs", async () =>
{
    var code = await FunctionSpecsCommands.Fetch(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-function-specs failed with code {code}");
    }
});

app.Add("fetch-function-specs-sources", async () =>
{
    var code = await FunctionSpecsCommands.FetchSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-function-specs-sources failed with code {code}");
    }
});

app.Add("parse-function-specs-sources", () =>
{
    var code = FunctionSpecsCommands.ParseSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parse-function-specs-sources failed with code {code}");
    }
});

app.Add("validate-function-specs", () =>
{
    var code = FunctionSpecsCommands.Validate(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"validate-function-specs failed with code {code}");
    }
});

app.Add("fetch-context-types", async () =>
{
    var code = await ContextTypesCommands.Fetch(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-context-types failed with code {code}");
    }
});

app.Add("fetch-context-types-sources", async () =>
{
    var code = await ContextTypesCommands.FetchSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-context-types-sources failed with code {code}");
    }
});

app.Add("parse-context-types-sources", () =>
{
    var code = ContextTypesCommands.ParseSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parse-context-types-sources failed with code {code}");
    }
});

app.Add("merge-context-types-sources", () =>
{
    var code = ContextTypesCommands.MergeSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"merge-context-types-sources failed with code {code}");
    }
});

app.Add("validate-context-types", () =>
{
    var code = ContextTypesCommands.Validate(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"validate-context-types failed with code {code}");
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

app.Add("fetch-availability", async () =>
{
    var code = await AvailabilityCommands.Fetch(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-availability failed with code {code}");
    }
});

app.Add("fetch-availability-sources", async () =>
{
    var code = await AvailabilityCommands.FetchSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-availability-sources failed with code {code}");
    }
});

app.Add("parse-availability-sources", () =>
{
    var code = AvailabilityCommands.ParseSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parse-availability-sources failed with code {code}");
    }
});

app.Add("merge-availability-sources", () =>
{
    var code = AvailabilityCommands.MergeSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"merge-availability-sources failed with code {code}");
    }
});

app.Add("fetch-popular-actions", async () =>
{
    var code = await PopularActionsCommands.Fetch(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-popular-actions failed with code {code}");
    }
});

app.Add("validate-popular-actions-targets", () =>
{
    var code = PopularActionsCommands.ValidateTargets(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"validate-popular-actions-targets failed with code {code}");
    }
});

app.Add("validate-popular-actions-versions", async () =>
{
    var code = await PopularActionsCommands.ValidateVersions(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"validate-popular-actions-versions failed with code {code}");
    }
});

app.Add("fetch-popular-actions-sources", async () =>
{
    var code = await PopularActionsCommands.FetchSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-popular-actions-sources failed with code {code}");
    }
});

app.Add("parse-popular-actions-sources", () =>
{
    var code = PopularActionsCommands.ParseSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parse-popular-actions-sources failed with code {code}");
    }
});

app.Add("merge-popular-actions-sources", () =>
{
    var code = PopularActionsCommands.MergeSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"merge-popular-actions-sources failed with code {code}");
    }
});

app.Add("sync-iana-timezones", () =>
{
    var code = IanaTimeZonesCommands.Sync(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"sync-iana-timezones failed with code {code}");
    }
});

app.Add("verify-iana-timezones", () =>
{
    var code = IanaTimeZonesCommands.Verify(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"verify-iana-timezones failed with code {code}");
    }
});

app.Add("sync-shells", () =>
{
    var code = ShellsCommands.Sync(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"sync-shells failed with code {code}");
    }
});

app.Add("verify-shells", () =>
{
    var code = ShellsCommands.Verify(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"verify-shells failed with code {code}");
    }
});

app.Add("fetch-shells", async () =>
{
    var code = await ShellsCommands.Fetch(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-shells failed with code {code}");
    }
});

app.Add("fetch-shells-sources", async () =>
{
    var code = await ShellsCommands.FetchSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-shells-sources failed with code {code}");
    }
});

app.Add("parse-shells-sources", () =>
{
    var code = ShellsCommands.ParseSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parse-shells-sources failed with code {code}");
    }
});

app.Add("merge-shells-sources", () =>
{
    var code = ShellsCommands.MergeSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"merge-shells-sources failed with code {code}");
    }
});

app.Add("fetch-unpinned-tools", async () =>
{
    var code = await UnpinnedToolsCommands.Fetch(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-unpinned-tools failed with code {code}");
    }
});

app.Add("fetch-unpinned-tools-sources", async () =>
{
    var code = await UnpinnedToolsCommands.FetchSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-unpinned-tools-sources failed with code {code}");
    }
});

app.Add("parse-unpinned-tools-sources", () =>
{
    var code = UnpinnedToolsCommands.ParseSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parse-unpinned-tools-sources failed with code {code}");
    }
});

app.Add("merge-unpinned-tools-sources", () =>
{
    var code = UnpinnedToolsCommands.MergeSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"merge-unpinned-tools-sources failed with code {code}");
    }
});

app.Add("sync-unpinned-tools", () =>
{
    var code = UnpinnedToolsCommands.Sync(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"sync-unpinned-tools failed with code {code}");
    }
});

app.Add("verify-unpinned-tools", () =>
{
    var code = UnpinnedToolsCommands.Verify(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"verify-unpinned-tools failed with code {code}");
    }
});

app.Add("sync-bot-actors", () =>
{
    var code = BotActorsCommands.Sync(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"sync-bot-actors failed with code {code}");
    }
});

app.Add("verify-bot-actors", () =>
{
    var code = BotActorsCommands.Verify(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"verify-bot-actors failed with code {code}");
    }
});

app.Add("fetch-superfluous-actions", async () =>
{
    var code = await SuperfluousActionsCommands.Fetch(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-superfluous-actions failed with code {code}");
    }
});

app.Add("fetch-superfluous-actions-sources", async () =>
{
    var code = await SuperfluousActionsCommands.FetchSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-superfluous-actions-sources failed with code {code}");
    }
});

app.Add("parse-superfluous-actions-sources", () =>
{
    var code = SuperfluousActionsCommands.ParseSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parse-superfluous-actions-sources failed with code {code}");
    }
});

app.Add("merge-superfluous-actions-sources", () =>
{
    var code = SuperfluousActionsCommands.MergeSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"merge-superfluous-actions-sources failed with code {code}");
    }
});

app.Add("sync-superfluous-actions", () =>
{
    var code = SuperfluousActionsCommands.Sync(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"sync-superfluous-actions failed with code {code}");
    }
});

app.Add("verify-superfluous-actions", () =>
{
    var code = SuperfluousActionsCommands.Verify(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"verify-superfluous-actions failed with code {code}");
    }
});

app.Add("fetch-expected-keys", async () =>
{
    var code = await ExpectedKeysCommands.Fetch(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-expected-keys failed with code {code}");
    }
});

app.Add("fetch-expected-keys-sources", async () =>
{
    var code = await ExpectedKeysCommands.FetchSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-expected-keys-sources failed with code {code}");
    }
});

app.Add("parse-expected-keys-sources", () =>
{
    var code = ExpectedKeysCommands.ParseSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parse-expected-keys-sources failed with code {code}");
    }
});

app.Add("merge-expected-keys-sources", () =>
{
    var code = ExpectedKeysCommands.MergeSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"merge-expected-keys-sources failed with code {code}");
    }
});

app.Add("sync-expected-keys", () =>
{
    var code = ExpectedKeysCommands.Sync(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"sync-expected-keys failed with code {code}");
    }
});

app.Add("verify-expected-keys", () =>
{
    var code = ExpectedKeysCommands.Verify(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"verify-expected-keys failed with code {code}");
    }
});

app.Add("fetch-iana-timezones", async () =>
{
    var code = await IanaTimeZonesCommands.Fetch(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-iana-timezones failed with code {code}");
    }
});

app.Add("fetch-iana-timezones-sources", async () =>
{
    var code = await IanaTimeZonesCommands.FetchSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-iana-timezones-sources failed with code {code}");
    }
});

app.Add("parse-iana-timezones-sources", () =>
{
    var code = IanaTimeZonesCommands.ParseSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parse-iana-timezones-sources failed with code {code}");
    }
});

app.Add("merge-iana-timezones-sources", () =>
{
    var code = IanaTimeZonesCommands.MergeSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"merge-iana-timezones-sources failed with code {code}");
    }
});

app.Add("fetch-runner-labels", async () =>
{
    var code = await RunnerLabelsCommands.Fetch(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-runner-labels failed with code {code}");
    }
});

app.Add("fetch-runner-labels-sources", async () =>
{
    var code = await RunnerLabelsCommands.FetchSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"fetch-runner-labels-sources failed with code {code}");
    }
});

app.Add("parse-runner-labels-sources", () =>
{
    var code = RunnerLabelsCommands.ParseSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"parse-runner-labels-sources failed with code {code}");
    }
});

app.Add("merge-runner-labels-sources", () =>
{
    var code = RunnerLabelsCommands.MergeSources(repoRoot);
    if (code != 0)
    {
        Environment.ExitCode = code;
        throw new InvalidOperationException($"merge-runner-labels-sources failed with code {code}");
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

static async Task<int> RunFetch(string repoRoot, string dataset, bool webhooksExcludeSchemaOnly)
{
    if (dataset is "webhooks")
    {
        return await WebhookCommands.Fetch(repoRoot, webhooksExcludeSchemaOnly);
    }

    if (dataset is "availability")
    {
        return await AvailabilityCommands.Fetch(repoRoot);
    }

    if (dataset is "popular-actions")
    {
        return await PopularActionsCommands.Fetch(repoRoot);
    }

    if (dataset is "runner-labels")
    {
        return await RunnerLabelsCommands.Fetch(repoRoot);
    }

    if (dataset is "context-types")
    {
        return await ContextTypesCommands.Fetch(repoRoot);
    }

    if (dataset is "function-specs")
    {
        return await FunctionSpecsCommands.Fetch(repoRoot);
    }

    if (dataset is "permissions")
    {
        return await PermissionsCommands.Fetch(repoRoot);
    }

    if (dataset is "iana-timezones")
    {
        return await IanaTimeZonesCommands.Fetch(repoRoot);
    }

    if (dataset is "shells")
    {
        return await ShellsCommands.Fetch(repoRoot);
    }

    if (dataset is "expected-keys")
    {
        return await ExpectedKeysCommands.Fetch(repoRoot);
    }

    if (dataset is "unpinned-tools")
    {
        return await UnpinnedToolsCommands.Fetch(repoRoot);
    }

    if (dataset is "event-payload-types")
    {
        return await EventPayloadTypesCommands.Fetch(repoRoot);
    }

    if (dataset is "superfluous-actions")
    {
        return await SuperfluousActionsCommands.Fetch(repoRoot);
    }

    if (dataset is "all")
    {
        var code = await WebhookCommands.Fetch(repoRoot, webhooksExcludeSchemaOnly);
        if (code != 0)
        {
            return code;
        }

        code = await AvailabilityCommands.Fetch(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = await PopularActionsCommands.Fetch(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = await RunnerLabelsCommands.Fetch(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = await ContextTypesCommands.Fetch(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = await FunctionSpecsCommands.Fetch(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = await PermissionsCommands.Fetch(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = await IanaTimeZonesCommands.Fetch(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = await ShellsCommands.Fetch(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = await ExpectedKeysCommands.Fetch(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = await UnpinnedToolsCommands.Fetch(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = await SuperfluousActionsCommands.Fetch(repoRoot);
        if (code != 0)
        {
            return code;
        }

        return await EventPayloadTypesCommands.Fetch(repoRoot);
    }

    UpdateLogger.Error($"Unsupported fetch dataset: {dataset}");
    return 1;
}

static int RunSync(string repoRoot, string dataset)
{
    if (dataset is "webhooks")
    {
        return WebhookCommands.Sync(repoRoot);
    }

    if (dataset is "availability")
    {
        return AvailabilityCommands.Sync(repoRoot);
    }

    if (dataset is "popular-actions")
    {
        return PopularActionsCommands.Sync(repoRoot);
    }

    if (dataset is "runner-labels")
    {
        return RunnerLabelsCommands.Sync(repoRoot);
    }

    if (dataset is "context-types")
    {
        return ContextTypesCommands.Sync(repoRoot);
    }

    if (dataset is "function-specs")
    {
        return FunctionSpecsCommands.Sync(repoRoot);
    }

    if (dataset is "permissions")
    {
        return PermissionsCommands.Sync(repoRoot);
    }

    if (dataset is "iana-timezones")
    {
        return IanaTimeZonesCommands.Sync(repoRoot);
    }

    if (dataset is "shells")
    {
        return ShellsCommands.Sync(repoRoot);
    }

    if (dataset is "expected-keys")
    {
        return ExpectedKeysCommands.Sync(repoRoot);
    }

    if (dataset is "unpinned-tools")
    {
        return UnpinnedToolsCommands.Sync(repoRoot);
    }

    if (dataset is "bot-actors")
    {
        return BotActorsCommands.Sync(repoRoot);
    }

    if (dataset is "superfluous-actions")
    {
        return SuperfluousActionsCommands.Sync(repoRoot);
    }

    if (dataset is "all")
    {
        var code = WebhookCommands.Sync(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = AvailabilityCommands.Sync(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = PopularActionsCommands.Sync(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = RunnerLabelsCommands.Sync(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = ContextTypesCommands.Sync(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = FunctionSpecsCommands.Sync(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = PermissionsCommands.Sync(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = IanaTimeZonesCommands.Sync(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = ShellsCommands.Sync(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = ExpectedKeysCommands.Sync(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = UnpinnedToolsCommands.Sync(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = BotActorsCommands.Sync(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = SuperfluousActionsCommands.Sync(repoRoot);
        if (code != 0)
        {
            return code;
        }

        return EventPayloadTypesCommands.Sync(repoRoot);
    }

    if (dataset is "event-payload-types")
    {
        return EventPayloadTypesCommands.Sync(repoRoot);
    }

    UpdateLogger.Error($"Unsupported sync dataset: {dataset}");
    return 1;
}

static int RunVerify(string repoRoot, string dataset)
{
    if (dataset is "webhooks")
    {
        return WebhookCommands.Verify(repoRoot);
    }

    if (dataset is "availability")
    {
        return AvailabilityCommands.Verify(repoRoot);
    }

    if (dataset is "popular-actions")
    {
        return PopularActionsCommands.Verify(repoRoot);
    }

    if (dataset is "runner-labels")
    {
        return RunnerLabelsCommands.Verify(repoRoot);
    }

    if (dataset is "context-types")
    {
        return ContextTypesCommands.Verify(repoRoot);
    }

    if (dataset is "function-specs")
    {
        return FunctionSpecsCommands.Verify(repoRoot);
    }

    if (dataset is "permissions")
    {
        return PermissionsCommands.Verify(repoRoot);
    }

    if (dataset is "iana-timezones")
    {
        return IanaTimeZonesCommands.Verify(repoRoot);
    }

    if (dataset is "shells")
    {
        return ShellsCommands.Verify(repoRoot);
    }

    if (dataset is "expected-keys")
    {
        return ExpectedKeysCommands.Verify(repoRoot);
    }

    if (dataset is "unpinned-tools")
    {
        return UnpinnedToolsCommands.Verify(repoRoot);
    }

    if (dataset is "bot-actors")
    {
        return BotActorsCommands.Verify(repoRoot);
    }

    if (dataset is "superfluous-actions")
    {
        return SuperfluousActionsCommands.Verify(repoRoot);
    }

    if (dataset is "all")
    {
        var code = WebhookCommands.Verify(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = AvailabilityCommands.Verify(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = PopularActionsCommands.Verify(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = RunnerLabelsCommands.Verify(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = ContextTypesCommands.Verify(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = FunctionSpecsCommands.Verify(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = PermissionsCommands.Verify(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = IanaTimeZonesCommands.Verify(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = ShellsCommands.Verify(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = ExpectedKeysCommands.Verify(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = UnpinnedToolsCommands.Verify(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = BotActorsCommands.Verify(repoRoot);
        if (code != 0)
        {
            return code;
        }

        code = SuperfluousActionsCommands.Verify(repoRoot);
        if (code != 0)
        {
            return code;
        }

        return EventPayloadTypesCommands.Verify(repoRoot);
    }

    if (dataset is "event-payload-types")
    {
        return EventPayloadTypesCommands.Verify(repoRoot);
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
