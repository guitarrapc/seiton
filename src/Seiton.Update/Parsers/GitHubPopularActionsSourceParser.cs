using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class GitHubPopularActionsSourceParser
{
    public IReadOnlyList<PopularActionModel> Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("GitHub popular-actions source snapshot not found.", path);
        }

        var text = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<PopularActionsSnapshot>(
            text,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        if (snapshot?.Actions is null)
        {
            throw new InvalidDataException($"GitHub popular-actions source snapshot is invalid: {path}");
        }

        return snapshot.Actions
            .Where(static x => !string.IsNullOrWhiteSpace(x.Uses))
            .Select(static x => new PopularActionModel(
                x.Uses,
                (x.Inputs ?? []).Select(static i => new PopularActionInputModel(i.Name, i.Required, i.DeprecationMessage)).ToArray(),
                (x.Outputs ?? []).Select(static o => new PopularActionOutputModel(o.Name)).ToArray(),
                x.RunsUsing ?? string.Empty,
                x.MaxDeprecatedMajorVersion ?? 0))
            .OrderBy(static x => x.Uses, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class PopularActionsSnapshot
    {
        public List<PopularActionEntry>? Actions { get; set; }
    }

    private sealed class PopularActionEntry
    {
        public string Uses { get; set; } = string.Empty;
        public List<PopularActionInputEntry>? Inputs { get; set; }
        public List<PopularActionOutputEntry>? Outputs { get; set; }
        public string? RunsUsing { get; set; }
        public int? MaxDeprecatedMajorVersion { get; set; }
    }

    private sealed class PopularActionInputEntry
    {
        public string Name { get; set; } = string.Empty;
        public bool Required { get; set; }
        public string? DeprecationMessage { get; set; }
    }

    private sealed class PopularActionOutputEntry
    {
        public string Name { get; set; } = string.Empty;
    }
}
