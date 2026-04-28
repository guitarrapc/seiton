using System.Text.Json;
using System.Text.RegularExpressions;

namespace Seiton.Update.Validators;

/// <summary>
/// Checks whether popular-actions targets.json references the latest major version
/// of each action by querying GitHub API for available tags.
/// </summary>
internal sealed partial class PopularActionsVersionValidator
{
    /// <summary>
    /// Validates that targets.json versions are up to date.
    /// Returns any stale entries.
    /// </summary>
    public async Task<PopularActionsVersionValidationResult> ValidateAsync(string repoRoot)
    {
        var configPath = Path.Combine(repoRoot, "data", "sources", "popular-actions", "targets.json");
        if (!File.Exists(configPath))
        {
            UpdateLogger.Warn("[validate:popular-actions:versions] targets.json not found.");
            return new PopularActionsVersionValidationResult();
        }

        var configText = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<TargetsConfig>(configText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Invalid targets.json: {configPath}");

        var targets = config.Targets ?? [];
        if (targets.Count == 0)
        {
            return new PopularActionsVersionValidationResult();
        }

        using var client = CreateHttpClient();
        var stale = new List<StaleActionVersion>();

        foreach (var target in targets)
        {
            var actionRef = (target.ActionRef ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(actionRef))
                continue;

            var atIdx = actionRef.LastIndexOf('@');
            if (atIdx < 0)
                continue;

            var ownerRepo = actionRef[..atIdx]; // e.g. "actions/checkout"
            var currentTag = actionRef[(atIdx + 1)..]; // e.g. "v6"

            if (!TryParseMajor(currentTag, out var currentMajor))
                continue;

            var latestMajor = await FindLatestMajorVersionAsync(client, ownerRepo);
            if (latestMajor is null)
            {
                UpdateLogger.Warn($"[validate:popular-actions:versions] could not resolve tags for {ownerRepo}");
                continue;
            }

            if (latestMajor.Value > currentMajor)
            {
                stale.Add(new StaleActionVersion(actionRef, currentMajor, latestMajor.Value));
            }
        }

        return new PopularActionsVersionValidationResult { StaleVersions = stale };
    }

    /// <summary>
    /// Queries GitHub API for tags and finds the highest major version (vN pattern).
    /// </summary>
    private static async Task<int?> FindLatestMajorVersionAsync(HttpClient client, string ownerRepo)
    {
        // Fetch tags sorted by version descending. We only need the first page to find
        // major version tags (v1, v2, ...) since they sort high alphabetically.
        // GitHub tags API returns in reverse-alphabetical order when using the refs endpoint.
        var url = $"https://api.github.com/repos/{ownerRepo}/tags?per_page=100";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            UpdateLogger.Warn($"[validate:popular-actions:versions] HTTP error for {ownerRepo}: {ex.Message}");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            UpdateLogger.Warn($"[validate:popular-actions:versions] GitHub API returned {(int)response.StatusCode} for {ownerRepo}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var tags = JsonSerializer.Deserialize<List<GitHubTag>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? [];

        var maxMajor = -1;
        foreach (var tag in tags)
        {
            if (TryParseMajor(tag.Name, out var major) && major > maxMajor)
            {
                maxMajor = major;
            }
        }

        return maxMajor >= 0 ? maxMajor : null;
    }

    /// <summary>
    /// Tries to parse a major version number from a tag like "v4" or "v12".
    /// Only matches exact major version tags (no dots, no suffixes).
    /// </summary>
    private static bool TryParseMajor(string tag, out int major)
    {
        major = 0;
        var match = MajorVersionTagRegex().Match(tag);
        if (!match.Success)
            return false;

        major = int.Parse(match.Groups[1].Value);
        return true;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(30);

        // Support GITHUB_TOKEN for higher rate limits (unauthenticated: 60/hr, authenticated: 5000/hr)
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    [GeneratedRegex(@"^v(\d+)$")]
    private static partial Regex MajorVersionTagRegex();

    private sealed class TargetsConfig
    {
        public List<TargetEntry>? Targets { get; set; }
    }

    private sealed class TargetEntry
    {
        public string? ActionRef { get; set; }
    }

    private sealed class GitHubTag
    {
        public string Name { get; set; } = string.Empty;
    }
}

internal sealed class PopularActionsVersionValidationResult
{
    public IReadOnlyList<StaleActionVersion> StaleVersions { get; init; } = [];
    public bool HasFindings => StaleVersions.Count > 0;
}

internal sealed record StaleActionVersion(string ActionRef, int CurrentMajor, int LatestMajor);
