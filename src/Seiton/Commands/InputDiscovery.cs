namespace Seiton.Commands;

internal static class InputDiscovery
{
    /// <summary>
    /// Discover files from given arguments, or auto-discover from .github/workflows/.
    /// </summary>
    public static string[] ResolveFiles(string[] files, bool includeActions)
    {
        if (files.Length > 0)
            return ExpandFileArgs(files);

        return DiscoverFiles(includeActions);
    }

    static string[] DiscoverFiles(bool includeActions)
    {
        var files = new List<string>();

        var workflowsDir = FindWorkflowsDirectory(Environment.CurrentDirectory);
        if (workflowsDir is not null && Directory.Exists(workflowsDir))
        {
            files.AddRange(CollectYamlFiles(workflowsDir));
        }

        if (includeActions)
        {
            var actionsDir = FindActionsDirectory(Environment.CurrentDirectory);
            if (actionsDir is not null && Directory.Exists(actionsDir))
            {
                files.AddRange(CollectYamlFiles(actionsDir));
            }
        }

        if (files.Count == 0)
        {
            return [];
        }

        files.Sort(StringComparer.Ordinal);
        return [.. files];
    }

    static string? FindWorkflowsDirectory(string startDir)
    {
        var current = startDir;
        while (current is not null)
        {
            var candidate = Path.Combine(current, ".github", "workflows");
            if (Directory.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(current);
            current = parent?.FullName;
        }

        return null;
    }

    static string? FindActionsDirectory(string startDir)
    {
        var current = startDir;
        while (current is not null)
        {
            var candidate = Path.Combine(current, ".github", "actions");
            if (Directory.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(current);
            current = parent?.FullName;
        }

        return null;
    }

    static string[] ExpandFileArgs(string[] args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "-")
            {
                result.Add("-");
                continue;
            }

            if (Directory.Exists(arg))
            {
                result.AddRange(CollectYamlFiles(arg));
                continue;
            }

            if (!File.Exists(arg))
                throw new FileNotFoundException($"file not found: {arg}", arg);

            result.Add(Path.GetFullPath(arg));
        }

        return [.. result];
    }

    static string[] CollectYamlFiles(string directory)
    {
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(Path.GetFullPath(file));
            }
        }

        files.Sort(StringComparer.Ordinal);
        return [.. files];
    }
}
