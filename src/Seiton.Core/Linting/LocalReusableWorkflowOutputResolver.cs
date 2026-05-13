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
    private readonly string _workflowFilePath;
    private readonly Dictionary<string, string[]?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LocalReusableWorkflowOutputResolver(string workflowFilePath)
    {
        _workflowFilePath = workflowFilePath;
    }

    /// <summary>
    /// Given a local reusable workflow uses reference (e.g. "./.github/workflows/reusable.yml"),
    /// returns the output names declared in <c>on.workflow_call.outputs</c>,
    /// or null if the workflow cannot be resolved.
    /// Returns empty array when the workflow is resolved but declares no outputs.
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

        if (!File.Exists(resolvedPath))
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

        var parseResult = WorkflowParser.Parse(bytes, resolvedPath);
        if (parseResult.HasFatalError || parseResult.Workflow is null)
        {
            return null;
        }

        WorkflowCallEvent? workflowCallEvent = null;
        for (var i = 0; i < parseResult.Workflow.On.Count; i++)
        {
            if (parseResult.Workflow.On[i] is WorkflowCallEvent wce)
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
