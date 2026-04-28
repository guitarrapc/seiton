namespace Seiton.Update.Model;

internal sealed record ShellEntry(
    string Name,
    IReadOnlyList<string> Platforms);

internal sealed record ShellsModel(
    IReadOnlyList<ShellEntry> Shells);
