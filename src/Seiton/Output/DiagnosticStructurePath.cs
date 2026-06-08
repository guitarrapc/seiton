using System.Runtime.CompilerServices;
using Seiton.Core.Parsing;

namespace Seiton.Output;

internal readonly struct DiagnosticStructurePath
{
    public DiagnosticStructurePath(
        bool hasJobs,
        string jobId,
        bool hasSteps,
        int stepIndex1Based,
        ReadOnlyMemory<char> remaining)
    {
        HasJobs = hasJobs;
        JobId = jobId;
        HasSteps = hasSteps;
        StepIndex1Based = stepIndex1Based;
        Remaining = remaining;
    }

    public bool HasJobs { get; }
    public string JobId { get; }
    public bool HasSteps { get; }
    public int StepIndex1Based { get; }
    public ReadOnlyMemory<char> Remaining { get; }

    public bool IsEmpty => !HasJobs && !HasSteps && Remaining.IsEmpty;

    public bool IsWorkflowScoped => HasJobs || HasSteps || !Remaining.IsEmpty;
}

internal static class DiagnosticStructurePathParser
{
    public static bool TryParse(Diagnostic diagnostic, out DiagnosticStructurePath path)
    {
        if (diagnostic.Metadata is not null
            && diagnostic.Metadata.TryGetValue(DiagnosticStructurePathMetadata.Key, out var metadataPath)
            && TryParsePath(metadataPath.AsSpan(), out path))
        {
            return true;
        }

        return TryParseMessage(diagnostic.Message.AsSpan(), out path);
    }

    public static bool TryParseMessage(ReadOnlySpan<char> message, out DiagnosticStructurePath path)
    {
        path = default;
        if (message.IsEmpty)
        {
            return false;
        }

        if (message.StartsWith("jobs.", StringComparison.Ordinal))
        {
            return TryParseJobsPath(message, out path);
        }

        if (message.StartsWith("steps[", StringComparison.Ordinal))
        {
            return TryParseStepsOnlyPath(message, out path);
        }

        return false;
    }

    private static bool TryParsePath(ReadOnlySpan<char> path, out DiagnosticStructurePath result)
    {
        result = default;
        if (path.IsEmpty)
        {
            return false;
        }

        if (path.StartsWith("jobs.", StringComparison.Ordinal))
        {
            return TryParseJobsPath(path, out result);
        }

        if (path.StartsWith("steps[", StringComparison.Ordinal))
        {
            return TryParseStepsOnlyPath(path, out result);
        }

        return false;
    }

    private static bool TryParseJobsPath(ReadOnlySpan<char> message, out DiagnosticStructurePath path)
    {
        path = default;
        var cursor = 5; // after "jobs."

        string jobId;
        if (cursor < message.Length && message[cursor] == '\'')
        {
            var close = message[(cursor + 1)..].IndexOf('\'');
            if (close < 0)
            {
                return false;
            }

            jobId = message.Slice(cursor + 1, close).ToString();
            cursor += close + 2;
            if (cursor >= message.Length || message[cursor] != '.')
            {
                return false;
            }

            cursor++;
        }
        else
        {
            var dot = message[cursor..].IndexOf('.');
            if (dot <= 0)
            {
                return false;
            }

            jobId = message.Slice(cursor, dot).ToString();
            cursor += dot + 1;
        }

        var hasSteps = false;
        var stepIndex = 0;
        ReadOnlyMemory<char> remaining = default;

        if (message[cursor..].StartsWith("steps[", StringComparison.Ordinal))
        {
            if (!TryParseStepIndex(message[cursor..], out var parsedIndex, out var consumed))
            {
                return false;
            }

            hasSteps = true;
            stepIndex = parsedIndex;
            cursor += consumed;
            remaining = TrimPathSuffix(message[cursor..]).ToString().AsMemory();
        }
        else
        {
            var tail = message[cursor..];
            var keyEnd = tail.IndexOfAny('.', ' ');
            if (keyEnd > 0)
            {
                remaining = tail[keyEnd..].ToString().AsMemory();
            }
            else if (keyEnd == 0)
            {
                remaining = tail.ToString().AsMemory();
            }
            else
            {
                remaining = tail.ToString().AsMemory();
            }
        }

        path = new DiagnosticStructurePath(true, jobId, hasSteps, stepIndex, remaining);
        return true;
    }

    private static bool TryParseStepsOnlyPath(ReadOnlySpan<char> message, out DiagnosticStructurePath path)
    {
        path = default;
        if (!TryParseStepIndex(message, out var stepIndex, out var consumed))
        {
            return false;
        }

        path = new DiagnosticStructurePath(false, string.Empty, true, stepIndex, TrimPathSuffix(message[consumed..]).ToString().AsMemory());
        return true;
    }

    private static ReadOnlySpan<char> TrimPathSuffix(ReadOnlySpan<char> suffix)
    {
        var space = suffix.IndexOf(' ');
        return space > 0 ? suffix[..space] : suffix;
    }

    private static bool TryParseStepIndex(ReadOnlySpan<char> message, out int stepIndex1Based, out int consumed)
    {
        stepIndex1Based = 0;
        consumed = 0;
        if (!message.StartsWith("steps[", StringComparison.Ordinal))
        {
            return false;
        }

        var close = message[6..].IndexOf(']');
        if (close <= 0)
        {
            return false;
        }

        var indexText = message.Slice(6, close);
        if (!int.TryParse(indexText, out stepIndex1Based) || stepIndex1Based <= 0)
        {
            return false;
        }

        consumed = 6 + close + 1;
        return true;
    }
}

internal static class DiagnosticStructurePathResolver
{
    public static bool TryResolveTargetLine(YamlLineIndex lineIndex, in DiagnosticStructurePath path, out int targetLine0)
    {
        targetLine0 = -1;
        if (path.IsEmpty)
        {
            return false;
        }

        var current = -1;
        if (path.HasJobs)
        {
            if (!lineIndex.TryFindJobsLine(out var jobsLine))
            {
                return false;
            }

            if (!lineIndex.TryFindChildMappingKey(jobsLine, path.JobId, out current))
            {
                return false;
            }
        }

        if (path.HasSteps)
        {
            if (current < 0)
            {
                if (!lineIndex.TryFindRunsLine(out current))
                {
                    return false;
                }
            }

            if (!lineIndex.TryFindChildScalarKey(current, "steps", out var stepsLine))
            {
                return false;
            }

            if (!lineIndex.TryFindSequenceItemLine(stepsLine, path.StepIndex1Based, out current))
            {
                return false;
            }
        }

        if (!path.Remaining.IsEmpty)
        {
            if (current < 0)
            {
                return false;
            }

            if (!TryResolveRemainingKeys(lineIndex, current, path.Remaining.Span, out current))
            {
                return false;
            }
        }

        if (current < 0)
        {
            return false;
        }

        targetLine0 = current;
        return true;
    }

    private static bool TryResolveRemainingKeys(YamlLineIndex lineIndex, int parentLine, ReadOnlySpan<char> remaining, out int line0)
    {
        line0 = parentLine;
        if (remaining.IsEmpty)
        {
            return true;
        }

        if (remaining[0] == '.')
        {
            remaining = remaining[1..];
        }

        while (!remaining.IsEmpty)
        {
            var dot = remaining.IndexOf('.');
            var segment = dot < 0 ? remaining : remaining[..dot];
            if (segment.IsEmpty)
            {
                return false;
            }

            if (lineIndex.IsSequenceItemWithInlineKey(line0, segment))
            {
                if (dot < 0)
                {
                    break;
                }

                remaining = remaining[(dot + 1)..];
                continue;
            }

            if (!lineIndex.TryFindChildScalarKey(line0, segment, out line0))
            {
                return false;
            }

            if (dot < 0)
            {
                break;
            }

            remaining = remaining[(dot + 1)..];
        }

        return true;
    }
}
