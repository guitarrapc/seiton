namespace Seiton.Update.Services;

/// <summary>
/// Ensures manifest sourceUrls match the intended official endpoints and slot order so reordering
/// cannot silently download the wrong artifact into each pipeline stage.
/// </summary>
internal static class ManifestDatasetUrlSemantics
{
    /// <summary>Official <c>github/docs</c> default branch raw paths only (not organization forks).</summary>
    private const string GitHubDocsRawMainPrefix = "/github/docs/main/";

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
                    PathEqualsIgnoreCase(u, "/github-workflow.json"));
                EnsureUri(urls[1], dataset, 1, static u =>
                    IsOfficialGitHubDocsRaw(u, "content/actions/reference/workflows-and-actions/events-that-trigger-workflows.md"));
                return;
            case "runner-labels":
                RequireCount(urls, 2, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    HostEquals(u, "docs.github.com") &&
                    PathEqualsIgnoreCase(u, "/en/actions/reference/runners/github-hosted-runners.md"));
                EnsureUri(urls[1], dataset, 1, static u =>
                    HostEquals(u, "docs.github.com") &&
                    PathEqualsIgnoreCase(u, "/en/actions/reference/runners/larger-runners.md"));
                return;
            case "permissions":
                RequireCount(urls, 1, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    IsOfficialGitHubDocsRaw(u, "data/reusables/actions/github-token-available-permissions.md"));
                return;
            case "availability":
            case "context-types":
                RequireCount(urls, 1, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    IsOfficialGitHubDocsRaw(u, "content/actions/reference/workflows-and-actions/contexts.md"));
                return;
            case "function-specs":
                RequireCount(urls, 1, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    IsOfficialGitHubDocsRaw(u, "content/actions/reference/workflows-and-actions/expressions.md"));
                return;
            case "expected-keys":
                RequireCount(urls, 1, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    IsOfficialGitHubDocsRaw(u, "content/actions/reference/workflows-and-actions/workflow-syntax.md"));
                return;
            case "shells":
                RequireCount(urls, 1, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    IsOfficialGitHubDocsRaw(u, "data/reusables/actions/supported-shells.md"));
                return;
            case "event-payload-types":
                RequireCount(urls, 1, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    HostEquals(u, "docs.github.com") &&
                    PathEqualsIgnoreCase(u, "/en/webhooks/webhook-events-and-payloads"));
                return;
            case "iana-timezones":
                RequireCount(urls, 1, dataset);
                EnsureUri(urls[0], dataset, 0, static u =>
                    HostEquals(u, "data.iana.org") &&
                    PathEqualsIgnoreCase(u, "/time-zones/tzdb/tzdata.zi"));
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

    /// <summary>
    /// Raw file under <c>https://raw.githubusercontent.com/github/docs/main/...</c> only
    /// (rejects forks such as <c>/other-user/docs/main/...</c> or other branches).
    /// </summary>
    private static bool IsOfficialGitHubDocsRaw(Uri uri, string relativeMainPath)
    {
        if (!HostEquals(uri, "raw.githubusercontent.com"))
        {
            return false;
        }

        var normalized = NormalizePath(uri.AbsolutePath);
        var expected = NormalizePath(GitHubDocsRawMainPrefix + relativeMainPath.TrimStart('/'));
        return string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEqualsIgnoreCase(Uri uri, string absolutePath)
    {
        var normalized = NormalizePath(uri.AbsolutePath);
        var expected = NormalizePath(absolutePath);
        return string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string absolutePath)
    {
        var trimmed = absolutePath.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }
}