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
    private readonly Dictionary<string, string[]?> _cache = new(PathComparer);

    public LocalActionOutputResolver(string workflowFilePath)
    {
        _workflowFilePath = workflowFilePath;
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

        var relativePath = DecodeAscii(usesValue).Replace('/', Path.DirectorySeparatorChar);

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
        var baseDirectory = ResolveLocalReferenceBaseDirectory(_workflowFilePath, relativePath);
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return null;
        }

        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(Path.Combine(baseDirectory, TrimCurrentDirectoryPrefix(relativePath)));
        }
        catch
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

        var parseResult = WorkflowParser.Parse(bytes, actionYamlPath);
        if (parseResult.HasFatalError || parseResult.ActionMetadata is null)
        {
            return null;
        }

        var meta = parseResult.ActionMetadata;
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
                return yml;
            }

            var yaml = Path.Combine(resolvedPath, "action.yaml");
            if (File.Exists(yaml))
            {
                return yaml;
            }
        }

        return null;
    }

    private static string ResolveLocalReferenceBaseDirectory(string workflowFilePath, string localPath)
    {
        var workflowDirectory = Path.GetDirectoryName(workflowFilePath);
        if (string.IsNullOrEmpty(workflowDirectory))
        {
            return string.Empty;
        }

        if (localPath.StartsWith($".{Path.DirectorySeparatorChar}.github{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && TryGetRepositoryRoot(workflowFilePath, out var repositoryRoot))
        {
            return repositoryRoot;
        }

        return workflowDirectory;
    }

    private static bool TryGetRepositoryRoot(string workflowFilePath, out string repositoryRoot)
    {
        var separator = Path.DirectorySeparatorChar;
        var marker = $"{separator}.github{separator}workflows{separator}";
        var index = workflowFilePath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            repositoryRoot = workflowFilePath[..index];
            return true;
        }

        var markerAtEnd = $"{separator}.github{separator}workflows";
        if (workflowFilePath.EndsWith(markerAtEnd, StringComparison.OrdinalIgnoreCase))
        {
            repositoryRoot = workflowFilePath[..^markerAtEnd.Length];
            return true;
        }

        repositoryRoot = string.Empty;
        return false;
    }

    private static string TrimCurrentDirectoryPrefix(string path)
    {
        if (path.StartsWith($".{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return path[2..];
        }

        return path;
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
