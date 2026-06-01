using Seiton.Cli;

namespace Seiton.Commands;

internal static class InputDiscovery
{
    /// <summary>
    /// Discover files from given arguments, or auto-discover from .github/workflows/.
    /// </summary>
    public static string[] ResolveFiles(
        string[] files,
        bool includeActions,
        VerboseLogger verboseLogger,
        bool skipAgenticWorkflows = false,
        string? startDirectory = null)
    {
        var startDir = startDirectory ?? Environment.CurrentDirectory;

        string[] resolved;
        if (files.Length > 0)
        {
            resolved = ExpandFileArgs(files);
            if (verboseLogger.IsEnabled)
            {
                verboseLogger.Log("discovery", $"{resolved.Length} file(s) from explicit args");
            }
        }
        else
        {
            resolved = DiscoverFiles(includeActions, verboseLogger, startDir);
        }

        if (!skipAgenticWorkflows || resolved.Length == 0)
        {
            return resolved;
        }

        return FilterAgenticWorkflows(resolved, verboseLogger);
    }

    private static string[] FilterAgenticWorkflows(string[] files, VerboseLogger verboseLogger)
    {
        var kept = new List<string>(files.Length);
        for (var i = 0; i < files.Length; i++)
        {
            var filePath = files[i];
            if (filePath == "-" || !AgenticWorkflowDetector.IsAgenticWorkflowFile(filePath))
            {
                kept.Add(filePath);
                continue;
            }

            if (verboseLogger.IsEnabled)
            {
                verboseLogger.Log("discovery", $"skipped {filePath} (agentic workflow)");
            }
        }

        return [.. kept];
    }

    private static string[] DiscoverFiles(bool includeActions, VerboseLogger verboseLogger, string startDir)
    {
        if (verboseLogger.IsEnabled)
        {
            verboseLogger.Log("discovery", $"searching from {startDir}");
        }

        var files = new List<string>();

        var workflowsDir = FindWorkflowsDirectory(startDir);
        if (workflowsDir is not null && Directory.Exists(workflowsDir))
        {
            if (verboseLogger.IsEnabled)
            {
                verboseLogger.Log("discovery", $"found {workflowsDir}");
            }
            files.AddRange(CollectYamlFiles(workflowsDir));
        }

        if (includeActions)
        {
            var actionsDir = FindActionsDirectory(startDir);
            if (actionsDir is not null && Directory.Exists(actionsDir))
            {
                if (verboseLogger.IsEnabled)
                {
                    verboseLogger.Log("discovery", $"found {actionsDir}");
                }
                files.AddRange(CollectYamlFiles(actionsDir));
            }
        }

        if (verboseLogger.IsEnabled)
        {
            verboseLogger.Log("discovery", $"{files.Count} file(s) resolved");
        }

        if (files.Count == 0)
        {
            return [];
        }

        files.Sort(StringComparer.Ordinal);
        return [.. files];
    }

    private static string? FindWorkflowsDirectory(string startDir)
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

    private static string? FindActionsDirectory(string startDir)
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

    private static string[] ExpandFileArgs(string[] args)
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

    private static string[] CollectYamlFiles(string directory)
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
