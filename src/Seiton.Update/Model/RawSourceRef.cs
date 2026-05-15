namespace Seiton.Update.Model;

/// <summary>
/// One fetched raw file's stable name (manifest <c>rawFileHashes</c> key) and UTF-8 content digest.
/// </summary>
internal sealed class RawSourceRef
{
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}
