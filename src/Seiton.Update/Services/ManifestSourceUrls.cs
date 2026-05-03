using Seiton.Update.Model;

namespace Seiton.Update.Services;

internal static class ManifestSourceUrls
{
    public static IReadOnlyList<string> Resolve(string repoRoot, string dataset, int? expectedCount)
    {
        var manifest = new ManifestService().Load(repoRoot);
        if (manifest.Entries is null)
        {
            throw new InvalidOperationException(
                "data/sources/manifest.json is invalid: \"entries\" is missing or null.");
        }

        var matches = manifest.Entries
            .Where(e => string.Equals(e.Dataset, dataset, StringComparison.Ordinal))
            .ToList();

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"data/sources/manifest.json has duplicate dataset '{dataset}' ({matches.Count} entries). Remove duplicates so only one entry defines sourceUrls.");
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"data/sources/manifest.json has no dataset '{dataset}'. Add an entry with sourceUrls before running fetch.");
        }

        var entry = matches[0];

        if (entry.SourceUrls is null)
        {
            throw new InvalidOperationException(
                $"data/sources/manifest.json dataset '{dataset}' has null sourceUrls. Set sourceUrls before running fetch.");
        }

        var urls = new List<string>(entry.SourceUrls.Count);
        for (var i = 0; i < entry.SourceUrls.Count; i++)
        {
            var raw = entry.SourceUrls[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException(
                    $"data/sources/manifest.json dataset '{dataset}' sourceUrls[{i}] is empty or whitespace. Blank slots would shift URL positions; remove them or set a valid URL.");
            }

            var trimmed = raw.Trim();
            ValidateAbsoluteHttpsUrl(trimmed, dataset, i);
            urls.Add(trimmed);
        }

        if (urls.Count == 0)
        {
            throw new InvalidOperationException(
                $"data/sources/manifest.json dataset '{dataset}' has no sourceUrls. Set sourceUrls before running fetch.");
        }

        if (expectedCount is int n && urls.Count != n)
        {
            throw new InvalidOperationException(
                $"data/sources/manifest.json dataset '{dataset}' has {urls.Count} sourceUrls but this fetch expects {n}. Update the manifest or targets configuration.");
        }

        return urls;
    }

    public static string ResolveSingle(string repoRoot, string dataset) =>
        Resolve(repoRoot, dataset, expectedCount: 1)[0];

    private static void ValidateAbsoluteHttpsUrl(string url, string dataset, int index)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"data/sources/manifest.json dataset '{dataset}' sourceUrls[{index}] is not a valid absolute URL: \"{url}\".");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"data/sources/manifest.json dataset '{dataset}' sourceUrls[{index}] must use the https scheme: \"{url}\".");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            throw new InvalidOperationException(
                $"data/sources/manifest.json dataset '{dataset}' sourceUrls[{index}] must include a host: \"{url}\".");
        }
    }
}
