namespace Seiton.Update.Model;

internal sealed record WebhookDiffResult(
    IReadOnlyList<string> MissingInSeiton,
    IReadOnlyList<string> ExtraInSeiton)
{
    public bool HasDifferences => MissingInSeiton.Count > 0 || ExtraInSeiton.Count > 0;
}
