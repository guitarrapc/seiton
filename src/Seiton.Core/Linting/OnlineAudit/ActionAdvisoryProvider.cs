namespace Seiton.Core.Linting.OnlineAudit;

public interface IActionAdvisoryProvider
{
    /// <summary>Retrieves a security advisory for the specified action reference, if one exists.</summary>
    Task<ActionAdvisory?> GetAdvisoryAsync(
        string owner,
        string repo,
        string reference,
        CancellationToken cancellationToken = default);
}

public sealed record ActionAdvisory(
    string AdvisoryId,
    string Summary);
