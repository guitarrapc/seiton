using Seiton.Update.Model;

namespace Seiton.Update.Services;

internal static class Stage2ArtifactRawSources
{
    /// <summary>
    /// Build <see cref="RawSourceRef"/> entries sorted by <paramref name="manifestFileName"/> for stable JSON.
    /// </summary>
    public static List<RawSourceRef> FromFiles(params (string fullPath, string manifestFileName)[] files)
    {
        var ordered = files
            .OrderBy(f => f.manifestFileName, StringComparer.Ordinal)
            .ToArray();

        var list = new List<RawSourceRef>(ordered.Length);
        foreach (var (fullPath, manifestFileName) in ordered)
        {
            var text = File.ReadAllText(fullPath);
            list.Add(new RawSourceRef
            {
                FileName = manifestFileName,
                Sha256 = SourceContentHasher.ComputeSha256(text),
            });
        }

        return list;
    }
}
