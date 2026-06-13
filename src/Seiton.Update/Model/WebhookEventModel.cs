namespace Seiton.Update.Model;

internal sealed record WebhookEventModel(
    string Name,
    IReadOnlyList<string>? ActivityTypes)
{
    internal static WebhookEventModel Create(string name, IReadOnlyList<string>? activityTypes)
        => new(name, NormalizeActivityTypes(activityTypes));

    internal static IReadOnlyList<string>? NormalizeActivityTypes(IReadOnlyList<string>? activityTypes)
    {
        if (activityTypes is null || activityTypes.Count <= 1)
        {
            return activityTypes;
        }

        return activityTypes
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();
    }
}
