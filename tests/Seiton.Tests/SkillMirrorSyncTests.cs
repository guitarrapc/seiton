namespace Seiton.Tests;

public sealed class SkillMirrorSyncTests
{
    [Test]
    public async Task SkillFiles_MirrorMatchesEmbeddedSource()
    {
        var repoRoot = FindRepoRoot();
        var sourceRoot = Path.Combine(repoRoot, "src", "Seiton", "Skills");
        var mirrorRoot = Path.Combine(repoRoot, ".claude", "skills", "seiton");

        await Assert.That(Directory.Exists(sourceRoot)).IsTrue();
        await Assert.That(Directory.Exists(mirrorRoot)).IsTrue();

        var sourceFiles = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        var mirrorFiles = Directory.GetFiles(mirrorRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(mirrorRoot, path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(mirrorFiles).IsEquivalentTo(sourceFiles);

        foreach (var relativePath in sourceFiles)
        {
            var sourceText = NormalizeNewlines(File.ReadAllText(Path.Combine(sourceRoot, relativePath)));
            var mirrorText = NormalizeNewlines(File.ReadAllText(Path.Combine(mirrorRoot, relativePath)));
            await Assert.That(mirrorText).IsEqualTo(sourceText);
        }
    }

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "seiton.slnx")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
