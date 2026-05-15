using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

/// <summary>
/// Resolves local reusable workflow outputs for building needs context types.
/// Caches parsed metadata across calls within a single workflow.
/// </summary>
internal sealed class LocalReusableWorkflowOutputResolver
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _workflowDirectory;
    private readonly string? _repositoryRoot;
    private readonly Dictionary<string, string[]?> _cache = new(PathComparer);

    public LocalReusableWorkflowOutputResolver(string workflowFilePath)
    {
        _workflowDirectory = ActionRefHelpers.NormalizePath(Path.GetDirectoryName(workflowFilePath) ?? string.Empty);
        var normalizedWorkflowPath = ActionRefHelpers.NormalizePath(workflowFilePath);
        _repositoryRoot = ActionRefHelpers.TryGetRepositoryRoot(normalizedWorkflowPath, out var repositoryRoot)
            ? repositoryRoot
            : null;
    }

    /// <summary>
    /// Maximum file size (2 MB) the resolver will attempt to read.
    /// Files larger than this are assumed non-workflow and skipped.
    /// </summary>
    private const long MaxFileSizeBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Given a local reusable workflow uses reference (e.g. "./.github/workflows/reusable.yml"),
    /// returns the output names declared in <c>on.workflow_call.outputs</c>,
    /// or null if the workflow cannot be resolved.
    /// Returns empty array when the workflow is resolved but declares no outputs.
    /// </summary>
    public string[]? ResolveOutputNames(ReadOnlySpan<byte> usesValue)
    {
        // GitHub Actions local reusable workflows must start with "./"
        if (!usesValue.StartsWith("./"u8))
        {
            return null;
        }

        if (usesValue.IndexOf((byte)'@') >= 0)
        {
            return null;
        }

        // Require .yml or .yaml extension (case-insensitive on the raw bytes)
        if (!EndsWithYmlExtension(usesValue))
        {
            return null;
        }

        var relativePath = ActionRefHelpers.NormalizePath(DecodeAscii(usesValue));

        if (_cache.TryGetValue(relativePath, out var cached))
        {
            return cached;
        }

        // Normalize to full path for cache key to maximize cache hits when
        // semantically equivalent paths differ in raw form (e.g., extra ./ segments).
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

        // Guard against path traversal: resolved path must remain under the base directory.
        // Use Path.GetRelativePath which is filesystem-aware and avoids case-sensitivity issues
        // with string prefix comparisons on case-sensitive platforms.
        var relativeToBase = Path.GetRelativePath(baseDirectory, resolvedPath);
        var isTraversal = string.Equals(relativeToBase, "..", StringComparison.Ordinal)
            || relativeToBase.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeToBase.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        if (isTraversal || Path.IsPathRooted(relativeToBase))
        {
            return null;
        }

        if (!File.Exists(resolvedPath))
        {
            return null;
        }

        // Skip oversized files to avoid performance/availability issues
        try
        {
            var fileInfo = new FileInfo(resolvedPath);
            if (fileInfo.Length > MaxFileSizeBytes)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(resolvedPath);
        }
        catch
        {
            return null;
        }

        var parseHandle = WorkflowParser.Parse(bytes, resolvedPath);
        using var _ = parseHandle;
        if (parseHandle.HasFatalError || parseHandle.Workflow is null)
        {
            return null;
        }

        WorkflowCallEvent? workflowCallEvent = null;
        for (var i = 0; i < parseHandle.Workflow.On.Count; i++)
        {
            if (parseHandle.Workflow.On[i] is WorkflowCallEvent wce)
            {
                workflowCallEvent = wce;
                break;
            }
        }

        if (workflowCallEvent is null)
        {
            return null;
        }

        if (workflowCallEvent.Outputs is not { Count: > 0 } outputs)
        {
            return [];
        }

        var names = new string[outputs.Count];
        var idx = 0;
        foreach (var kv in outputs)
        {
            names[idx++] = Encoding.UTF8.GetString(kv.Key.AsSpan(bytes));
        }

        return names;
    }

    private string ResolveLocalReferenceBaseDirectory(string localPath)
    {
        if (string.IsNullOrEmpty(_workflowDirectory))
        {
            return string.Empty;
        }

        if (_repositoryRoot is not null
            && localPath.StartsWith("./.github/", StringComparison.Ordinal))
        {
            return _repositoryRoot;
        }

        return _workflowDirectory;
    }

    private static string DecodeAscii(ReadOnlySpan<byte> utf8)
    {
        var chars = new char[utf8.Length];
        for (var i = 0; i < utf8.Length; i++)
        {
            chars[i] = (char)utf8[i];
        }

        return new string(chars);
    }

    private static bool EndsWithYmlExtension(ReadOnlySpan<byte> value)
    {
        // .yml (case-insensitive)
        if (value.Length >= 4)
        {
            var tail = value[^4..];
            if (tail[0] == (byte)'.'
                && (tail[1] == (byte)'y' || tail[1] == (byte)'Y')
                && (tail[2] == (byte)'m' || tail[2] == (byte)'M')
                && (tail[3] == (byte)'l' || tail[3] == (byte)'L'))
            {
                return true;
            }
        }

        // .yaml (case-insensitive)
        if (value.Length >= 5)
        {
            var tail = value[^5..];
            if (tail[0] == (byte)'.'
                && (tail[1] == (byte)'y' || tail[1] == (byte)'Y')
                && (tail[2] == (byte)'a' || tail[2] == (byte)'A')
                && (tail[3] == (byte)'m' || tail[3] == (byte)'M')
                && (tail[4] == (byte)'l' || tail[4] == (byte)'L'))
            {
                return true;
            }
        }

        return false;
    }
}
