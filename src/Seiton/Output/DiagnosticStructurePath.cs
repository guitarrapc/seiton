using System.Runtime.CompilerServices;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Output;

internal readonly struct DiagnosticStructurePath
{
    public DiagnosticStructurePath(
        string source,
        bool hasJobs,
        int jobIdStart,
        int jobIdLength,
        bool hasSteps,
        int stepIndex1Based,
        int remainingStart,
        int remainingLength)
    {
        Source = source;
        HasJobs = hasJobs;
        JobIdStart = jobIdStart;
        JobIdLength = jobIdLength;
        HasSteps = hasSteps;
        StepIndex1Based = stepIndex1Based;
        RemainingStart = remainingStart;
        RemainingLength = remainingLength;
    }

    public string Source { get; }
    public bool HasJobs { get; }
    public int JobIdStart { get; }
    public int JobIdLength { get; }
    public bool HasSteps { get; }
    public int StepIndex1Based { get; }
    public int RemainingStart { get; }
    public int RemainingLength { get; }

    public ReadOnlySpan<char> JobId => JobIdLength <= 0 ? [] : Source.AsSpan(JobIdStart, JobIdLength);
    public ReadOnlySpan<char> Remaining => RemainingLength <= 0 ? [] : Source.AsSpan(RemainingStart, RemainingLength);

    public bool IsEmpty => !HasJobs && !HasSteps && RemainingLength == 0;

    public bool IsWorkflowScoped => HasJobs || HasSteps || RemainingLength > 0;
}

internal static class DiagnosticStructurePathParser
{
    public static bool TryParse(Diagnostic diagnostic, out DiagnosticStructurePath path)
    {
        if (diagnostic.Metadata is not null
            && diagnostic.Metadata.TryGetValue(DiagnosticStructurePathMetadata.Key, out var metadataPath)
            && TryParsePath(metadataPath, out path))
        {
            return true;
        }

        return TryParseMessage(diagnostic.Message, out path);
    }

    public static bool TryParseMessage(string message, out DiagnosticStructurePath path)
    {
        path = default;
        if (message.Length == 0)
        {
            return false;
        }

        if (message.AsSpan().StartsWith("jobs.", StringComparison.Ordinal))
        {
            return TryParseJobsPath(message, out path);
        }

        if (message.AsSpan().StartsWith("steps[", StringComparison.Ordinal))
        {
            return TryParseStepsOnlyPath(message, out path);
        }

        return false;
    }

    private static bool TryParsePath(string path, out DiagnosticStructurePath result)
    {
        result = default;
        if (path.Length == 0)
        {
            return false;
        }

        if (path.AsSpan().StartsWith("jobs.", StringComparison.Ordinal))
        {
            return TryParseJobsPath(path, out result);
        }

        if (path.AsSpan().StartsWith("steps[", StringComparison.Ordinal))
        {
            return TryParseStepsOnlyPath(path, out result);
        }

        return false;
    }

    private static bool TryParseJobsPath(string message, out DiagnosticStructurePath path)
    {
        path = default;
        var messageSpan = message.AsSpan();
        var cursor = 5; // after "jobs."

        var jobIdStart = 0;
        var jobIdLength = 0;
        if (cursor < message.Length && message[cursor] == '\'')
        {
            var close = messageSpan[(cursor + 1)..].IndexOf('\'');
            if (close < 0)
            {
                return false;
            }

            jobIdStart = cursor + 1;
            jobIdLength = close;
            cursor += close + 2;
            if (cursor >= message.Length || message[cursor] != '.')
            {
                return false;
            }

            cursor++;
        }
        else
        {
            var dot = messageSpan[cursor..].IndexOf('.');
            if (dot <= 0)
            {
                return false;
            }

            jobIdStart = cursor;
            jobIdLength = dot;
            cursor += dot + 1;
        }

        var hasSteps = false;
        var stepIndex = 0;
        var remainingStart = 0;
        var remainingLength = 0;

        if (messageSpan[cursor..].StartsWith("steps[", StringComparison.Ordinal))
        {
            if (!TryParseStepIndex(messageSpan[cursor..], out var parsedIndex, out var consumed))
            {
                return false;
            }

            hasSteps = true;
            stepIndex = parsedIndex;
            cursor += consumed;
            var suffix = TrimPathSuffix(messageSpan[cursor..]);
            remainingStart = cursor;
            remainingLength = suffix.Length;
        }
        else
        {
            var tailPath = TrimPathSuffix(messageSpan[cursor..]);
            remainingStart = cursor;
            remainingLength = tailPath.Length;
        }

        path = new DiagnosticStructurePath(
            source: message,
            hasJobs: true,
            jobIdStart: jobIdStart,
            jobIdLength: jobIdLength,
            hasSteps: hasSteps,
            stepIndex1Based: stepIndex,
            remainingStart: remainingStart,
            remainingLength: remainingLength);
        return true;
    }

    private static bool TryParseStepsOnlyPath(string message, out DiagnosticStructurePath path)
    {
        path = default;
        var messageSpan = message.AsSpan();
        if (!TryParseStepIndex(messageSpan, out var stepIndex, out var consumed))
        {
            return false;
        }

        var remaining = TrimPathSuffix(messageSpan[consumed..]);
        path = new DiagnosticStructurePath(
            source: message,
            hasJobs: false,
            jobIdStart: 0,
            jobIdLength: 0,
            hasSteps: true,
            stepIndex1Based: stepIndex,
            remainingStart: consumed,
            remainingLength: remaining.Length);
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

            if (!lineIndex.TryFindChildScalarKey(jobsLine, path.JobId, out current))
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

            if (!TryResolveRemainingKeys(lineIndex, current, path.Remaining, out current))
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
