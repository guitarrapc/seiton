using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>
/// Cross-file validation of job-scoped exclusion entries against discovered workflow files.
/// Used by <c>seiton validate-config</c> to catch unknown job-ids before lint runs.
/// </summary>
public static class ExclusionJobIdValidator
{
    /// <summary>Statistics from a validation pass (for verbose logging).</summary>
    public readonly record struct ValidationStats(int WorkflowsScanned, int JobScopedExclusionsChecked);

    /// <summary>
    /// Validates exclusion <c>jobs</c> entries against <paramref name="workflowFilePaths"/>.
    /// Returns configuration diagnostics with <paramref name="configDiagnosticPath"/> as <see cref="Diagnostic.FilePath"/>.
    /// </summary>
    public static Diagnostic[] Validate(
        LintConfig? config,
        IReadOnlyList<string> workflowFilePaths,
        string configDiagnosticPath,
        out ValidationStats stats)
    {
        stats = default;
        if (config?.Exclusions is not { Count: > 0 } exclusions)
        {
            return [];
        }

        var configPath = config.ConfigFilePath ?? configDiagnosticPath;
        var diagnostics = new List<Diagnostic>();
        var pathsToParse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobScopedMatches = new List<(IReadOnlyList<string> Jobs, List<string> MatchingPaths)>();
        var jobScopedCount = 0;

        for (var i = 0; i < exclusions.Count; i++)
        {
            var exclusion = exclusions[i];
            if (exclusion.Jobs is not { Count: > 0 } jobs)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(exclusion.File))
            {
                continue;
            }

            jobScopedCount++;
            var matchingPaths = CollectMatchingWorkflowPaths(exclusion.File, workflowFilePaths);
            if (matchingPaths.Count == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    $"exclusion file pattern '{exclusion.File.Trim()}' matches no discovered workflow files",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: configPath));
                continue;
            }

            jobScopedMatches.Add((jobs, matchingPaths));
            for (var p = 0; p < matchingPaths.Count; p++)
            {
                pathsToParse.Add(matchingPaths[p]);
            }
        }

        if (pathsToParse.Count == 0)
        {
            stats = new ValidationStats(0, jobScopedCount);
            return diagnostics.Count == 0 ? [] : [.. diagnostics];
        }

        var jobIdsByPath = new Dictionary<string, HashSet<string>?>(pathsToParse.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var workflowPath in pathsToParse)
        {
            jobIdsByPath[workflowPath] = TryExtractJobIds(workflowPath);
        }

        stats = new ValidationStats(pathsToParse.Count, jobScopedCount);

        for (var i = 0; i < jobScopedMatches.Count; i++)
        {
            var (jobs, matchingPaths) = jobScopedMatches[i];
            var reportedUnknownJobIds = new HashSet<string>(StringComparer.Ordinal);
            for (var p = 0; p < matchingPaths.Count; p++)
            {
                if (!jobIdsByPath.TryGetValue(matchingPaths[p], out var knownJobIds)
                    || knownJobIds is null
                    || knownJobIds.Count == 0)
                {
                    continue;
                }

                for (var j = 0; j < jobs.Count; j++)
                {
                    var jobId = jobs[j];
                    if (string.IsNullOrEmpty(jobId) || knownJobIds.Contains(jobId))
                    {
                        continue;
                    }

                    if (!reportedUnknownJobIds.Add(jobId))
                    {
                        continue;
                    }

                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        $"unknown job-id '{jobId}' in exclusion configuration",
                        new TextRange(0, jobId.Length, 1, 1, 1, 1 + jobId.Length),
                        FilePath: configPath));
                }
            }
        }

        return diagnostics.Count == 0 ? [] : [.. diagnostics];
    }

    private static List<string> CollectMatchingWorkflowPaths(string filePattern, IReadOnlyList<string> workflowFilePaths)
    {
        if (workflowFilePaths.Count == 0)
        {
            return [];
        }

        var matches = new List<string>();
        for (var i = 0; i < workflowFilePaths.Count; i++)
        {
            var path = workflowFilePaths[i];
            if (path == "-" || !ExclusionMatcher.MatchesWorkflowFile(filePattern, path))
            {
                continue;
            }

            matches.Add(path);
        }

        return matches;
    }

    private static HashSet<string>? TryExtractJobIds(string workflowFilePath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(workflowFilePath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        using var parseResult = WorkflowParser.Parse(bytes, workflowFilePath);
        var workflow = parseResult.Workflow;
        if (workflow is null)
        {
            return null;
        }

        HashSet<string>? jobIds = null;
        foreach (var pair in workflow.Jobs)
        {
            var id = pair.Value.Id;
            if (!id.HasValue)
            {
                continue;
            }

            jobIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            jobIds.Add(parseResult.GetString(id));
        }

        return jobIds;
    }
}
