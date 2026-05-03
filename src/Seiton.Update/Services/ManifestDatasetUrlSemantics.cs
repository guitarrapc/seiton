namespace Seiton.Update.Services;

/// <summary>
/// Ensures manifest sourceUrls match the intended official endpoints and slot order so reordering
/// cannot silently download the wrong artifact into each pipeline stage.
/// </summary>
internal static class ManifestDatasetUrlSemantics
{
    public static void EnsureDatasetUrls(string dataset, IReadOnlyList<string> urls)
    {
        switch (dataset)
        {
            case "popular-actions":
                return;
            case "webhooks":
                RequireCount(urls, 2, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    HostEquals(u, "json.schemastore.org") &&
                    PathContains(u, "github-workflow.json"));
                EnsureUri(urls[1], dataset, 1, static u =>
                    HostEquals(u, "raw.githubusercontent.com") &&
                    PathContains(u, "events-that-trigger-workflows"));
                return;
            case "runner-labels":
                RequireCount(urls, 2, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    HostEquals(u, "docs.github.com") &&
                    PathContains(u, "github-hosted-runners"));
                EnsureUri(urls[1], dataset, 1, static u =>
                    HostEquals(u, "docs.github.com") &&
                    PathContains(u, "larger-runners"));
                return;
            case "permissions":
                RequireCount(urls, 1, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    HostEquals(u, "raw.githubusercontent.com") &&
                    PathContains(u, "github-token-available-permissions.md"));
                return;
            case "availability":
            case "context-types":
                RequireCount(urls, 1, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    HostEquals(u, "raw.githubusercontent.com") &&
                    PathContains(u, "contexts.md"));
                return;
            case "function-specs":
                RequireCount(urls, 1, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    HostEquals(u, "raw.githubusercontent.com") &&
                    PathContains(u, "expressions.md"));
                return;
            case "expected-keys":
                RequireCount(urls, 1, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    HostEquals(u, "raw.githubusercontent.com") &&
                    PathContains(u, "workflow-syntax.md"));
                return;
            case "event-payload-types":
                RequireCount(urls, 1, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    HostEquals(u, "docs.github.com") &&
                    PathContains(u, "webhook-events"));
                return;
            case "iana-timezones":
                RequireCount(urls, 1, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    HostEquals(u, "data.iana.org") &&
                    PathContains(u, "tzdata.zi"));
                return;
            default:
                throw new InvalidOperationException(
                    $"data/sources/manifest.json dataset '{dataset}' has no URL semantics guard. Add an entry to {nameof(ManifestDatasetUrlSemantics)} or mark the dataset as validated elsewhere.");
        }
    }

    private static void RequireCount(IReadOnlyList<string> urls, int expected, string dataset)
    {
        if (urls.Count != expected)
        {
            throw new InvalidOperationException(
                $"data/sources/manifest.json dataset '{dataset}' internal error: expected {expected} URL(s) for semantics check, got {urls.Count}.");
        }
    }

    private static void EnsureUri(string url, string dataset, int index, Func<Uri, bool> predicate)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"data/sources/manifest.json dataset '{dataset}' sourceUrls[{index}] is not a valid absolute URL for semantics validation.");
        }

        if (!predicate(uri))
        {
            throw new InvalidOperationException(
                $"data/sources/manifest.json dataset '{dataset}' sourceUrls[{index}] does not match the expected official source for this slot (wrong host, path, or order). url={url}");
        }
    }

    private static bool HostEquals(Uri uri, string host) =>
        string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase);

    private static bool PathContains(Uri uri, string fragment) =>
        uri.AbsolutePath.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
