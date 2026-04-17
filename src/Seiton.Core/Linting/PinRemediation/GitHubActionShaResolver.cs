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
    readonly ConcurrentDictionary<string, CachedResolution> _successCache = new(StringComparer.Ordinal);
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
        if (_successCache.TryGetValue(cacheKey, out var cached))
        {
            return (cached.Sha, cached.TagComment);
        }

        var token = ResolveToken();
        var resolvedRef = refStr;

        if (_config.MinAgeDays > 0 && TryBuildVersionFamily(refStr, out var family))
        {
            var selectedTag = await SelectBestEligibleTagAsync(owner, repo, family, token, cancellationToken);
            if (string.IsNullOrWhiteSpace(selectedTag))
            {
                return (null, null);
            }

            resolvedRef = selectedTag;
        }

        var result = await ResolveShaWithFallbackAsync(owner, repo, resolvedRef, token, cancellationToken);
        if (_config.MinAgeDays > 0 && !TryBuildVersionFamily(refStr, out _))
        {
            if (result.TagDate.HasValue)
            {
                var age = DateTimeOffset.UtcNow - result.TagDate.Value;
                if (age.TotalDays < _config.MinAgeDays)
                {
                    return (null, null);
                }
            }
        }

        var cacheValue = new CachedResolution(result.Sha!, resolvedRef);
        _successCache.TryAdd(cacheKey, cacheValue);
        return (cacheValue.Sha, cacheValue.TagComment);
    }

    async Task<string?> SelectBestEligibleTagAsync(
        string owner,
        string repo,
        VersionFamily family,
        string token,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_config.MinAgeDays);
        var releaseCandidates = await TryGetReleaseCandidatesAsync(owner, repo, family, cutoff, token, cancellationToken);
        if (releaseCandidates.Count > 0)
        {
            return PickBestTag(releaseCandidates);
        }

        var tagCandidates = await TryGetTagCandidatesAsync(owner, repo, family, cutoff, token, cancellationToken);
        if (tagCandidates.Count > 0)
        {
            return PickBestTag(tagCandidates);
        }

        return null;
    }

    async Task<List<string>> TryGetReleaseCandidatesAsync(
        string owner,
        string repo,
        VersionFamily family,
        DateTimeOffset cutoff,
        string token,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        var path = $"repos/{owner}/{repo}/releases?per_page=100";

        var response = await SendGetWithFallbackAsync(owner, repo, path, token, cancellationToken);
        if (response is null)
        {
            return candidates;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return candidates;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return candidates;
            }

            var root = document.RootElement;
            for (var i = 0; i < root.GetArrayLength(); i++)
            {
                var release = root[i];
                if (!release.TryGetProperty("tag_name", out var tagNameNode))
                {
                    continue;
                }

                var tagName = tagNameNode.GetString();
                if (string.IsNullOrWhiteSpace(tagName) || !family.IsMatch(tagName))
                {
                    continue;
                }

                if (!release.TryGetProperty("published_at", out var publishedNode) ||
                    !publishedNode.TryGetDateTimeOffset(out var publishedAt) ||
                    publishedAt > cutoff)
                {
                    continue;
                }

                candidates.Add(tagName);
            }
        }

        return candidates;
    }

    async Task<List<string>> TryGetTagCandidatesAsync(
        string owner,
        string repo,
        VersionFamily family,
        DateTimeOffset cutoff,
        string token,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        var path = $"repos/{owner}/{repo}/tags?per_page=100";

        var response = await SendGetWithFallbackAsync(owner, repo, path, token, cancellationToken);
        if (response is null)
        {
            return candidates;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return candidates;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return candidates;
            }

            var root = document.RootElement;
            for (var i = 0; i < root.GetArrayLength(); i++)
            {
                var tag = root[i];
                if (!tag.TryGetProperty("name", out var tagNameNode))
                {
                    continue;
                }

                var tagName = tagNameNode.GetString();
                if (string.IsNullOrWhiteSpace(tagName) || !family.IsMatch(tagName))
                {
                    continue;
                }

                if (!tag.TryGetProperty("commit", out var commitNode) ||
                    !commitNode.TryGetProperty("sha", out var shaNode))
                {
                    continue;
                }

                var commitSha = shaNode.GetString();
                if (string.IsNullOrWhiteSpace(commitSha))
                {
                    continue;
                }

                var commitDate = await TryGetCommitDateWithFallbackAsync(owner, repo, commitSha, token, cancellationToken);
                if (!commitDate.HasValue || commitDate.Value > cutoff)
                {
                    continue;
                }

                candidates.Add(tagName);
            }
        }

        return candidates;
    }

    static string? PickBestTag(List<string> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var best = candidates[0];
        for (var i = 1; i < candidates.Count; i++)
        {
            var current = candidates[i];
            if (CompareVersionTag(current, best) > 0)
            {
                best = current;
            }
        }

        return best;
    }

    async Task<HttpResponseMessage?> SendGetWithFallbackAsync(
        string owner,
        string repo,
        string relativePath,
        string token,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_config.GhesApiUrl))
        {
            var ghesBaseUri = NormalizeApiBaseUri(_config.GhesApiUrl!);
            var ghesResponse = await SendGetAsync(ghesBaseUri, relativePath, token, cancellationToken);
            if (ghesResponse.IsSuccessStatusCode)
            {
                return ghesResponse;
            }

            if (!_config.GhesFallback || ghesResponse.StatusCode != HttpStatusCode.NotFound)
            {
                return ghesResponse;
            }

            ghesResponse.Dispose();
        }

        return await SendGetAsync(PublicApiBaseUri, relativePath, token, cancellationToken);
    }

    async Task<DateTimeOffset?> TryGetCommitDateWithFallbackAsync(
        string owner,
        string repo,
        string commitSha,
        string token,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_config.GhesApiUrl))
        {
            var ghesBaseUri = NormalizeApiBaseUri(_config.GhesApiUrl!);
            var ghesDate = await TryGetCommitDateAsync(ghesBaseUri, owner, repo, commitSha, token, cancellationToken);
            if (ghesDate.HasValue)
            {
                return ghesDate;
            }

            if (!_config.GhesFallback)
            {
                return null;
            }
        }

        return await TryGetCommitDateAsync(PublicApiBaseUri, owner, repo, commitSha, token, cancellationToken);
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

    readonly record struct CachedResolution(string Sha, string TagComment);

    readonly record struct VersionFamily(bool HasVPrefix, int[] Parts)
    {
        public bool IsMatch(string candidate)
        {
            if (!TryParseVersionTag(candidate, out var parsed))
            {
                return false;
            }

            if (HasVPrefix != parsed.HasVPrefix || parsed.Parts.Length < Parts.Length)
            {
                return false;
            }

            for (var i = 0; i < Parts.Length; i++)
            {
                if (parsed.Parts[i] != Parts[i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    static bool TryBuildVersionFamily(string refStr, out VersionFamily family)
    {
        family = default;
        if (!TryParseVersionTag(refStr, out var parsed))
        {
            return false;
        }

        family = new VersionFamily(parsed.HasVPrefix, parsed.Parts);
        return true;
    }

    static int CompareVersionTag(string left, string right)
    {
        var leftParsed = TryParseVersionTag(left, out var l);
        var rightParsed = TryParseVersionTag(right, out var r);

        if (leftParsed && rightParsed)
        {
            var maxParts = Math.Max(l.Parts.Length, r.Parts.Length);
            for (var i = 0; i < maxParts; i++)
            {
                var lv = i < l.Parts.Length ? l.Parts[i] : 0;
                var rv = i < r.Parts.Length ? r.Parts[i] : 0;
                if (lv != rv)
                {
                    return lv.CompareTo(rv);
                }
            }

            if (l.IsPrerelease != r.IsPrerelease)
            {
                return l.IsPrerelease ? -1 : 1;
            }

            return string.CompareOrdinal(left, right);
        }

        if (leftParsed != rightParsed)
        {
            return leftParsed ? 1 : -1;
        }

        return string.CompareOrdinal(left, right);
    }

    static bool TryParseVersionTag(string value, out ParsedVersionTag parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan();
        var hasVPrefix = false;
        if (span.Length > 1 && (span[0] == 'v' || span[0] == 'V'))
        {
            hasVPrefix = true;
            span = span[1..];
        }

        var dashIndex = span.IndexOf('-');
        var isPrerelease = false;
        if (dashIndex >= 0)
        {
            isPrerelease = true;
            span = span[..dashIndex];
        }

        if (span.IsEmpty)
        {
            return false;
        }

        var text = span.ToString();
        var segments = text.Split('.');
        if (segments.Length is < 1 or > 3)
        {
            return false;
        }

        var parts = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], out var number) || number < 0)
            {
                return false;
            }

            parts[i] = number;
        }

        parsed = new ParsedVersionTag(hasVPrefix, parts, isPrerelease);
        return true;
    }

    readonly record struct ParsedVersionTag(bool HasVPrefix, int[] Parts, bool IsPrerelease);

    readonly record struct CompiledIgnoreActionEntry(Regex NameRegex, Regex RefRegex);
}
