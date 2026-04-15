using Seiton.Update.Generators;
using Seiton.Update.Parsers;

namespace Seiton.Update.Services;

internal sealed class AvailabilitySyncService
{
    readonly GitHubAvailabilitySourceParser parser = new();
    readonly AvailabilityCSharpGenerator generator = new();

    public bool Sync(string repoRoot)
    {
        var primarySourcePath = AvailabilitySourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "Availability.g.cs");

        var availability = parser.Parse(primarySourcePath);
        var generated = generator.Generate(availability);

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
        var primarySourcePath = AvailabilitySourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "Availability.g.cs");
        if (!File.Exists(outputPath))
        {
            return false;
        }

        var availability = parser.Parse(primarySourcePath);
        var generated = generator.Generate(availability);
        var current = File.ReadAllText(outputPath).Replace("\r\n", "\n");
        return string.Equals(current, generated, StringComparison.Ordinal);
    }
}
