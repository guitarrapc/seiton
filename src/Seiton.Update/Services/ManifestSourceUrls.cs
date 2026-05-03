using Seiton.Update.Model;

namespace Seiton.Update.Services;

internal static class ManifestSourceUrls
{
    public static IReadOnlyList<string> Resolve(string repoRoot, string dataset, int? expectedCount)
    {
        var manifest = new ManifestService().Load(repoRoot);
        var entry = manifest.Entries
            .FirstOrDefault(e => string.Equals(e.Dataset, dataset, StringComparison.Ordinal));

        if (entry is null)
        {
            throw new InvalidOperationException(
                $"data/sources/manifest.json has no dataset '{dataset}'. Add an entry with sourceUrls before running fetch.");
        }

        var urls = entry.SourceUrls
            .Where(static u => !string.IsNullOrWhiteSpace(u))
            .Select(static u => u.Trim())
            .ToList();

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
}
