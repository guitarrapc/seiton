using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class SuperfluousActionsSourceParser
{
    public SuperfluousActionsModel Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Superfluous actions source file not found.", path);
        }

        var text = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<SuperfluousActionsSnapshot>(
            text,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        if (snapshot?.Actions is null)
        {
            throw new InvalidDataException($"Superfluous actions source file is invalid: {path}");
        }

        var entries = snapshot.Actions
            .Where(static x => !string.IsNullOrWhiteSpace(x.Owner) && !string.IsNullOrWhiteSpace(x.Repo) && !string.IsNullOrWhiteSpace(x.Replacement))
            .Select(static x => new SuperfluousActionEntry(
                x.Owner.Trim().ToLowerInvariant(),
                x.Repo.Trim().ToLowerInvariant(),
                x.Replacement.Trim(),
                x.Description?.Trim() ?? string.Empty))
            .OrderBy(static x => x.Owner, StringComparer.Ordinal)
            .ThenBy(static x => x.Repo, StringComparer.Ordinal)
            .ToList();

        return new SuperfluousActionsModel(entries);
    }

    private sealed class SuperfluousActionsSnapshot
    {
        public int SchemaVersion { get; set; }
        public string? Source { get; set; }
        public List<SuperfluousActionsSnapshotEntry>? Actions { get; set; }
    }

    private sealed class SuperfluousActionsSnapshotEntry
    {
        public string Owner { get; set; } = string.Empty;
        public string Repo { get; set; } = string.Empty;
        public string Replacement { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
