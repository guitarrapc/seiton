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
        var startDir = Path.GetFullPath(startDirectory ?? Environment.CurrentDirectory);

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
            verboseLogger.Log("discovery", $"searching under cwd {startDir}");
        }

        var files = new List<string>();

        var workflowsDir = GetWorkflowsDirectory(startDir);
        if (workflowsDir is not null)
        {
            if (verboseLogger.IsEnabled)
            {
                verboseLogger.Log("discovery", $"found {workflowsDir}");
            }
            files.AddRange(CollectYamlFiles(workflowsDir));
        }

        if (includeActions)
        {
            var actionsDir = GetActionsDirectory(startDir);
            if (actionsDir is not null)
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

    private static string? GetWorkflowsDirectory(string startDir)
    {
        var candidate = Path.Combine(startDir, ".github", "workflows");
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static string? GetActionsDirectory(string startDir)
    {
        var candidate = Path.Combine(startDir, ".github", "actions");
        return Directory.Exists(candidate) ? candidate : null;
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
        CollectYamlFilesWithExtension(directory, ".yml", files);
        CollectYamlFilesWithExtension(directory, ".yaml", files);
        files.Sort(StringComparer.Ordinal);
        return [.. files];
    }

    private static void CollectYamlFilesWithExtension(string directory, string extension, List<string> files)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        foreach (var file in Directory.EnumerateFiles(directory, $"*{extension}", options))
        {
            files.Add(Path.GetFullPath(file));
        }
    }
}
