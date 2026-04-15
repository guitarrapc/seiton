using Seiton.Update.Generators;
using Seiton.Update.Parsers;

namespace Seiton.Update.Services;

internal sealed class RunnerLabelsSyncService
{
    readonly GitHubRunnerLabelsSourceParser parser = new();
    readonly RunnerLabelsCSharpGenerator generator = new();

    public bool Sync(string repoRoot)
    {
        var primarySourcePath = RunnerLabelsSourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "RunnerLabels.g.cs");

        var labels = parser.Parse(primarySourcePath);
        var generated = generator.Generate(labels);

        var current = File.Exists(outputPath)
            ? TextNormalization.NormalizeToLf(File.ReadAllText(outputPath))
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
        var primarySourcePath = RunnerLabelsSourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "RunnerLabels.g.cs");
        if (!File.Exists(outputPath))
        {
            return false;
        }

        var labels = parser.Parse(primarySourcePath);
        var generated = generator.Generate(labels);
        var current = TextNormalization.NormalizeToLf(File.ReadAllText(outputPath));
        return string.Equals(current, generated, StringComparison.Ordinal);
    }
}
