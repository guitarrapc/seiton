namespace Seiton.Update.Model;

internal sealed record BotActorEntry(
    string Login,
    long Id,
    string Description);

internal sealed record BotActorsModel(
    IReadOnlyList<BotActorEntry> BotActors);
