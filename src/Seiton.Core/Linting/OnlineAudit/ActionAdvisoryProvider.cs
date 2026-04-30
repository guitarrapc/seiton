namespace Seiton.Core.Linting.OnlineAudit;

/// <summary>Provides security advisory information for GitHub Actions references.</summary>
public interface IActionAdvisoryProvider
{
    /// <summary>Retrieves a security advisory for the specified action reference, if one exists.</summary>
    public Task<ActionAdvisory?> GetAdvisoryAsync(
        string owner,
        string repo,
        string reference,
        CancellationToken cancellationToken = default);
}

/// <summary>A security advisory associated with a GitHub Actions reference.</summary>
public sealed record ActionAdvisory(
    string AdvisoryId,
    string Summary);
