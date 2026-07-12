using System.Text.RegularExpressions;

namespace Seiton.Core.Tests;

public sealed class AstUtf8PolicyTests
{
    [Test]
    public async Task AstFiles_ShouldNotUseSystemStringType()
    {
        // Scope: AST node definitions only (top-level Ast/*.cs). The Refs/ facade layer is
        // excluded because refs never store strings — their Decode() methods return strings
        // for diagnostic display, which spec §0.5.2 explicitly permits.
        var root = FindRepoRoot();
        var astDir = Path.Combine(root, "src", "Seiton.Core", "Parsing", "Ast");
        var files = Directory.EnumerateFiles(astDir, "*.cs", SearchOption.TopDirectoryOnly).ToArray();

        await Assert.That(files.Length).IsGreaterThan(0);

        var violations = new List<string>();
        var stringTypePattern = new Regex(@"\bstring\b|System\.String", RegexOptions.Compiled);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            if (stringTypePattern.IsMatch(text))
            {
                violations.Add(Path.GetRelativePath(root, file));
            }
        }

        await Assert.That(violations).IsEmpty();
    }

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
