namespace Seiton.Update.Model;

internal sealed record AvailabilityModel(IReadOnlyList<AvailabilityEntry> Entries);

internal sealed record AvailabilityEntry(string WorkflowKey, IReadOnlyList<string> Contexts);
