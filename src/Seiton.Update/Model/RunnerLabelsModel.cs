namespace Seiton.Update.Model;

internal sealed record RunnerLabelsModel(
    IReadOnlyList<string> StableLabels,
    IReadOnlyList<string> PreviewLabels,
    IReadOnlyList<string> DeprecatedLabels);
