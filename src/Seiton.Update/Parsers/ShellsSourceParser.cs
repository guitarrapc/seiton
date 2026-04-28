using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class ShellsSourceParser
{
    public ShellsModel Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Shells source file not found.", path);
        }

        var text = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<ShellsSnapshot>(
            text,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        if (snapshot?.Shells is null)
        {
            throw new InvalidDataException($"Shells source file is invalid: {path}");
        }

        var entries = snapshot.Shells
            .Where(static x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(static x => new ShellEntry(
                x.Name!,
                (x.Platforms ?? [])
                    .Where(static p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderBy(static x => x.Name, StringComparer.Ordinal)
            .ToArray();

        return new ShellsModel(entries);
    }

    private sealed class ShellsSnapshot
    {
        public List<ShellEntrySnapshot>? Shells { get; set; }
    }

    private sealed class ShellEntrySnapshot
    {
        public string? Name { get; set; }
        public List<string>? Platforms { get; set; }
    }
}
