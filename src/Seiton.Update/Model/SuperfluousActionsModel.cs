namespace Seiton.Update.Model;

internal sealed record SuperfluousActionEntry(
    string Owner,
    string Repo,
    string Replacement,
    string Description);

internal sealed record SuperfluousActionsModel(
    IReadOnlyList<SuperfluousActionEntry> Actions);
