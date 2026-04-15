using Seiton.Update.Generators;
using Seiton.Update.Parsers;

namespace Seiton.Update.Services;

internal sealed class WebhookSyncService
{
    readonly GitHubWebhookSourceParser parser = new();
    readonly WebhookTypesCSharpGenerator generator = new();

    public bool Sync(string repoRoot)
    {
        var primarySourcePath = WebhookSourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "WebhookTypes.g.cs");

        var events = parser.Parse(primarySourcePath);
        var generated = generator.Generate(events);

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
        var primarySourcePath = WebhookSourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "WebhookTypes.g.cs");
        if (!File.Exists(outputPath))
        {
            return false;
        }

        var events = parser.Parse(primarySourcePath);
        var generated = generator.Generate(events);
        var current = File.ReadAllText(outputPath).Replace("\r\n", "\n");
        return string.Equals(current, generated, StringComparison.Ordinal);
    }
}
