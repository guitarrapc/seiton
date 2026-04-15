namespace Seiton.Update.Model;

internal sealed record WebhookEventModel(
    string Name,
    IReadOnlyList<string>? ActivityTypes);
