namespace Seiton.Update.Model;

internal sealed class SourceManifestEntry
{
    public string Dataset { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public List<string> SourceUrls { get; set; } = [];
    public string FetchedAtUtc { get; set; } = string.Empty;
    public string ParserVersion { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
}

internal sealed class SourceManifest
{
    public int SchemaVersion { get; set; } = 1;
    public List<SourceManifestEntry> Entries { get; set; } = [];

    public static SourceManifest Empty => new();
}
