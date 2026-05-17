namespace Seiton.Update.Model;

internal sealed record UnpinnedToolAction(
    string Owner,
    string Repo,
    string VersionInput,
    string Description);

internal sealed record UnpinnedToolsModel(
    IReadOnlyList<UnpinnedToolAction> Actions);
