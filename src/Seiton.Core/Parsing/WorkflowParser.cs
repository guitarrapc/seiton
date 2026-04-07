using System.Text;
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
            var keyUtf8 = reader.GetScalarUtf8();

            if (keyUtf8.SequenceEqual("name"u8))
            {
                reader.Read(); // consume key
                hasName = true;
                name = ReadScalarOrSkip(ref reader, diagnostics, "name must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("run-name"u8))
            {
                reader.Read(); // consume key
                hasRunName = true;
                runName = ReadScalarOrSkip(ref reader, diagnostics, "run-name must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("on"u8))
            {
                reader.Read(); // consume key
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

            if (keyUtf8.SequenceEqual("jobs"u8))
            {
                reader.Read(); // consume key
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

            if (keyUtf8.SequenceEqual("permissions"u8) ||
                keyUtf8.SequenceEqual("env"u8) ||
                keyUtf8.SequenceEqual("defaults"u8) ||
                keyUtf8.SequenceEqual("concurrency"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyText = reader.GetScalarString() ?? string.Empty;
            reader.Read(); // consume key

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
        var hasWith = false;
        var hasSecrets = false;
        string? stepsOnlyKeyInReusable = null;
        Marker stepsOnlyKeyInReusableMark = default;

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
            var keyUtf8 = reader.GetScalarUtf8();

            if (keyUtf8.SequenceEqual("runs-on"u8))
            {
                reader.Read(); // consume key
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

            if (keyUtf8.SequenceEqual("steps"u8))
            {
                reader.Read(); // consume key
                hasSteps = true;
                if (stepsOnlyKeyInReusable is null)
                {
                    stepsOnlyKeyInReusable = "steps";
                    stepsOnlyKeyInReusableMark = keyMark;
                }
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

            if (keyUtf8.SequenceEqual("uses"u8))
            {
                reader.Read(); // consume key
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

            if (keyUtf8.SequenceEqual("strategy"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    if (reader.CurrentEventType != ParseEventType.MappingStart)
                    {
                        AddError(diagnostics, $"job '{jobId}' strategy must be mapping", reader.CurrentMark);
                        reader.SkipCurrentNode();
                    }
                    else
                    {
                        ParseStrategy(ref reader, diagnostics, jobId);
                    }
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("container"u8))
            {
                reader.Read(); // consume key
                if (stepsOnlyKeyInReusable is null)
                {
                    stepsOnlyKeyInReusable = "container";
                    stepsOnlyKeyInReusableMark = keyMark;
                }

                if (!reader.End)
                {
                    ParseContainerLike(ref reader, diagnostics, $"job '{jobId}' container", requireImage: true);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("services"u8))
            {
                reader.Read(); // consume key
                if (stepsOnlyKeyInReusable is null)
                {
                    stepsOnlyKeyInReusable = "services";
                    stepsOnlyKeyInReusableMark = keyMark;
                }

                if (!reader.End)
                {
                    ParseServices(ref reader, diagnostics, jobId);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("with"u8))
            {
                reader.Read(); // consume key
                hasWith = true;
                if (!reader.End)
                {
                    ParseStringMapping(ref reader, diagnostics, $"job '{jobId}' with must be mapping");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("secrets"u8))
            {
                reader.Read(); // consume key
                hasSecrets = true;
                if (!reader.End)
                {
                    ParseJobSecrets(ref reader, diagnostics, jobId);
                }
                continue;
            }

            var key = reader.GetScalarString() ?? string.Empty;
            reader.Read(); // consume key

            if (IsStepsOnlyJobKey(key) && stepsOnlyKeyInReusable is null)
            {
                stepsOnlyKeyInReusable = key;
                stepsOnlyKeyInReusableMark = keyMark;
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

        if (hasUses && stepsOnlyKeyInReusable is not null)
        {
            AddError(
                diagnostics,
                $"when job '{jobId}' calls reusable workflow with uses, key '{stepsOnlyKeyInReusable}' is not allowed",
                stepsOnlyKeyInReusableMark);
        }

        if (!hasUses && !hasRunsOn)
        {
            AddError(diagnostics, $"job '{jobId}' requires runs-on (or uses)", jobIdMark);
        }

        if (!hasUses && !hasSteps)
        {
            AddError(diagnostics, $"job '{jobId}' requires steps (or uses)", jobIdMark);
        }

        if (!hasUses && hasWith)
        {
            AddError(diagnostics, $"job '{jobId}' key 'with' requires uses", jobIdMark);
        }

        if (!hasUses && hasSecrets)
        {
            AddError(diagnostics, $"job '{jobId}' key 'secrets' requires uses", jobIdMark);
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

            var keyMark = reader.CurrentMark;
            var keyUtf8 = reader.GetScalarUtf8();

            if (keyUtf8.SequenceEqual("run"u8))
            {
                reader.Read();
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

            if (keyUtf8.SequenceEqual("uses"u8))
            {
                reader.Read();
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

            var key = reader.GetScalarString() ?? string.Empty;
            reader.Read();

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

    private static bool IsStepsOnlyJobKey(string key)
    {
        return key is "runs-on" or "environment" or "outputs" or "env" or "defaults" or
            "steps" or "timeout-minutes" or "continue-on-error" or "container";
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
            var eventMark = reader.CurrentMark;
            var eventName = ReadOnEventName(ref reader);
            ValidateKnownOnEvent(eventName, eventMark, diagnostics);
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

            var eventMark = reader.CurrentMark;
            var eventName = ReadOnEventName(ref reader);
            ValidateKnownOnEvent(eventName, eventMark, diagnostics);
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

            var eventName = ReadOnEventName(ref reader);
            var eventMark = reader.CurrentMark;
            ValidateKnownOnEvent(eventName, eventMark, diagnostics);
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
        _ = OnEventSpecs.TryGet(eventName, out var spec);

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

            var keyMark = reader.CurrentMark;
            var keyUtf8 = reader.GetScalarUtf8();

            if (keyUtf8.SequenceEqual("types"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                if (OnEventSpecs.TryGet(eventName, out var typeSpec) && !typeSpec.IsTypeOptionSupported())
                {
                    AddError(diagnostics, $"on.{eventName}.types is not supported", keyMark);
                    reader.SkipCurrentNode();
                    continue;
                }

                ParseOnTypes(ref reader, diagnostics, eventName);
                continue;
            }

            if (OnEventSpecs.TryGet(eventName, out var knownSpec)
                && !knownSpec.IsOptionAllowed(keyUtf8))
            {
                var key = reader.GetScalarString() ?? string.Empty;
                reader.Read();
                AddError(diagnostics, $"on.{eventName} does not support option: {key}", keyMark);
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("branches"u8))
            {
                reader.Read();
                hasBranches = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.branches must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("branches-ignore"u8))
            {
                reader.Read();
                hasBranchesIgnore = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.branches-ignore must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("tags"u8))
            {
                reader.Read();
                hasTags = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.tags must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("tags-ignore"u8))
            {
                reader.Read();
                hasTagsIgnore = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.tags-ignore must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("paths"u8))
            {
                reader.Read();
                hasPaths = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.paths must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("paths-ignore"u8))
            {
                reader.Read();
                hasPathsIgnore = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.paths-ignore must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("workflows"u8))
            {
                reader.Read();
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventName}.workflows must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("inputs"u8) || keyUtf8.SequenceEqual("secrets"u8) || keyUtf8.SequenceEqual("outputs"u8))
            {
                var key = reader.GetScalarString() ?? string.Empty;
                reader.Read();
                if (reader.CurrentEventType != ParseEventType.MappingStart)
                {
                    AddError(diagnostics, $"on.{eventName}.{key} must be mapping", reader.CurrentMark);
                }
                reader.SkipCurrentNode();
                continue;
            }

            if (reader.End)
            {
                break;
            }

            var unknownKey = reader.GetScalarString() ?? string.Empty;
            reader.Read();
            AddError(diagnostics, $"unexpected on.{eventName} option: {unknownKey}", keyMark);
            reader.SkipCurrentNode();
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

    private static void ParseScalarOrScalarSequence(
        ref VYamlStreamReader reader,
        List<Diagnostic> diagnostics,
        string error,
        Func<string, string?>? scalarValidator = null)
    {
        if (reader.CurrentEventType == ParseEventType.Scalar)
        {
            if (scalarValidator is not null)
            {
                var value = reader.GetScalarString() ?? string.Empty;
                var validationError = scalarValidator(value);
                if (validationError is not null)
                {
                    AddError(diagnostics, validationError, reader.CurrentMark);
                }
            }
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

            if (scalarValidator is not null)
            {
                var value = reader.GetScalarString() ?? string.Empty;
                var validationError = scalarValidator(value);
                if (validationError is not null)
                {
                    AddError(diagnostics, validationError, reader.CurrentMark);
                }
            }

            reader.Read();
        }

        if (reader.CurrentEventType == ParseEventType.SequenceEnd)
        {
            reader.Read();
        }
    }

    private static void ParseStrategy(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string jobId)
    {
        reader.Read(); // consume MappingStart

        while (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, $"job '{jobId}' strategy key must be scalar", reader.CurrentMark);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentMark;
            var keyUtf8 = reader.GetScalarUtf8();

            if (keyUtf8.SequenceEqual("matrix"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                ParseMatrix(ref reader, diagnostics, jobId);
                continue;
            }

            if (keyUtf8.SequenceEqual("fail-fast"u8) || keyUtf8.SequenceEqual("max-parallel"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var key = reader.GetScalarString() ?? string.Empty;
            reader.Read();
            AddError(diagnostics, $"unexpected strategy key '{key}' in job '{jobId}'", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentEventType == ParseEventType.MappingEnd)
        {
            reader.Read();
        }
    }

    private static void ParseMatrix(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string jobId)
    {
        if (reader.CurrentEventType == ParseEventType.Scalar)
        {
            reader.Read();
            return;
        }

        if (reader.CurrentEventType != ParseEventType.MappingStart)
        {
            AddError(diagnostics, $"job '{jobId}' strategy.matrix must be scalar or mapping", reader.CurrentMark);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read(); // consume matrix mapping
        while (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, $"job '{jobId}' strategy.matrix key must be scalar", reader.CurrentMark);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyUtf8 = reader.GetScalarUtf8();
            var isInclude = keyUtf8.SequenceEqual("include"u8);
            var isExclude = keyUtf8.SequenceEqual("exclude"u8);
            reader.Read();
            if (reader.End)
            {
                break;
            }

            if (isInclude || isExclude)
            {
                if (reader.CurrentEventType is not ParseEventType.SequenceStart and not ParseEventType.Scalar)
                {
                    var keyTextForDiagnostic = isInclude ? "include" : "exclude";
                    AddError(diagnostics, $"job '{jobId}' strategy.matrix.{keyTextForDiagnostic} must be sequence or scalar", reader.CurrentMark);
                }
                reader.SkipCurrentNode();
                continue;
            }

            if (reader.CurrentEventType is not ParseEventType.SequenceStart and not ParseEventType.Scalar)
            {
                var keyTextForDiagnostic = Encoding.UTF8.GetString(keyUtf8);
                AddError(diagnostics, $"job '{jobId}' strategy.matrix.{keyTextForDiagnostic} must be sequence or scalar", reader.CurrentMark);
            }
            reader.SkipCurrentNode();
        }

        if (reader.CurrentEventType == ParseEventType.MappingEnd)
        {
            reader.Read();
        }
    }

    private static void ParseServices(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string jobId)
    {
        if (reader.CurrentEventType != ParseEventType.MappingStart)
        {
            AddError(diagnostics, $"job '{jobId}' services must be mapping", reader.CurrentMark);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read(); // consume services mapping
        while (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, $"job '{jobId}' services key must be scalar", reader.CurrentMark);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var serviceName = reader.GetScalarString() ?? string.Empty;
            reader.Read();
            if (reader.End)
            {
                break;
            }

            ParseContainerLike(ref reader, diagnostics, $"job '{jobId}' service '{serviceName}'", requireImage: true);
        }

        if (reader.CurrentEventType == ParseEventType.MappingEnd)
        {
            reader.Read();
        }
    }

    private static void ParseContainerLike(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string sectionName, bool requireImage)
    {
        if (reader.CurrentEventType == ParseEventType.Scalar)
        {
            reader.Read();
            return;
        }

        if (reader.CurrentEventType != ParseEventType.MappingStart)
        {
            AddError(diagnostics, $"{sectionName} must be scalar or mapping", reader.CurrentMark);
            reader.SkipCurrentNode();
            return;
        }

        var hasImage = false;
        reader.Read(); // consume mapping

        while (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, $"{sectionName} key must be scalar", reader.CurrentMark);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentMark;
            var keyUtf8 = reader.GetScalarUtf8();

            if (keyUtf8.SequenceEqual("image"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                hasImage = true;
                if (reader.CurrentEventType != ParseEventType.Scalar)
                {
                    AddError(diagnostics, $"{sectionName}.image must be scalar", reader.CurrentMark);
                }
                reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("credentials"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                ParseCredentials(ref reader, diagnostics, sectionName);
                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                if (reader.CurrentEventType != ParseEventType.MappingStart)
                {
                    AddError(diagnostics, $"{sectionName}.env must be mapping", reader.CurrentMark);
                }
                reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("ports"u8) || keyUtf8.SequenceEqual("volumes"u8))
            {
                var optionKey = reader.GetScalarString() ?? string.Empty;
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                ParseScalarOrScalarSequence(ref reader, diagnostics, $"{sectionName}.{optionKey} must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("options"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                if (reader.CurrentEventType != ParseEventType.Scalar)
                {
                    AddError(diagnostics, $"{sectionName}.options must be scalar", reader.CurrentMark);
                }
                reader.SkipCurrentNode();
                continue;
            }

            var key = reader.GetScalarString() ?? string.Empty;
            reader.Read();
            if (reader.End)
            {
                break;
            }

            AddError(diagnostics, $"unexpected {sectionName} key: {key}", keyMark);
            reader.SkipCurrentNode();
        }

        if (reader.CurrentEventType == ParseEventType.MappingEnd)
        {
            reader.Read();
        }

        if (requireImage && !hasImage)
        {
            AddError(diagnostics, $"{sectionName}.image is required", new Marker(0, 1, 1));
        }
    }

    private static void ParseCredentials(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string sectionName)
    {
        if (reader.CurrentEventType != ParseEventType.MappingStart)
        {
            AddError(diagnostics, $"{sectionName}.credentials must be mapping", reader.CurrentMark);
            reader.SkipCurrentNode();
            return;
        }

        var hasUsername = false;
        var hasPassword = false;
        reader.Read();
        while (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, $"{sectionName}.credentials key must be scalar", reader.CurrentMark);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentMark;
            var keyUtf8 = reader.GetScalarUtf8();
            reader.Read();
            if (reader.End)
            {
                break;
            }

            if (keyUtf8.SequenceEqual("username"u8))
            {
                hasUsername = true;
            }
            else if (keyUtf8.SequenceEqual("password"u8))
            {
                hasPassword = true;
            }
            else
            {
                var unexpectedKey = reader.GetScalarString() ?? string.Empty;
                AddError(diagnostics, $"unexpected {sectionName}.credentials key: {unexpectedKey}", keyMark);
            }

            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                var fieldName = reader.GetScalarString() ?? string.Empty;
                AddError(diagnostics, $"{sectionName}.credentials.{fieldName} must be scalar", reader.CurrentMark);
            }
            reader.SkipCurrentNode();
        }

        if (reader.CurrentEventType == ParseEventType.MappingEnd)
        {
            reader.Read();
        }

        if (!hasUsername || !hasPassword)
        {
            AddError(diagnostics, $"{sectionName}.credentials requires both username and password", new Marker(0, 1, 1));
        }
    }

    private static void ParseStringMapping(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string error)
    {
        if (reader.CurrentEventType != ParseEventType.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentMark);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read();
        while (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentMark);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            reader.Read();
            if (reader.End)
            {
                break;
            }

            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentMark);
            }
            reader.SkipCurrentNode();
        }

        if (reader.CurrentEventType == ParseEventType.MappingEnd)
        {
            reader.Read();
        }
    }

    private static void ParseJobSecrets(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string jobId)
    {
        if (reader.CurrentEventType == ParseEventType.Scalar)
        {
            var valueUtf8 = reader.GetScalarUtf8();
            if (!valueUtf8.SequenceEqual("inherit"u8))
            {
                var value = reader.GetScalarString() ?? string.Empty;
                AddError(diagnostics, $"job '{jobId}' secrets scalar must be 'inherit'", reader.CurrentMark);
            }
            reader.Read();
            return;
        }

        ParseStringMapping(ref reader, diagnostics, $"job '{jobId}' secrets must be mapping or scalar 'inherit'");
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

    private static void ValidateKnownOnEvent(string eventName, Marker eventMark, List<Diagnostic> diagnostics)
    {
        if (!OnEventSpecs.TryGet(eventName, out _))
        {
            AddError(diagnostics, $"unknown event in on: {eventName}", eventMark);
        }
    }

    private static string ReadOnEventName(ref VYamlStreamReader reader)
    {
        try
        {
            var eventNameUtf8 = reader.GetScalarUtf8();
            if (OnEventSpecs.TryGet(eventNameUtf8, out var knownEventName, out _))
            {
                return knownEventName;
            }
        }
        catch (YamlParserException)
        {
            // Fall back to scalar string for odd scalar representations.
        }

        return reader.GetScalarString() ?? string.Empty;
    }

    private static void ParseOnTypes(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string eventName)
    {
        if (reader.CurrentEventType == ParseEventType.Scalar)
        {
            var valueUtf8 = reader.GetScalarUtf8();
            if (OnEventSpecs.TryGet(eventName, out var knownTypeSpec) && !knownTypeSpec.IsTypeAllowed(valueUtf8))
            {
                AddError(diagnostics, $"on.{eventName}.types contains unsupported activity type: {Encoding.UTF8.GetString(valueUtf8)}", reader.CurrentMark);
            }

            reader.Read();
            return;
        }

        if (reader.CurrentEventType != ParseEventType.SequenceStart)
        {
            AddError(diagnostics, $"on.{eventName}.types must be scalar or sequence of scalar", reader.CurrentMark);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read();
        while (!reader.End && reader.CurrentEventType != ParseEventType.SequenceEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, $"on.{eventName}.types must be scalar or sequence of scalar", reader.CurrentMark);
                reader.SkipCurrentNode();
                continue;
            }

            var valueUtf8 = reader.GetScalarUtf8();
            if (OnEventSpecs.TryGet(eventName, out var knownTypeSpec) && !knownTypeSpec.IsTypeAllowed(valueUtf8))
            {
                AddError(diagnostics, $"on.{eventName}.types contains unsupported activity type: {Encoding.UTF8.GetString(valueUtf8)}", reader.CurrentMark);
            }

            reader.Read();
        }

        if (reader.CurrentEventType == ParseEventType.SequenceEnd)
        {
            reader.Read();
        }
    }
}
