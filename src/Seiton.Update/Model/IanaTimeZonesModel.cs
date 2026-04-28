namespace Seiton.Update.Model;

internal sealed record IanaTimeZonesModel(
    string Version,
    IReadOnlyList<string> ZoneIds,
    IReadOnlyList<string> LinkIds);
