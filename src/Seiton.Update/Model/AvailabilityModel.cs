namespace Seiton.Update.Model;

internal sealed record AvailabilityModel(
    IReadOnlyList<string> WorkflowRoots,
    IReadOnlyList<string> WorkflowCallOutputRoots,
    IReadOnlyList<string> JobRoots,
    IReadOnlyList<string> JobOutputRoots,
    IReadOnlyList<string> ReusableWorkflowCallSecretsRoots,
    IReadOnlyList<string> StrategyRoots,
    IReadOnlyList<string> StepRoots,
    IReadOnlyList<string> StepIfRoots);
