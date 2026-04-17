using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Seiton.Core.Linting.PinRemediation;

public sealed class GitHubActionShaResolver(IHttpClientFactory httpClientFactory, GitHubActionsResolutionConfig config) : IActionShaResolver
{
    static readonly Uri PublicApiBaseUri = new("https://api.github.com/");

    readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    readonly GitHubActionsResolutionConfig _config = config;
    readonly ConcurrentDictionary<string, string> _successCache = new(StringComparer.Ordinal);
    readonly Regex[] _compiledExcludeBranches = CompileLiteralBranchPatterns(config.ExcludeBranches);
    readonly CompiledIgnoreActionEntry[] _compiledIgnoreActions = CompileIgnoreActions(config.IgnoreActions);

    public async Task<(string? Sha, string? TagComment)> ResolveAsync(
        string owner,
        string repo,
        string refStr,
        CancellationToken cancellationToken = default)
    {
        if (ShouldSkip(owner, repo, refStr))
        {
            return (null, null);
        }

        var cacheKey = string.Concat(owner, "/", repo, "@", refStr);
        if (_successCache.TryGetValue(cacheKey, out var cachedSha))
        {
            return (cachedSha, refStr);
        }

        var token = ResolveToken();
        var result = await ResolveShaWithFallbackAsync(owner, repo, refStr, token, cancellationToken);

        if (_config.MinAgeDays > 0 && result.TagDate.HasValue)
        {
            var age = DateTimeOffset.UtcNow - result.TagDate.Value;
            if (age.TotalDays < _config.MinAgeDays)
            {
                return (null, null);
            }
        }

        _successCache.TryAdd(cacheKey, result.Sha!);
        return (result.Sha, refStr);
    }

    bool ShouldSkip(string owner, string repo, string refStr)
    {
        if (MatchesExcludedBranch(refStr))
        {
            return true;
        }

        var name = owner + "/" + repo;
        for (var i = 0; i < _compiledIgnoreActions.Length; i++)
        {
            var entry = _compiledIgnoreActions[i];
            if (entry.NameRegex.IsMatch(name) && entry.RefRegex.IsMatch(refStr))
            {
                return true;
            }
        }

        return false;
    }

    bool MatchesExcludedBranch(string refStr)
    {
        for (var i = 0; i < _compiledExcludeBranches.Length; i++)
        {
            if (_compiledExcludeBranches[i].IsMatch(refStr))
            {
                return true;
            }
        }

        return false;
    }

    string ResolveToken()
    {
        foreach (var envVar in _config.TokenEnvVars)
        {
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

    async Task<ResolveAttemptResult> ResolveShaWithFallbackAsync(
        string owner,
        string repo,
        string refStr,
        string token,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_config.GhesApiUrl))
        {
            var ghesBaseUri = NormalizeApiBaseUri(_config.GhesApiUrl!);
            var ghesResult = await TryResolveShaAsync(ghesBaseUri, owner, repo, refStr, token, cancellationToken);
            if (ghesResult.Success)
            {
                return ghesResult;
            }

            if (!_config.GhesFallback || ghesResult.StatusCode != HttpStatusCode.NotFound)
            {
                throw CreateResolutionException(owner, repo, refStr, ghesResult.StatusCode, ghesBaseUri);
            }
        }

        var publicResult = await TryResolveShaAsync(PublicApiBaseUri, owner, repo, refStr, token, cancellationToken);
        if (publicResult.Success)
        {
            return publicResult;
        }

        throw CreateResolutionException(owner, repo, refStr, publicResult.StatusCode, PublicApiBaseUri);
    }


    async Task<ResolveAttemptResult> TryResolveShaAsync(
        Uri apiBaseUri,
        string owner,
        string repo,
        string refStr,
        string token,
        CancellationToken cancellationToken)
    {
        var refPath = $"repos/{owner}/{repo}/git/ref/tags/{Uri.EscapeDataString(refStr)}";
        using var refResponse = await SendGetAsync(apiBaseUri, refPath, token, cancellationToken);
        if (!refResponse.IsSuccessStatusCode)
        {
            return new ResolveAttemptResult(null, refResponse.StatusCode);
        }

        await using var refStream = await refResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var refDocument = await JsonDocument.ParseAsync(refStream, cancellationToken: cancellationToken);
        var root = refDocument.RootElement;
        var objectNode = root.GetProperty("object");
        var objectType = objectNode.GetProperty("type").GetString();
        var objectSha = objectNode.GetProperty("sha").GetString();

        if (string.Equals(objectType, "commit", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(objectSha))
        {
            var commitDate = await TryGetCommitDateAsync(apiBaseUri, owner, repo, objectSha, token, cancellationToken);
            return new ResolveAttemptResult(objectSha, HttpStatusCode.OK, commitDate);
        }

        if (!string.Equals(objectType, "tag", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(objectSha))
        {
            return new ResolveAttemptResult(null, HttpStatusCode.UnprocessableEntity);
        }

        var tagPath = $"repos/{owner}/{repo}/git/tags/{objectSha}";
        using var tagResponse = await SendGetAsync(apiBaseUri, tagPath, token, cancellationToken);
        if (!tagResponse.IsSuccessStatusCode)
        {
            return new ResolveAttemptResult(null, tagResponse.StatusCode);
        }

        await using var tagStream = await tagResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var tagDocument = await JsonDocument.ParseAsync(tagStream, cancellationToken: cancellationToken);
        DateTimeOffset? taggerDate = null;
        if (tagDocument.RootElement.TryGetProperty("tagger", out var taggerNode) &&
            taggerNode.TryGetProperty("date", out var taggerDateNode) &&
            taggerDateNode.TryGetDateTimeOffset(out var parsedTaggerDate))
        {
            taggerDate = parsedTaggerDate;
        }
        var targetNode = tagDocument.RootElement.GetProperty("object");
        var targetType = targetNode.GetProperty("type").GetString();
        var targetSha = targetNode.GetProperty("sha").GetString();
        if (!string.Equals(targetType, "commit", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(targetSha))
        {
            return new ResolveAttemptResult(null, HttpStatusCode.UnprocessableEntity);
        }

        return new ResolveAttemptResult(targetSha, HttpStatusCode.OK, taggerDate);
    }

    async Task<DateTimeOffset?> TryGetCommitDateAsync(
        Uri apiBaseUri,
        string owner,
        string repo,
        string commitSha,
        string token,
        CancellationToken cancellationToken)
    {
        var commitPath = $"repos/{owner}/{repo}/commits/{commitSha}";
        using var response = await SendGetAsync(apiBaseUri, commitPath, token, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("commit", out var commitNode) &&
            commitNode.TryGetProperty("committer", out var committerNode) &&
            committerNode.TryGetProperty("date", out var dateNode) &&
            dateNode.TryGetDateTimeOffset(out var date))
        {
            return date;
        }

        return null;
    }

    async Task<HttpResponseMessage> SendGetAsync(
        Uri apiBaseUri,
        string relativePath,
        string token,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(GitHubActionShaResolver));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(apiBaseUri, relativePath));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Seiton", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    static Uri NormalizeApiBaseUri(string apiUrl)
    {
        var baseUri = new Uri(apiUrl, UriKind.Absolute);
        var builder = new UriBuilder(baseUri);
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    static Regex[] CompileLiteralBranchPatterns(IReadOnlyList<string> branches)
    {
        if (branches.Count == 0)
        {
            return [];
        }

        var compiled = new Regex[branches.Count];
        for (var i = 0; i < branches.Count; i++)
        {
            compiled[i] = new Regex("^" + Regex.Escape(branches[i]) + "$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }

        return compiled;
    }

    static CompiledIgnoreActionEntry[] CompileIgnoreActions(IReadOnlyList<IgnoreActionEntry> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var compiled = new CompiledIgnoreActionEntry[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            compiled[i] = new CompiledIgnoreActionEntry(
                new Regex(entry.NamePattern, RegexOptions.CultureInvariant | RegexOptions.Compiled),
                new Regex(entry.RefPattern, RegexOptions.CultureInvariant | RegexOptions.Compiled));
        }

        return compiled;
    }

    static InvalidOperationException CreateResolutionException(
        string owner,
        string repo,
        string refStr,
        HttpStatusCode statusCode,
        Uri apiBaseUri)
    {
        return new InvalidOperationException(
            $"Failed to resolve GitHub action SHA for '{owner}/{repo}@{refStr}' via '{apiBaseUri}' (status {(int)statusCode}).");
    }

    readonly record struct ResolveAttemptResult(string? Sha, HttpStatusCode StatusCode, DateTimeOffset? TagDate = null)
    {
        public bool Success => !string.IsNullOrWhiteSpace(Sha);
    }

    readonly record struct CompiledIgnoreActionEntry(Regex NameRegex, Regex RefRegex);
}
