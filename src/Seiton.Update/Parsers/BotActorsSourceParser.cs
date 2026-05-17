using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class BotActorsSourceParser
{
    public BotActorsModel Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Bot actors source file not found.", path);
        }

        var text = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<BotActorsSnapshot>(
            text,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        if (snapshot?.BotActors is null)
        {
            throw new InvalidDataException($"Bot actors source file is invalid: {path}");
        }

        var entries = snapshot.BotActors
            .Where(static x => !string.IsNullOrWhiteSpace(x.Login) && x.Id > 0)
            .Select(static x => new BotActorEntry(
                x.Login!,
                x.Id,
                x.Description ?? string.Empty))
            .OrderBy(static x => x.Login, StringComparer.Ordinal)
            .ToArray();

        return new BotActorsModel(entries);
    }

    private sealed class BotActorsSnapshot
    {
        public int SchemaVersion { get; set; }
        public string? Source { get; set; }
        public string? Description { get; set; }
        public BotActorJsonEntry[]? BotActors { get; set; }
    }

    private sealed class BotActorJsonEntry
    {
        public string? Login { get; set; }
        public long Id { get; set; }
        public string? Description { get; set; }
    }
}
