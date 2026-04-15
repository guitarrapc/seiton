namespace Seiton.Update.Model;

internal sealed record AvailabilityModel(
    IReadOnlyList<string> WorkflowRoots,
    IReadOnlyList<string> JobRoots,
    IReadOnlyList<string> StepRoots);
