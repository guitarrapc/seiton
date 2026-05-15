using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>
/// Resolves local action metadata (outputs) for building step context types.
/// Caches parsed metadata across calls within a single workflow.
/// </summary>
internal sealed class LocalActionOutputResolver
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _workflowFilePath;
    private readonly string _workflowDirectory;
    private readonly string? _repositoryRoot;
    private readonly Dictionary<string, string[]?> _cache = new(PathComparer);

    public LocalActionOutputResolver(string workflowFilePath)
    {
        _workflowFilePath = ActionRefHelpers.NormalizePath(workflowFilePath);
        _workflowDirectory = ActionRefHelpers.NormalizePath(Path.GetDirectoryName(workflowFilePath) ?? string.Empty);
        _repositoryRoot = ActionRefHelpers.TryGetRepositoryRoot(_workflowFilePath, out var repositoryRoot)
            ? repositoryRoot
            : null;
    }

    /// <summary>
    /// Given a local action uses reference (e.g. "./.github/actions/my-action"), returns the output names
    /// declared in the action metadata, or null if the action cannot be resolved.
    /// </summary>
    public string[]? ResolveOutputNames(ReadOnlySpan<byte> usesValue)
    {
        if (!usesValue.StartsWith("./"u8) && !usesValue.StartsWith("../"u8))
        {
            return null;
        }

        if (usesValue.IndexOf((byte)'@') >= 0)
        {
            return null;
        }

        var relativePath = ActionRefHelpers.NormalizePath(DecodeAscii(usesValue));

        if (_cache.TryGetValue(relativePath, out var cached))
        {
            return cached;
        }

        var result = ResolveAndParse(relativePath);
        _cache[relativePath] = result;
        return result;
    }

    private string[]? ResolveAndParse(string relativePath)
    {
        var baseDirectory = ResolveLocalReferenceBaseDirectory(relativePath);
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return null;
        }

        var resolvedPath = ActionRefHelpers.NormalizeFullPath(baseDirectory, relativePath);
        if (resolvedPath is null)
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

        var parseHandle = WorkflowParser.Parse(bytes, actionYamlPath);
        if (parseHandle.HasFatalError || parseHandle.ActionMetadata is null)
        {
            parseHandle.Dispose();
            return null;
        }

        var meta = parseHandle.ActionMetadata;
        if (meta.Outputs is null || meta.Outputs.Value.Count == 0)
        {
            return [];
        }

        var names = new string[meta.Outputs.Value.Count];
        var idx = 0;
        foreach (var kv in meta.Outputs.Value)
        {
            names[idx++] = Encoding.UTF8.GetString(kv.Key.AsSpan(bytes));
        }

        parseHandle.Dispose();
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
}
