using System.Text.Json;
using System.Text.RegularExpressions;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed partial class PermissionsSourceParser
{
    public PermissionsModel Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Permissions source not found.", path);
        }

        var text = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<PermissionsSnapshot>(
            text,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        if (snapshot?.Scopes is null || snapshot.Scopes.Count == 0)
        {
            throw new InvalidDataException($"Permissions source is invalid or empty: {path}");
        }

        var entries = snapshot.Scopes
            .Where(static s => !string.IsNullOrWhiteSpace(s.Name))
            .ToArray();

        // This snapshot is the codegen input: names and access values are emitted verbatim into
        // C# literals (including "..."u8), and a repeated name becomes a duplicate switch label.
        foreach (var entry in entries)
        {
            if (!ScopeNameRegex().IsMatch(entry.Name!))
            {
                throw new InvalidDataException(
                    $"Invalid permission scope name '{entry.Name}' in {path}. Expected lowercase kebab-case.");
            }

            foreach (var value in entry.Allowed ?? [])
            {
                if (!string.IsNullOrWhiteSpace(value) && !AccessValueRegex().IsMatch(value))
                {
                    throw new InvalidDataException(
                        $"Invalid access value '{value}' for permission scope '{entry.Name}' in {path}. Expected lowercase letters.");
                }
            }
        }

        var duplicate = entries
            .GroupBy(static s => s.Name!, StringComparer.Ordinal)
            .FirstOrDefault(static g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate permission scope '{duplicate.Key}' in {path}.");
        }

        var scopes = entries
            .OrderBy(static s => s.Name, StringComparer.Ordinal)
            .Select(static s => new PermissionScopeModel(
                s.Name!,
                (s.Allowed ?? []).Where(static v => !string.IsNullOrWhiteSpace(v)).ToArray(),
                string.IsNullOrWhiteSpace(s.DeprecationNote) ? null : s.DeprecationNote))
            .ToArray();

        return new PermissionsModel(scopes);
    }

    [GeneratedRegex(@"^[a-z][a-z0-9\-]*$")]
    private static partial Regex ScopeNameRegex();

    [GeneratedRegex(@"^[a-z]+$")]
    private static partial Regex AccessValueRegex();

    private sealed class PermissionsSnapshot
    {
        public List<ScopeEntry>? Scopes { get; set; }
    }

    private sealed class ScopeEntry
    {
        public string? Name { get; set; }
        public List<string>? Allowed { get; set; }
        public string? DeprecationNote { get; set; }
    }
}
