using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using static Seiton.Core.Linting.ActionRefHelpers;

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

public sealed class ActionRefResolver(HttpClient httpClient, GitHubNetworkConfig githubConfig) : IActionRefResolver
{
    static readonly Uri PublicApiBaseUri = new("https://api.github.com/");
    static readonly string[] TokenEnvVars = ["SEITON_GITHUB_TOKEN", "GITHUB_TOKEN"];

    readonly HttpClient httpClient = httpClient;
    readonly GitHubNetworkConfig githubConfig = githubConfig;
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
        var resolved = IsFullCommitSha(reference)
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
        if (!string.IsNullOrWhiteSpace(githubConfig.GhesApiUrl))
        {
            var ghesBaseUri = NormalizeApiBaseUri(githubConfig.GhesApiUrl!);
            var ghesResponse = await SendGetAsync(ghesBaseUri, relativePath, token, cancellationToken);
            if (ghesResponse.IsSuccessStatusCode)
            {
                return ghesResponse;
            }

            if (!githubConfig.GhesFallback || ghesResponse.StatusCode != HttpStatusCode.NotFound)
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

        var client = httpClient;
        return await client.SendAsync(request, cancellationToken);
    }

    string ResolveToken()
    {
        for (var i = 0; i < TokenEnvVars.Length; i++)
        {
            var envVar = TokenEnvVars[i];
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
}
