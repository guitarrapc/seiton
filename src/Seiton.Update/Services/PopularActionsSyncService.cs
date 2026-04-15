using Seiton.Update.Generators;
using Seiton.Update.Parsers;

namespace Seiton.Update.Services;

internal sealed class PopularActionsSyncService
{
    readonly GitHubPopularActionsSourceParser parser = new();
    readonly PopularActionsCSharpGenerator generator = new();

    public bool Sync(string repoRoot)
    {
        var primarySourcePath = PopularActionsSourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "PopularActions.g.cs");

        var actions = parser.Parse(primarySourcePath);
        var generated = generator.Generate(actions);

        var current = File.Exists(outputPath)
            ? File.ReadAllText(outputPath).Replace("\r\n", "\n")
            : string.Empty;

        if (string.Equals(current, generated, StringComparison.Ordinal))
        {
            return false;
        }

        File.WriteAllText(outputPath, generated);
        return true;
    }

    public bool IsUpToDate(string repoRoot)
    {
        var primarySourcePath = PopularActionsSourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "PopularActions.g.cs");
        if (!File.Exists(outputPath))
        {
            return false;
        }

        var actions = parser.Parse(primarySourcePath);
        var generated = generator.Generate(actions);
        var current = File.ReadAllText(outputPath).Replace("\r\n", "\n");
        return string.Equals(current, generated, StringComparison.Ordinal);
    }
}
