using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>
/// Resolves local action metadata (outputs) for building step context types.
/// Caches parsed metadata across calls within a single workflow.
/// </summary>
internal sealed class LocalActionOutputResolver
{
    private readonly string _workflowDirectory;
    private readonly string? _repositoryRoot;
    private readonly string? _githubDirectoryRoot;
    private readonly Dictionary<string, string[]?> _cache = new(ActionRefHelpers.FileSystemPathComparer);

    public LocalActionOutputResolver(string workflowFilePath)
    {
        _workflowDirectory = ActionRefHelpers.NormalizePath(Path.GetDirectoryName(workflowFilePath) ?? string.Empty);
        var normalizedWorkflowPath = ActionRefHelpers.NormalizePath(workflowFilePath);
        _repositoryRoot = ActionRefHelpers.TryGetRepositoryRoot(normalizedWorkflowPath, out var repositoryRoot)
            ? repositoryRoot
            : null;
        _githubDirectoryRoot = ActionRefHelpers.TryGetGithubDirectoryRoot(normalizedWorkflowPath, out var githubDirectoryRoot)
            ? githubDirectoryRoot
            : null;
    }

    /// <summary>
    /// Given a local action uses reference (e.g. "./.github/actions/my-action" or "$/.github/actions/my-action"), returns the output names
    /// declared in the action metadata, or null if the action cannot be resolved.
    /// </summary>
    public string[]? ResolveOutputNames(ReadOnlySpan<byte> usesValue)
    {
        if (!ActionRefHelpers.IsLocalActionUses(usesValue))
        {
            return null;
        }

        if (usesValue.IndexOf((byte)'@') >= 0)
        {
            return null;
        }

        var relativePath = ActionRefHelpers.NormalizePath(DecodeUtf8(usesValue));

        if (_cache.TryGetValue(relativePath, out var cached))
        {
            return cached;
        }

        var normalizedKey = NormalizeCacheKey(relativePath);
        if (normalizedKey is not null && _cache.TryGetValue(normalizedKey, out cached))
        {
            _cache[relativePath] = cached;
            return cached;
        }

        var result = ResolveAndParse(relativePath, out var resolvedPath);
        _cache[relativePath] = result;
        if (normalizedKey is not null
            && !string.Equals(normalizedKey, relativePath, StringComparison.Ordinal))
        {
            _cache[normalizedKey] = result;
        }

        if (resolvedPath is not null
            && !string.Equals(resolvedPath, relativePath, StringComparison.Ordinal)
            && !string.Equals(resolvedPath, normalizedKey, StringComparison.Ordinal))
        {
            _cache[resolvedPath] = result;
        }

        return result;
    }

    private string? NormalizeCacheKey(string relativePath)
    {
        var baseDirectory = ResolveLocalReferenceBaseDirectory(relativePath);
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return null;
        }

        return ActionRefHelpers.NormalizeFullPath(baseDirectory, relativePath);
    }

    private string[]? ResolveAndParse(string relativePath, out string? resolvedPath)
    {
        resolvedPath = null;

        var baseDirectory = ResolveLocalReferenceBaseDirectory(relativePath);
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return null;
        }

        resolvedPath = ActionRefHelpers.NormalizeFullPath(baseDirectory, relativePath);
        if (resolvedPath is null)
        {
            return null;
        }

        // Guard against path traversal: resolved path must remain under the repository root
        // (or the base directory when the repository root is unknown).
        // Uses ../relative are valid within the repo, so check against repo root when available.
        var traversalBase = _githubDirectoryRoot ?? _repositoryRoot ?? baseDirectory;
        var relativeToBase = Path.GetRelativePath(traversalBase, resolvedPath);
        var isTraversal = string.Equals(relativeToBase, "..", StringComparison.Ordinal)
            || relativeToBase.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeToBase.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        if (isTraversal || Path.IsPathRooted(relativeToBase))
        {
            return null;
        }

        var actionYamlPath = FindActionYaml(resolvedPath);
        if (actionYamlPath is null)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(actionYamlPath);
        }
        catch
        {
            return null;
        }

        using var parseHandle = WorkflowParser.Parse(bytes, actionYamlPath);
        if (parseHandle.HasFatalError || parseHandle.ActionMetadataNode is null)
        {
            return null;
        }

        var outputs = parseHandle.ActionMetadata.Outputs;
        if (!outputs.HasValue || outputs.Count == 0)
        {
            return [];
        }

        var names = new string[outputs.Count];
        var idx = 0;
        foreach (var kv in outputs)
        {
            names[idx++] = Encoding.UTF8.GetString(kv.Key.Slice.AsSpan(bytes));
        }

        return names;
    }

    private static string? FindActionYaml(string resolvedPath)
    {
        if (File.Exists(resolvedPath))
        {
            var fileName = Path.GetFileName(resolvedPath);
            if (fileName.Equals("action.yml", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("action.yaml", StringComparison.OrdinalIgnoreCase))
            {
                return resolvedPath;
            }
        }

        if (Directory.Exists(resolvedPath))
        {
            var yml = Path.Combine(resolvedPath, "action.yml");
            if (File.Exists(yml))
            {
                return ActionRefHelpers.NormalizePath(yml);
            }

            var yaml = Path.Combine(resolvedPath, "action.yaml");
            if (File.Exists(yaml))
            {
                return ActionRefHelpers.NormalizePath(yaml);
            }
        }

        return null;
    }

    private string ResolveLocalReferenceBaseDirectory(string localPath)
    {
        if (string.IsNullOrEmpty(_workflowDirectory))
        {
            return string.Empty;
        }

        if (localPath.StartsWith("$/", StringComparison.Ordinal))
        {
            return _repositoryRoot ?? string.Empty;
        }

        var trimmedLocalPath = ActionRefHelpers.TrimCurrentDirectoryPrefix(localPath);
        if (_githubDirectoryRoot is not null
            && trimmedLocalPath.StartsWith(".github/", StringComparison.Ordinal))
        {
            return _githubDirectoryRoot;
        }

        return _workflowDirectory;
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> utf8)
    {
        return Encoding.UTF8.GetString(utf8);
    }
}
