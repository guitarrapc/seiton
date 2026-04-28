using Seiton.Update.Generators;
using Seiton.Update.Parsers;

namespace Seiton.Update.Services;

internal sealed class WebhookSyncService
{
    private readonly GitHubWebhookSourceParser parser = new();
    private readonly ExpectedKeysSourceParser expectedKeysParser = new();
    private readonly WebhookTypesCSharpGenerator generator = new();

    public bool Sync(string repoRoot)
    {
        var primarySourcePath = WebhookSourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "WebhookTypes.g.cs");

        var events = parser.Parse(primarySourcePath);
        var eventFilterKeys = LoadEventFilterKeys(repoRoot);
        var generated = generator.Generate(events, eventFilterKeys);

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
        var primarySourcePath = WebhookSourcePathResolver.ResolvePrimary(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "WebhookTypes.g.cs");
        if (!File.Exists(outputPath))
        {
            return false;
        }

        var events = parser.Parse(primarySourcePath);
        var eventFilterKeys = LoadEventFilterKeys(repoRoot);
        var generated = generator.Generate(events, eventFilterKeys);
        var current = TextNormalization.NormalizeToLf(File.ReadAllText(outputPath));
        return string.Equals(current, generated, StringComparison.Ordinal);
    }

    /// <summary>
    /// Loads event-specific filter keys from expected-keys.json.
    /// Sections named "on-{kebab-event}" are mapped to event names with underscores.
    /// </summary>
    private Dictionary<string, string[]> LoadEventFilterKeys(string repoRoot)
    {
        var primaryPath = ExpectedKeysSourcePathResolver.ResolvePrimary(repoRoot);
        var model = expectedKeysParser.Parse(primaryPath);
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var section in model.Sections)
        {
            if (!section.Name.StartsWith("on-", StringComparison.Ordinal))
                continue;

            // Skip the generic "on-event" section (it only has "types" which is handled separately)
            if (section.Name == "on-event")
                continue;

            // Convert section name to event name: "on-pull-request" → "pull_request"
            var eventName = section.Name["on-".Length..].Replace('-', '_');
            result[eventName] = section.Keys.ToArray();
        }

        return result;
    }
}
