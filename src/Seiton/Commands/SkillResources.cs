using System.Reflection;

namespace Seiton.Commands;

internal static class SkillResources
{
    private static readonly Assembly ThisAssembly = typeof(SkillResources).Assembly;
    private const string Prefix = "Skills/";

    /// <summary>Get all embedded skill file entries (relative path and content).</summary>
    public static List<(string RelativePath, string Content)> GetAllSkillFiles()
    {
        var results = new List<(string RelativePath, string Content)>();
        foreach (var name in ThisAssembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal))
                continue;

            using var stream = ThisAssembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"embedded resource stream not found: {name}");

            using var reader = new StreamReader(stream);
            var relativePath = name[Prefix.Length..];
            results.Add((relativePath, reader.ReadToEnd()));
        }

        results.Sort(static (a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));
        return results;
    }
}
