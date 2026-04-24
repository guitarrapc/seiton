using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class PermissionsSourceParser
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

        var scopes = snapshot.Scopes
            .Where(static s => !string.IsNullOrWhiteSpace(s.Name))
            .OrderBy(static s => s.Name, StringComparer.Ordinal)
            .Select(static s => new PermissionScopeModel(
                s.Name!,
                (s.Allowed ?? []).Where(static v => !string.IsNullOrWhiteSpace(v)).ToArray()))
            .ToArray();

        return new PermissionsModel(scopes);
    }

    private sealed class PermissionsSnapshot
    {
        public List<ScopeEntry>? Scopes { get; set; }
    }

    private sealed class ScopeEntry
    {
        public string? Name { get; set; }
        public List<string>? Allowed { get; set; }
    }
}
