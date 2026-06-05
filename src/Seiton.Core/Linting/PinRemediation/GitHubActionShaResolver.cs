using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Seiton.Core.Linting.PinRemediation;

/// <summary>Resolves GitHub Actions references to commit SHAs via the GitHub API for pinning remediation.</summary>
public sealed class GitHubActionShaResolver(HttpClient httpClient, FixPinningConfig pinningConfig, GitHubNetworkConfig githubConfig) : IActionShaResolver
{
    private static readonly Uri PublicApiBaseUri = new("https://api.github.com/");
    private static readonly string[] TokenEnvVars = ["SEITON_GITHUB_TOKEN", "GITHUB_TOKEN"];

    private readonly HttpClient _httpClient = httpClient;
    private readonly FixPinningConfig _pinningConfig = pinningConfig;
    private readonly GitHubNetworkConfig _githubConfig = githubConfig;
    private readonly ConcurrentDictionary<string, CachedResolution> _successCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _canonicalTagByShaCache = new(StringComparer.Ordinal);
    private readonly string[] _excludeBranches = ToExcludeBranchArray(pinningConfig.ExcludeBranches);
    private readonly CompiledIgnoreActionEntry[] _compiledIgnoreActions = CompileIgnoreActions(pinningConfig.IgnoreActions);

    public async Task<ActionShaResolution> ResolveAsync(
        string owner,
        string repo,
        string refStr,
        CancellationToken cancellationToken = default)
    {
        if (ShouldSkip(owner, repo, refStr))
        {
            return ActionShaResolution.Skipped($"pinning skipped by fix.pinning exclude settings for '{owner}/{repo}@{refStr}'");
        }

        var cacheKey = string.Concat(owner, "/", repo, "@", refStr);
        if (_successCache.TryGetValue(cacheKey, out var cached))
        {
            return ActionShaResolution.Resolved(cached.Sha, cached.TagComment);
        }

        var token = ResolveToken();
        var resolvedRef = refStr;

        if (_pinningConfig.MinAgeDays > 0 && TryBuildVersionFamily(refStr, out var family))
        {
            var selectedTag = await SelectBestEligibleTagAsync(owner, repo, family, token, cancellationToken);
            if (string.IsNullOrWhiteSpace(selectedTag))
            {
                return ActionShaResolution.Skipped(
                    $"pinning skipped: no eligible tag satisfies fix.pinning.min-age-days={_pinningConfig.MinAgeDays} for '{owner}/{repo}@{refStr}'");
            }

            resolvedRef = selectedTag;
        }

        var result = await ResolveShaWithFallbackAsync(owner, repo, resolvedRef, token, cancellationToken);
        if (_pinningConfig.PreferCanonicalTagComment
            && TryBuildVersionFamily(refStr, out var refFamily)
            && refFamily.Parts.Length < 3)
        {
            var promoted = await TryPromoteTagCommentForBranchAliasAsync(
                owner,
                repo,
                result.Sha!,
                refFamily,
                token,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(promoted))
            {
                resolvedRef = promoted;
            }
        }

        if (_pinningConfig.MinAgeDays > 0 && !TryBuildVersionFamily(refStr, out _))
        {
            if (result.TagDate is not null)
            {
                var age = DateTimeOffset.UtcNow - result.TagDate.Value;
                if (age.TotalDays < _pinningConfig.MinAgeDays)
                {
                    return ActionShaResolution.Skipped(
                        $"pinning skipped: resolved ref '{resolvedRef}' is younger than fix.pinning.min-age-days={_pinningConfig.MinAgeDays} for '{owner}/{repo}@{refStr}'");
                }
            }
        }

        var cacheValue = new CachedResolution(result.Sha!, resolvedRef);
        _successCache.TryAdd(cacheKey, cacheValue);
        return ActionShaResolution.Resolved(cacheValue.Sha, cacheValue.TagComment);
    }

    private async Task<string?> SelectBestEligibleTagAsync(
        string owner,
        string repo,
        VersionFamily family,
        string token,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_pinningConfig.MinAgeDays);
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

    private async Task<List<string>> TryGetReleaseCandidatesAsync(
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

    private async Task<List<string>> TryGetTagCandidatesAsync(
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
                if (commitDate is null || commitDate.Value > cutoff)
                {
                    continue;
                }

                candidates.Add(tagName);
            }
        }

        return candidates;
    }

    private static string? PickBestTag(List<string> candidates)
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

    private async Task<HttpResponseMessage?> SendGetWithFallbackAsync(
        string owner,
        string repo,
        string relativePath,
        string token,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_githubConfig.GhesApiUrl))
        {
            var ghesBaseUri = NormalizeApiBaseUri(_githubConfig.GhesApiUrl!);
            var ghesResponse = await SendGetAsync(ghesBaseUri, relativePath, token, cancellationToken);
            if (ghesResponse.IsSuccessStatusCode)
            {
                return ghesResponse;
            }

            if (!_githubConfig.GhesFallback || ghesResponse.StatusCode != HttpStatusCode.NotFound)
            {
                return ghesResponse;
            }

            ghesResponse.Dispose();
        }

        return await SendGetAsync(PublicApiBaseUri, relativePath, token, cancellationToken);
    }

    private async Task<string?> TryPromoteTagCommentForBranchAliasAsync(
        string owner,
        string repo,
        string resolvedSha,
        VersionFamily family,
        string token,
        CancellationToken cancellationToken)
    {
        var cacheKey = string.Concat(owner, "/", repo, "@", resolvedSha, "|", BuildFamilyCacheKey(family));
        if (_canonicalTagByShaCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var path = $"repos/{owner}/{repo}/tags?per_page=100";
        var response = await SendGetWithFallbackAsync(owner, repo, path, token, cancellationToken);
        if (response is null)
        {
            return null;
        }

        string? bestTag = null;
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var root = document.RootElement;
            for (var i = 0; i < root.GetArrayLength(); i++)
            {
                var tag = root[i];
                if (!tag.TryGetProperty("name", out var tagNameNode) ||
                    !tag.TryGetProperty("commit", out var commitNode) ||
                    !commitNode.TryGetProperty("sha", out var shaNode))
                {
                    continue;
                }

                var tagName = tagNameNode.GetString();
                var commitSha = shaNode.GetString();
                if (string.IsNullOrWhiteSpace(tagName) ||
                    string.IsNullOrWhiteSpace(commitSha) ||
                    !string.Equals(commitSha, resolvedSha, StringComparison.OrdinalIgnoreCase) ||
                    !family.IsMatch(tagName))
                {
                    continue;
                }

                if (bestTag is null || CompareVersionTag(tagName, bestTag) > 0)
                {
                    bestTag = tagName;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(bestTag))
        {
            _canonicalTagByShaCache.TryAdd(cacheKey, bestTag);
            return bestTag;
        }

        return null;
    }

    private static string BuildFamilyCacheKey(VersionFamily family)
    {
        var key = family.HasVPrefix ? "v" : string.Empty;
        for (var i = 0; i < family.Parts.Length; i++)
        {
            if (i > 0)
            {
                key += ".";
            }

            key += family.Parts[i].ToString();
        }

        return key;
    }

    private async Task<DateTimeOffset?> TryGetCommitDateWithFallbackAsync(
        string owner,
        string repo,
        string commitSha,
        string token,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_githubConfig.GhesApiUrl))
        {
            var ghesBaseUri = NormalizeApiBaseUri(_githubConfig.GhesApiUrl!);
            var ghesDate = await TryGetCommitDateAsync(ghesBaseUri, owner, repo, commitSha, token, cancellationToken);
            if (ghesDate is not null)
            {
                return ghesDate;
            }

            if (!_githubConfig.GhesFallback)
            {
                return null;
            }
        }

        return await TryGetCommitDateAsync(PublicApiBaseUri, owner, repo, commitSha, token, cancellationToken);
    }

    private bool ShouldSkip(string owner, string repo, string refStr)
    {
        if (MatchesExcludedBranch(refStr))
        {
            return true;
        }

        var name = owner + "/" + repo;
        for (var i = 0; i < _compiledIgnoreActions.Length; i++)
        {
            var entry = _compiledIgnoreActions[i];
            if (ActionRefHelpers.WildcardMatch(name, entry.NamePattern) && ActionRefHelpers.WildcardMatch(refStr, entry.RefPattern))
            {
                return true;
            }
        }

        return false;
    }

    private bool MatchesExcludedBranch(string refStr)
    {
        for (var i = 0; i < _excludeBranches.Length; i++)
        {
            if (string.Equals(_excludeBranches[i], refStr, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private string ResolveToken()
    {
        foreach (var envVar in TokenEnvVars)
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

    private async Task<ResolveAttemptResult> ResolveShaWithFallbackAsync(
        string owner,
        string repo,
        string refStr,
        string token,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_githubConfig.GhesApiUrl))
        {
            var ghesBaseUri = NormalizeApiBaseUri(_githubConfig.GhesApiUrl!);
            var ghesResult = await TryResolveShaAsync(ghesBaseUri, owner, repo, refStr, token, cancellationToken);
            if (ghesResult.Success)
            {
                return ghesResult;
            }

            if (!_githubConfig.GhesFallback || ghesResult.StatusCode != HttpStatusCode.NotFound)
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


    private async Task<ResolveAttemptResult> TryResolveShaAsync(
        Uri apiBaseUri,
        string owner,
        string repo,
        string refStr,
        string token,
        CancellationToken cancellationToken)
    {
        var escapedRef = Uri.EscapeDataString(refStr);
        var tagRefPath = $"repos/{owner}/{repo}/git/ref/tags/{escapedRef}";
        var refResult = await TryResolveFromGitRefPathAsync(apiBaseUri, owner, repo, tagRefPath, token, cancellationToken);
        if (refResult.Success)
        {
            return refResult;
        }

        // Branch fallback: tags-only resolution fails for refs like "v1" when the repository
        // uses a moving branch alias instead of creating a "v1" tag.
        if (refResult.StatusCode == HttpStatusCode.NotFound)
        {
            var branchRefPath = $"repos/{owner}/{repo}/git/ref/heads/{escapedRef}";
            var branchResult = await TryResolveFromGitRefPathAsync(apiBaseUri, owner, repo, branchRefPath, token, cancellationToken);
            if (branchResult.Success)
            {
                return branchResult with { UsedBranchFallback = true };
            }

            return branchResult;
        }

        return refResult;
    }

    private async Task<ResolveAttemptResult> TryResolveFromGitRefPathAsync(
        Uri apiBaseUri,
        string owner,
        string repo,
        string refPath,
        string token,
        CancellationToken cancellationToken)
    {
        using var refResponse = await SendGetAsync(apiBaseUri, refPath, token, cancellationToken);
        if (!refResponse.IsSuccessStatusCode)
        {
            return new ResolveAttemptResult(null, refResponse.StatusCode);
        }

        await using var refStream = await refResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var refDocument = await JsonDocument.ParseAsync(refStream, cancellationToken: cancellationToken);
        var objectNode = refDocument.RootElement.GetProperty("object");
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

    private async Task<DateTimeOffset?> TryGetCommitDateAsync(
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

    private async Task<HttpResponseMessage> SendGetAsync(
        Uri apiBaseUri,
        string relativePath,
        string token,
        CancellationToken cancellationToken)
    {
        var client = _httpClient;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(apiBaseUri, relativePath));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Seiton", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static Uri NormalizeApiBaseUri(string apiUrl) => GitHubEnterpriseApiBase.ToRequestBaseUri(apiUrl);

    private static string[] ToExcludeBranchArray(IReadOnlyList<string> branches)
    {
        if (branches.Count == 0)
        {
            return [];
        }

        var result = new string[branches.Count];
        for (var i = 0; i < branches.Count; i++)
        {
            result[i] = branches[i];
        }

        return result;
    }

    private static CompiledIgnoreActionEntry[] CompileIgnoreActions(IReadOnlyList<IgnoreActionEntry> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var compiled = new CompiledIgnoreActionEntry[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            compiled[i] = new CompiledIgnoreActionEntry(entry.NamePattern, entry.RefPattern);
        }

        return compiled;
    }

    private static InvalidOperationException CreateResolutionException(
        string owner,
        string repo,
        string refStr,
        HttpStatusCode statusCode,
        Uri apiBaseUri)
    {
        return new InvalidOperationException(
            $"Failed to resolve GitHub action SHA for '{owner}/{repo}@{refStr}' via '{apiBaseUri}' (status {(int)statusCode}).");
    }

    private readonly record struct ResolveAttemptResult(
        string? Sha,
        HttpStatusCode StatusCode,
        DateTimeOffset? TagDate = null,
        bool UsedBranchFallback = false)
    {
        public bool Success => !string.IsNullOrWhiteSpace(Sha);
    }

    private readonly record struct CachedResolution(string Sha, string TagComment);

    private readonly record struct VersionFamily(bool HasVPrefix, int[] Parts)
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

    private static bool TryBuildVersionFamily(string refStr, out VersionFamily family)
    {
        family = default;
        if (!TryParseVersionTag(refStr, out var parsed))
        {
            return false;
        }

        family = new VersionFamily(parsed.HasVPrefix, parsed.Parts);
        return true;
    }

    private static int CompareVersionTag(string left, string right)
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

    private static bool TryParseVersionTag(string value, out ParsedVersionTag parsed)
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

    private readonly record struct ParsedVersionTag(bool HasVPrefix, int[] Parts, bool IsPrerelease);

    private readonly record struct CompiledIgnoreActionEntry(string NamePattern, string RefPattern);
}
