namespace Seiton.Commands;

internal static class InputDiscovery
{
    /// <summary>
    /// Discover workflow files from given arguments, or auto-discover from .github/workflows/.
    /// </summary>
    public static string[] ResolveFiles(string[] files)
    {
        if (files.Length > 0)
            return ExpandFileArgs(files);

        return DiscoverWorkflowFiles();
    }

    static string[] DiscoverWorkflowFiles()
    {
        var dir = FindWorkflowsDirectory(Environment.CurrentDirectory);
        if (dir is null || !Directory.Exists(dir))
            return [];

        return CollectYamlFiles(dir);
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
