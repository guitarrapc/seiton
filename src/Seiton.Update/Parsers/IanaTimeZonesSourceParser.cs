using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class IanaTimeZonesSourceParser
{
    public IanaTimeZonesModel Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("IANA timezones source snapshot not found.", path);
        }

        var text = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<IanaTimeZonesSnapshot>(
            text,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        if (snapshot is null)
        {
            throw new InvalidDataException($"IANA timezones source snapshot is invalid: {path}");
        }

        var zones = (snapshot.ZoneIds ?? [])
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var links = (snapshot.LinkIds ?? [])
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        return new IanaTimeZonesModel(snapshot.Version ?? string.Empty, zones, links);
    }

    private sealed class IanaTimeZonesSnapshot
    {
        public string? Version { get; set; }
        public List<string>? ZoneIds { get; set; }
        public List<string>? LinkIds { get; set; }
    }
}
