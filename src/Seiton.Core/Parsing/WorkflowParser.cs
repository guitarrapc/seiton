using VYaml.Parser;

namespace Seiton.Core.Parsing;

public static class WorkflowParser
{
    public static ParseResult Parse(byte[] utf8Yaml, string filePath)
    {
        var diagnostics = new List<Diagnostic>(16);
        var reader = new VYamlStreamReader(utf8Yaml.AsMemory());

        reader.SkipHeader();

        if (reader.CurrentEventType != ParseEventType.MappingStart)
        {
            AddError(diagnostics, "workflow root must be mapping", reader.CurrentMark);
            return new ParseResult(default, diagnostics.ToArray(), HasFatalError: true);
        }

        reader.Read(); // skip MappingStart

        var hasName = false;
        var hasRunName = false;
        var hasOn = false;
        var hasJobs = false;
        Utf8Slice name = default;
        Utf8Slice runName = default;

        while (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, "workflow key must be scalar", reader.CurrentMark);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentMark;
            var keyText = reader.GetScalarString() ?? string.Empty;
            reader.Read(); // consume key

            if (string.Equals(keyText, "name", StringComparison.Ordinal))
            {
                hasName = true;
                name = ReadScalarOrSkip(ref reader, diagnostics, "name must be scalar");
                continue;
            }

            if (string.Equals(keyText, "run-name", StringComparison.Ordinal))
            {
                hasRunName = true;
                runName = ReadScalarOrSkip(ref reader, diagnostics, "run-name must be scalar");
                continue;
            }

            if (string.Equals(keyText, "on", StringComparison.Ordinal))
            {
                hasOn = true;
                if (!reader.End)
                {
                    if (reader.CurrentEventType is not ParseEventType.Scalar and not ParseEventType.MappingStart and not ParseEventType.SequenceStart)
                    {
                        AddError(diagnostics, "on must be scalar, mapping, or sequence", reader.CurrentMark);
                        reader.SkipCurrentNode();
                    }
                    else
                    {
                        ParseOn(ref reader, diagnostics);
                    }
                }
                continue;
            }

            if (string.Equals(keyText, "jobs", StringComparison.Ordinal))
            {
                hasJobs = true;
                if (!reader.End)
                {
                    if (reader.CurrentEventType != ParseEventType.MappingStart)
                    {
                        AddError(diagnostics, "jobs must be mapping", reader.CurrentMark);
                        reader.SkipCurrentNode();
                    }
                    else
                    {
                        ParseJobsMapping(ref reader, diagnostics);
                    }
                }
                continue;
            }

            if (string.Equals(keyText, "permissions", StringComparison.Ordinal) ||
                string.Equals(keyText, "env", StringComparison.Ordinal) ||
                string.Equals(keyText, "defaults", StringComparison.Ordinal) ||
                string.Equals(keyText, "concurrency", StringComparison.Ordinal))
            {
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            AddError(diagnostics, $"unexpected workflow key: {keyText}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentEventType == ParseEventType.MappingEnd)
        {
            reader.Read();
        }

        if (!hasOn)
        {
            AddError(diagnostics, "required key 'on' is missing", new Marker(0, 1, 1));
        }

        if (!hasJobs)
        {
            AddError(diagnostics, "required key 'jobs' is missing", new Marker(0, 1, 1));
        }

        var document = new WorkflowDocument(
            HasName: hasName,
            Name: name,
            HasRunName: hasRunName,
            RunName: runName,
            HasOn: hasOn,
            HasJobs: hasJobs);

        return new ParseResult(document, diagnostics.ToArray(), HasFatalError: false);
    }

    private static void ParseJobsMapping(ref VYamlStreamReader reader, List<Diagnostic> diagnostics)
    {
        // current is MappingStart
        reader.Read();

        while (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, "job id must be scalar", reader.CurrentMark);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var jobIdMark = reader.CurrentMark;
            var jobId = reader.GetScalarString() ?? string.Empty;
            reader.Read(); // consume job id

            if (reader.End)
            {
                break;
            }

            ParseJobNode(ref reader, diagnostics, jobId, jobIdMark);
        }

        if (reader.CurrentEventType == ParseEventType.MappingEnd)
        {
            reader.Read();
        }
    }

    private static void ParseJobNode(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string jobId, Marker jobIdMark)
    {
        if (reader.CurrentEventType != ParseEventType.MappingStart)
        {
            AddError(diagnostics, $"job '{jobId}' must be mapping", reader.CurrentMark);
            reader.SkipCurrentNode();
            return;
        }

        var hasRunsOn = false;
        var hasSteps = false;
        var hasUses = false;

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, $"job '{jobId}' key must be scalar", reader.CurrentMark);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentMark;
            var key = reader.GetScalarString() ?? string.Empty;
            reader.Read(); // consume key

            if (string.Equals(key, "runs-on", StringComparison.Ordinal))
            {
                hasRunsOn = true;
                if (!reader.End)
                {
                    if (reader.CurrentEventType is not ParseEventType.Scalar and not ParseEventType.SequenceStart and not ParseEventType.MappingStart)
                    {
                        AddError(diagnostics, $"job '{jobId}' runs-on has invalid type", reader.CurrentMark);
                    }
                    reader.SkipCurrentNode();
                }
                continue;
            }

            if (string.Equals(key, "steps", StringComparison.Ordinal))
            {
                hasSteps = true;
                if (!reader.End)
                {
                    if (reader.CurrentEventType != ParseEventType.SequenceStart)
                    {
                        AddError(diagnostics, $"job '{jobId}' steps must be sequence", reader.CurrentMark);
                        reader.SkipCurrentNode();
                    }
                    else
                    {
                        ParseSteps(ref reader, diagnostics, jobId);
                    }
                }
                continue;
            }

            if (string.Equals(key, "uses", StringComparison.Ordinal))
            {
                hasUses = true;
                if (!reader.End)
                {
                    if (reader.CurrentEventType != ParseEventType.Scalar)
                    {
                        AddError(diagnostics, $"job '{jobId}' uses must be scalar", reader.CurrentMark);
                    }
                    reader.SkipCurrentNode();
                }
                continue;
            }

            if (IsKnownJobKey(key))
            {
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            AddError(diagnostics, $"unexpected job key '{key}' in job '{jobId}'", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentEventType == ParseEventType.MappingEnd)
        {
            reader.Read();
        }

        if (hasUses && hasSteps)
        {
            AddError(diagnostics, $"job '{jobId}' cannot have both uses and steps", jobIdMark);
        }

        if (hasUses && hasRunsOn)
        {
            AddError(diagnostics, $"job '{jobId}' cannot have both uses and runs-on", jobIdMark);
        }

        if (!hasUses && !hasRunsOn)
        {
            AddError(diagnostics, $"job '{jobId}' requires runs-on (or uses)", jobIdMark);
        }

        if (!hasUses && !hasSteps)
        {
            AddError(diagnostics, $"job '{jobId}' requires steps (or uses)", jobIdMark);
        }
    }

    private static void ParseSteps(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string jobId)
    {
        // current is SequenceStart
        reader.Read();

        var stepIndex = 0;
        while (!reader.End && reader.CurrentEventType != ParseEventType.SequenceEnd)
        {
            stepIndex++;
            ParseStep(ref reader, diagnostics, jobId, stepIndex);
        }

        if (reader.CurrentEventType == ParseEventType.SequenceEnd)
        {
            reader.Read();
        }
    }

    private static void ParseStep(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string jobId, int stepIndex)
    {
        if (reader.CurrentEventType != ParseEventType.MappingStart)
        {
            AddError(diagnostics, $"job '{jobId}' step[{stepIndex}] must be mapping", reader.CurrentMark);
            reader.SkipCurrentNode();
            return;
        }

        var hasRun = false;
        var hasUses = false;

        reader.Read();
        while (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, $"job '{jobId}' step[{stepIndex}] key must be scalar", reader.CurrentMark);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var key = reader.GetScalarString() ?? string.Empty;
            var keyMark = reader.CurrentMark;
            reader.Read();

            if (string.Equals(key, "run", StringComparison.Ordinal))
            {
                hasRun = true;
                if (!reader.End)
                {
                    if (reader.CurrentEventType != ParseEventType.Scalar)
                    {
                        AddError(diagnostics, $"job '{jobId}' step[{stepIndex}] run must be scalar", reader.CurrentMark);
                    }
                    reader.SkipCurrentNode();
                }
                continue;
            }

            if (string.Equals(key, "uses", StringComparison.Ordinal))
            {
                hasUses = true;
                if (!reader.End)
                {
                    if (reader.CurrentEventType != ParseEventType.Scalar)
                    {
                        AddError(diagnostics, $"job '{jobId}' step[{stepIndex}] uses must be scalar", reader.CurrentMark);
                    }
                    reader.SkipCurrentNode();
                }
                continue;
            }

            if (IsKnownStepKey(key))
            {
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            AddError(diagnostics, $"unexpected step key '{key}' in job '{jobId}' step[{stepIndex}]", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentEventType == ParseEventType.MappingEnd)
        {
            reader.Read();
        }

        if (hasRun && hasUses)
        {
            AddError(diagnostics, $"job '{jobId}' step[{stepIndex}] cannot have both run and uses", reader.CurrentMark);
        }

        if (!hasRun && !hasUses)
        {
            AddError(diagnostics, $"job '{jobId}' step[{stepIndex}] requires run or uses", reader.CurrentMark);
        }
    }

    private static bool IsKnownJobKey(string key)
    {
        return key is "name" or "needs" or "if" or "permissions" or "env" or "defaults" or
            "timeout-minutes" or "strategy" or "concurrency" or "container" or "services" or
            "outputs" or "secrets" or "with";
    }

    private static bool IsKnownStepKey(string key)
    {
        return key is "name" or "id" or "if" or "with" or "env" or "shell" or "working-directory" or
            "timeout-minutes" or "continue-on-error";
    }

    private static Utf8Slice ReadScalarOrSkip(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string errorMessage)
    {
        if (reader.End)
        {
            return default;
        }

        if (reader.CurrentEventType != ParseEventType.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentMark);
            reader.SkipCurrentNode();
            return default;
        }

        var slice = reader.GetScalarSlice();
        reader.Read();
        return slice;
    }

    private static void ParseOn(ref VYamlStreamReader reader, List<Diagnostic> diagnostics)
    {
        if (reader.CurrentEventType == ParseEventType.Scalar)
        {
            reader.Read();
            return;
        }

        if (reader.CurrentEventType == ParseEventType.SequenceStart)
        {
            ParseOnSequence(ref reader, diagnostics);
            return;
        }

        if (reader.CurrentEventType == ParseEventType.MappingStart)
        {
            ParseOnMapping(ref reader, diagnostics);
            return;
        }

        AddError(diagnostics, "on must be scalar, sequence, or mapping", reader.CurrentMark);
        reader.SkipCurrentNode();
    }

    private static void ParseOnSequence(ref VYamlStreamReader reader, List<Diagnostic> diagnostics)
    {
        reader.Read(); // consume SequenceStart
        while (!reader.End && reader.CurrentEventType != ParseEventType.SequenceEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, "on sequence item must be scalar event name", reader.CurrentMark);
                reader.SkipCurrentNode();
                continue;
            }

            reader.Read();
        }

        if (reader.CurrentEventType == ParseEventType.SequenceEnd)
        {
            reader.Read();
        }
    }

    private static void ParseOnMapping(ref VYamlStreamReader reader, List<Diagnostic> diagnostics)
    {
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, "on mapping key must be scalar event name", reader.CurrentMark);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var eventName = reader.GetScalarString() ?? string.Empty;
            var eventMark = reader.CurrentMark;
            reader.Read(); // consume event key

            if (reader.End)
            {
                break;
            }

            if (reader.CurrentEventType == ParseEventType.MappingStart)
            {
                ParseOnEventOptions(ref reader, diagnostics, eventName, eventMark);
                continue;
            }

            if (reader.CurrentEventType is ParseEventType.Scalar or ParseEventType.SequenceStart)
            {
                // Some events can be represented as scalar/sequence/null-like value; accept shape and skip.
                reader.SkipCurrentNode();
                continue;
            }

            AddError(diagnostics, $"on.{eventName} must be scalar, sequence, or mapping", reader.CurrentMark);
            reader.SkipCurrentNode();
        }

        if (reader.CurrentEventType == ParseEventType.MappingEnd)
        {
            reader.Read();
        }
    }

    private static void ParseOnEventOptions(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string eventName, Marker eventMark)
    {
        var hasBranches = false;
        var hasBranchesIgnore = false;
        var hasTags = false;
        var hasTagsIgnore = false;
        var hasPaths = false;
        var hasPathsIgnore = false;

        reader.Read(); // consume MappingStart

        while (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, $"on.{eventName} option key must be scalar", reader.CurrentMark);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var key = reader.GetScalarString() ?? string.Empty;
            var keyMark = reader.CurrentMark;
            reader.Read();

            if (reader.End)
            {
                break;
            }

            switch (key)
            {
                case "types":
                    ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.types must be scalar or sequence of scalar");
                    break;
                case "branches":
                    hasBranches = true;
                    ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.branches must be scalar or sequence of scalar");
                    break;
                case "branches-ignore":
                    hasBranchesIgnore = true;
                    ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.branches-ignore must be scalar or sequence of scalar");
                    break;
                case "tags":
                    hasTags = true;
                    ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.tags must be scalar or sequence of scalar");
                    break;
                case "tags-ignore":
                    hasTagsIgnore = true;
                    ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.tags-ignore must be scalar or sequence of scalar");
                    break;
                case "paths":
                    hasPaths = true;
                    ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.paths must be scalar or sequence of scalar");
                    break;
                case "paths-ignore":
                    hasPathsIgnore = true;
                    ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.paths-ignore must be scalar or sequence of scalar");
                    break;
                case "workflows":
                    ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.workflows must be scalar or sequence of scalar");
                    break;
                case "inputs":
                case "secrets":
                case "outputs":
                    if (reader.CurrentEventType != ParseEventType.MappingStart)
                    {
                        AddError(diagnostics, $"on.{eventName}.{key} must be mapping", reader.CurrentMark);
                    }
                    reader.SkipCurrentNode();
                    break;
                default:
                    AddError(diagnostics, $"unexpected on.{eventName} option: {key}", keyMark);
                    reader.SkipCurrentNode();
                    break;
            }
        }

        if (reader.CurrentEventType == ParseEventType.MappingEnd)
        {
            reader.Read();
        }

        if (hasBranches && hasBranchesIgnore)
        {
            AddError(diagnostics, $"on.{eventName} cannot use both branches and branches-ignore", eventMark);
        }

        if (hasTags && hasTagsIgnore)
        {
            AddError(diagnostics, $"on.{eventName} cannot use both tags and tags-ignore", eventMark);
        }

        if (hasPaths && hasPathsIgnore)
        {
            AddError(diagnostics, $"on.{eventName} cannot use both paths and paths-ignore", eventMark);
        }
    }

    private static void ParseScalarOrScalarSequence(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string error)
    {
        if (reader.CurrentEventType == ParseEventType.Scalar)
        {
            reader.Read();
            return;
        }

        if (reader.CurrentEventType != ParseEventType.SequenceStart)
        {
            AddError(diagnostics, error, reader.CurrentMark);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read();
        while (!reader.End && reader.CurrentEventType != ParseEventType.SequenceEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentMark);
                reader.SkipCurrentNode();
                continue;
            }

            reader.Read();
        }

        if (reader.CurrentEventType == ParseEventType.SequenceEnd)
        {
            reader.Read();
        }
    }

    private static void AddError(List<Diagnostic> diagnostics, string message, Marker mark)
    {
        var location = new TextRange(
            Start: mark.Position,
            Length: 0,
            StartLine: mark.Line,
            StartColumn: mark.Col,
            EndLine: mark.Line,
            EndColumn: mark.Col);

        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, location));
    }
}
