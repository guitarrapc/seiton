using Seiton.Update.Generators;
using Seiton.Update.Parsers;

namespace Seiton.Update.Services;

internal sealed class AvailabilitySyncService
{
    private readonly GitHubAvailabilitySourceParser parser = new();
    private readonly AvailabilityCSharpGenerator generator = new();

    public bool Sync(string repoRoot)
    {
        var primarySourcePath = AvailabilitySourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "Availability.g.cs");

        var availability = parser.Parse(primarySourcePath);
        var generated = generator.Generate(availability);

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
        var primarySourcePath = AvailabilitySourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "Availability.g.cs");
        if (!File.Exists(outputPath))
        {
            return false;
        }

        var availability = parser.Parse(primarySourcePath);
        var generated = generator.Generate(availability);
        var current = TextNormalization.NormalizeToLf(File.ReadAllText(outputPath));
        return string.Equals(current, generated, StringComparison.Ordinal);
    }
}
