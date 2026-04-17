using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Seiton.Core.Linting.OnlineAudit;

public interface IActionRefResolver
{
    Task<ActionRefResolution> ResolveAsync(
        string owner,
        string repo,
        string reference,
        CancellationToken cancellationToken = default);
}

public readonly record struct ActionRefResolution(
    bool CommitExists,
    bool HasBranchReference,
    bool HasTagReference,
    bool IsTaggedCommit);

public sealed class ActionRefResolver(IHttpClientFactory httpClientFactory, OnlineAuditGitHubConfig config) : IActionRefResolver
{
    static readonly Uri PublicApiBaseUri = new("https://api.github.com/");

    readonly IHttpClientFactory httpClientFactory = httpClientFactory;
    readonly OnlineAuditGitHubConfig config = config;
    readonly ConcurrentDictionary<string, ActionRefResolution> cache = new(StringComparer.Ordinal);

    public async Task<ActionRefResolution> ResolveAsync(
        string owner,
        string repo,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = string.Concat(owner, "/", repo, "@", reference);
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var token = ResolveToken();
        var resolved = IsCommitSha(reference)
            ? await ResolveCommitAsync(owner, repo, reference, token, cancellationToken)
            : await ResolveSymbolicRefAsync(owner, repo, reference, token, cancellationToken);

        cache.TryAdd(cacheKey, resolved);
        return resolved;
    }

    async Task<ActionRefResolution> ResolveCommitAsync(
        string owner,
        string repo,
        string reference,
        string token,
        CancellationToken cancellationToken)
    {
        var commitExists = await CommitExistsWithFallbackAsync(owner, repo, reference, token, cancellationToken);
        if (!commitExists)
        {
            return new ActionRefResolution(
                CommitExists: false,
                HasBranchReference: false,
                HasTagReference: false,
                IsTaggedCommit: false);
        }

        var isTaggedCommit = await IsTaggedCommitWithFallbackAsync(owner, repo, reference, token, cancellationToken);
        return new ActionRefResolution(
            CommitExists: true,
            HasBranchReference: false,
            HasTagReference: false,
            IsTaggedCommit: isTaggedCommit);
    }

    async Task<ActionRefResolution> ResolveSymbolicRefAsync(
        string owner,
        string repo,
        string reference,
        string token,
        CancellationToken cancellationToken)
    {
        var branchExists = await RefExistsWithFallbackAsync(owner, repo, "heads", reference, token, cancellationToken);
        var tagExists = await RefExistsWithFallbackAsync(owner, repo, "tags", reference, token, cancellationToken);
        return new ActionRefResolution(
            CommitExists: false,
            HasBranchReference: branchExists,
            HasTagReference: tagExists,
            IsTaggedCommit: false);
    }

    async Task<bool> CommitExistsWithFallbackAsync(
        string owner,
        string repo,
        string sha,
        string token,
        CancellationToken cancellationToken)
    {
        var path = $"repos/{owner}/{repo}/commits/{sha}";
        var response = await SendGetWithFallbackAsync(path, token, cancellationToken);
        if (response is null)
        {
            return false;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            response.EnsureSuccessStatusCode();
            return true;
        }
    }

    async Task<bool> RefExistsWithFallbackAsync(
        string owner,
        string repo,
        string namespaceName,
        string reference,
        string token,
        CancellationToken cancellationToken)
    {
        var encodedReference = Uri.EscapeDataString(reference);
        var path = $"repos/{owner}/{repo}/git/ref/{namespaceName}/{encodedReference}";
        var response = await SendGetWithFallbackAsync(path, token, cancellationToken);
        if (response is null)
        {
            return false;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            response.EnsureSuccessStatusCode();
            return true;
        }
    }

    async Task<bool> IsTaggedCommitWithFallbackAsync(
        string owner,
        string repo,
        string sha,
        string token,
        CancellationToken cancellationToken)
    {
        var path = $"repos/{owner}/{repo}/tags?per_page=100";
        var response = await SendGetWithFallbackAsync(path, token, cancellationToken);
        if (response is null)
        {
            return false;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var root = document.RootElement;
            for (var i = 0; i < root.GetArrayLength(); i++)
            {
                var tag = root[i];
                if (!tag.TryGetProperty("commit", out var commit)
                    || !commit.TryGetProperty("sha", out var shaNode))
                {
                    continue;
                }

                if (string.Equals(shaNode.GetString(), sha, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    async Task<HttpResponseMessage?> SendGetWithFallbackAsync(
        string relativePath,
        string token,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(config.GhesApiUrl))
        {
            var ghesBaseUri = NormalizeApiBaseUri(config.GhesApiUrl!);
            var ghesResponse = await SendGetAsync(ghesBaseUri, relativePath, token, cancellationToken);
            if (ghesResponse.IsSuccessStatusCode)
            {
                return ghesResponse;
            }

            if (!config.GhesFallback || ghesResponse.StatusCode != HttpStatusCode.NotFound)
            {
                return ghesResponse;
            }

            ghesResponse.Dispose();
        }

        return await SendGetAsync(PublicApiBaseUri, relativePath, token, cancellationToken);
    }

    async Task<HttpResponseMessage> SendGetAsync(
        Uri baseUri,
        string relativePath,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, relativePath));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Seiton", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var client = httpClientFactory.CreateClient(nameof(ActionRefResolver));
        return await client.SendAsync(request, cancellationToken);
    }

    string ResolveToken()
    {
        for (var i = 0; i < config.TokenEnvVars.Count; i++)
        {
            var envVar = config.TokenEnvVars[i];
            if (string.IsNullOrWhiteSpace(envVar))
            {
                continue;
            }

            var value = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    static Uri NormalizeApiBaseUri(string apiBaseUrl)
    {
        var normalized = apiBaseUrl.Trim();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized += "/";
        }

        return new Uri(normalized, UriKind.Absolute);
    }

    static bool IsCommitSha(string reference)
    {
        if (reference.Length != 40)
        {
            return false;
        }

        for (var i = 0; i < reference.Length; i++)
        {
            var ch = reference[i];
            var isDigit = ch is >= '0' and <= '9';
            var isLowerHex = ch is >= 'a' and <= 'f';
            var isUpperHex = ch is >= 'A' and <= 'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }
}
