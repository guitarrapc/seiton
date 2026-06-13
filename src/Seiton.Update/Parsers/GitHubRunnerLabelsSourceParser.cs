using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class GitHubRunnerLabelsSourceParser
{
    public RunnerLabelsModel Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("GitHub runner-labels source snapshot not found.", path);
        }

        var text = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<RunnerLabelsSnapshot>(
            text,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        if (snapshot is null)
        {
            throw new InvalidDataException($"GitHub runner-labels source snapshot is invalid: {path}");
        }

        var stable = (snapshot.StableLabels ?? [])
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var preview = (snapshot.PreviewLabels ?? [])
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var deprecated = (snapshot.DeprecatedLabels ?? [])
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        return new RunnerLabelsModel(stable, preview, deprecated);
    }

    private sealed class RunnerLabelsSnapshot
    {
        public List<string>? StableLabels { get; set; }
        public List<string>? PreviewLabels { get; set; }
        public List<string>? DeprecatedLabels { get; set; }
    }
}
