using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class UnpinnedToolsSourceParser
{
    public UnpinnedToolsModel Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Unpinned-tools source file not found.", path);
        }

        var text = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<UnpinnedToolsSnapshot>(
            text,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        if (snapshot?.Actions is null)
        {
            throw new InvalidDataException($"Unpinned-tools source file is invalid: {path}");
        }

        var entries = snapshot.Actions
            .Where(static x => !string.IsNullOrWhiteSpace(x.Owner) && !string.IsNullOrWhiteSpace(x.Repo))
            .Select(static x => new UnpinnedToolAction(
                x.Owner!.Trim().ToLowerInvariant(),
                x.Repo!.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(x.VersionInput) ? "version" : x.VersionInput!.Trim(),
                x.Description ?? string.Empty))
            .OrderBy(static x => x.Owner, StringComparer.Ordinal)
            .ThenBy(static x => x.Repo, StringComparer.Ordinal)
            .ToArray();

        return new UnpinnedToolsModel(entries);
    }

    private sealed class UnpinnedToolsSnapshot
    {
        public List<UnpinnedToolActionSnapshot>? Actions { get; set; }
    }

    private sealed class UnpinnedToolActionSnapshot
    {
        public string? Owner { get; set; }
        public string? Repo { get; set; }
        public string? VersionInput { get; set; }
        public string? Description { get; set; }
    }
}
