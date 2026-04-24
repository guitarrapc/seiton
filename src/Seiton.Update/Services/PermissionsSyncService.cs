using Seiton.Update.Generators;
using Seiton.Update.Parsers;

namespace Seiton.Update.Services;

internal sealed class PermissionsSyncService
{
    private readonly PermissionsSourceParser _parser = new();
    private readonly PermissionsCSharpGenerator _generator = new();

    public bool Sync(string repoRoot)
    {
        var primarySourcePath = PermissionsSourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "PermissionScopes.g.cs");

        var model = _parser.Parse(primarySourcePath);
        var generated = _generator.Generate(model);

        var current = File.Exists(outputPath)
            ? TextNormalization.NormalizeToLf(File.ReadAllText(outputPath))
            : string.Empty;

        if (string.Equals(current, generated, StringComparison.Ordinal))
        {
            return false; // no change
        }

        File.WriteAllText(outputPath, generated);
        return true;
    }

    public bool IsUpToDate(string repoRoot)
    {
        var primarySourcePath = PermissionsSourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "PermissionScopes.g.cs");
        if (!File.Exists(outputPath))
        {
            return false;
        }

        var model = _parser.Parse(primarySourcePath);
        var generated = _generator.Generate(model);
        var current = TextNormalization.NormalizeToLf(File.ReadAllText(outputPath));
        return string.Equals(current, generated, StringComparison.Ordinal);
    }
}
