using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class GitHubAvailabilitySourceParser
{
    public AvailabilityModel Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("GitHub availability source snapshot not found.", path);
        }

        var text = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<AvailabilitySnapshot>(
            text,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        if (snapshot is null)
        {
            throw new InvalidDataException($"GitHub availability source snapshot is invalid: {path}");
        }

        var entries = (snapshot.Entries ?? [])
            .Select(static e => new AvailabilityEntry(e.WorkflowKey ?? "", e.Contexts ?? []))
            .Where(static e => !string.IsNullOrEmpty(e.WorkflowKey))
            .ToList();

        return new AvailabilityModel(entries);
    }

    private sealed class AvailabilitySnapshot
    {
        public int SchemaVersion { get; set; }
        public string? Source { get; set; }
        public List<SnapshotEntry>? Entries { get; set; }
    }

    private sealed class SnapshotEntry
    {
        public string? WorkflowKey { get; set; }
        public List<string>? Contexts { get; set; }
    }
}
