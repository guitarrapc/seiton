using Seiton.Update.Generators;
using Seiton.Update.Parsers;

namespace Seiton.Update.Services;

internal sealed class IanaTimeZonesSyncService
{
    private readonly IanaTimeZonesSourceParser parser = new();
    private readonly IanaTimeZonesCSharpGenerator generator = new();

    public bool Sync(string repoRoot)
    {
        var primarySourcePath = IanaTimeZonesSourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "IanaTimeZones.g.cs");

        var model = parser.Parse(primarySourcePath);
        var generated = generator.Generate(model);

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
        var primarySourcePath = IanaTimeZonesSourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "IanaTimeZones.g.cs");
        if (!File.Exists(outputPath))
        {
            return false;
        }

        var model = parser.Parse(primarySourcePath);
        var generated = generator.Generate(model);
        var current = TextNormalization.NormalizeToLf(File.ReadAllText(outputPath));
        return string.Equals(current, generated, StringComparison.Ordinal);
    }
}
