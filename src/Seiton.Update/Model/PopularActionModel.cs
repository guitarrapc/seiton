namespace Seiton.Update.Model;

internal sealed record PopularActionInputModel(
    string Name,
    bool Required);

internal sealed record PopularActionOutputModel(
    string Name);

internal sealed record PopularActionModel(
    string Uses,
    IReadOnlyList<PopularActionInputModel> Inputs,
    IReadOnlyList<PopularActionOutputModel> Outputs,
    string RunsUsing);
