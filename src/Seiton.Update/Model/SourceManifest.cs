namespace Seiton.Update.Model;

internal sealed class SourceManifestEntry
{
    public string Dataset { get; set; } = string.Empty;
    public List<string> SourceUrls { get; set; } = [];
    public string FetchedAtUtc { get; set; } = string.Empty;
    /// <summary>SHA-256 hashes of each downloaded raw source file. Key is the file name.</summary>
    public Dictionary<string, string> RawFileHashes { get; set; } = [];
}

internal sealed class SourceManifest
{
    public int SchemaVersion { get; set; } = 1;
    public List<SourceManifestEntry> Entries { get; set; } = [];

    public static SourceManifest Empty => new();
}
