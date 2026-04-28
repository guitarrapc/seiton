using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

/// <summary>
/// Reads the canonical expected-keys.json snapshot and deserializes it into <see cref="ExpectedKeysModel"/>.
/// Used by the sync service to generate .g.cs from the committed snapshot.
/// </summary>
internal sealed class ExpectedKeysSourceParser
{
    public ExpectedKeysModel Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Expected keys source file not found.", path);
        }

        var text = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<ExpectedKeysSnapshot>(
            text,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        if (snapshot?.Sections is null)
        {
            throw new InvalidDataException($"Expected keys source file is invalid: {path}");
        }

        var entries = snapshot.Sections
            .Where(static x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(static x => new ExpectedKeySection(
                x.Name!,
                x.Description ?? string.Empty,
                (x.Keys ?? [])
                    .Where(static k => !string.IsNullOrWhiteSpace(k))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static k => k, StringComparer.Ordinal)
                    .ToList()))
            .ToList();

        return new ExpectedKeysModel(entries);
    }

    private sealed class ExpectedKeysSnapshot
    {
        public List<ExpectedKeysSectionSnapshot>? Sections { get; set; }
    }

    private sealed class ExpectedKeysSectionSnapshot
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? Keys { get; set; }
    }
}
