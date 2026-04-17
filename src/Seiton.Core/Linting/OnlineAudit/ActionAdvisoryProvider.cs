namespace Seiton.Core.Linting.OnlineAudit;

public interface IActionAdvisoryProvider
{
    Task<ActionAdvisory?> GetAdvisoryAsync(
        string owner,
        string repo,
        string reference,
        CancellationToken cancellationToken = default);
}

public sealed record ActionAdvisory(
    string AdvisoryId,
    string Summary);
