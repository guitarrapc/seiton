namespace Seiton.Update.Model;

internal sealed record ExpectedKeySection(
    string Name,
    string Description,
    IReadOnlyList<string> Keys);

internal sealed record ExpectedKeysModel(
    IReadOnlyList<ExpectedKeySection> Sections);
