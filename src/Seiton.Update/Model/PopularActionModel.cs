namespace Seiton.Update.Model;

internal sealed record PopularActionModel(
    string Uses,
    IReadOnlyList<string> Inputs);
