using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class GitHubWebhookSourceParser
{
    public IReadOnlyList<WebhookEventModel> Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("GitHub webhook source snapshot not found.", path);
        }

        var text = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<GitHubWebhookSnapshot>(
            text,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        if (snapshot?.Events is null)
        {
            throw new InvalidDataException($"GitHub webhook source snapshot is invalid: {path}");
        }

        var events = snapshot.Events
            .Where(static x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(static x => WebhookEventModel.Create(
                x.Name,
                x.ActivityTypes is null ? null : x.ActivityTypes.ToArray()))
            .OrderBy(static x => x.Name, StringComparer.Ordinal)
            .ToArray();

        return events;
    }

    private sealed class GitHubWebhookSnapshot
    {
        public List<GitHubWebhookEvent>? Events { get; set; }
    }

    private sealed class GitHubWebhookEvent
    {
        public string Name { get; set; } = string.Empty;

        public List<string>? ActivityTypes { get; set; }
    }
}
