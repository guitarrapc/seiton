using System.Text;

namespace Seiton.Core.Parsing;

public static class WorkflowParser
{
    private delegate string? Utf8ScalarValidator(ReadOnlySpan<byte> valueUtf8);

    private readonly struct OnEventInfo
    {
        public OnEventInfo(string name, bool isKnown, OnEventSpecs.EventSpec spec)
        {
            Name = name;
            IsKnown = isKnown;
            Spec = spec;
        }

        public string Name { get; }

        public bool IsKnown { get; }

        public OnEventSpecs.EventSpec Spec { get; }
    }

    public static ParseResult Parse(byte[] utf8Yaml, string filePath)
    {
        var reader = new VYamlStreamAdapter(utf8Yaml.AsMemory());
        return Parse(ref reader, utf8Yaml, filePath);
    }

    internal static ParseResult Parse(ref VYamlStreamAdapter reader, ReadOnlySpan<byte> source, string filePath)
    {
        var diagnostics = new List<Diagnostic>(16);

        reader.SkipHeader();

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "workflow root must be mapping", reader.CurrentStart);
            return new ParseResult(default, diagnostics.ToArray(), HasFatalError: true);
        }

        reader.Read(); // skip MappingStart

        var hasName = false;
        var hasRunName = false;
        var hasOn = false;
        var hasJobs = false;
        Utf8Slice name = default;
        Utf8Slice runName = default;

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "workflow key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
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
                if (reader.End)
                {
                    runName = default;
                    continue;
                }

                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, "run-name must be scalar", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    runName = default;
                    continue;
                }

                var runNameMark = reader.CurrentStart;
                runName = reader.GetScalarSlice();
                var runNameUtf8 = reader.GetScalarUtf8();
                ValidateExpressionText(
                    runNameUtf8,
                    BuildScalarLocation(runNameMark, runNameUtf8.Length),
                    ExpressionValidationContext.Workflow,
                    diagnostics,
                    parseWholeValueIfNoEmbedded: false);
                reader.Read();
                continue;
            }

            if (keyUtf8.SequenceEqual("on"u8))
            {
                reader.Read(); // consume key
                hasOn = true;
                if (!reader.End)
                {
                    if (reader.CurrentKind is not YamlEventKind.Scalar and not YamlEventKind.MappingStart and not YamlEventKind.SequenceStart)
                    {
                        AddError(diagnostics, "on must be scalar, mapping, or sequence", reader.CurrentStart);
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
                    if (reader.CurrentKind != YamlEventKind.MappingStart)
                    {
                        AddError(diagnostics, "jobs must be mapping", reader.CurrentStart);
                        reader.SkipCurrentNode();
                    }
                    else
                    {
                        ParseJobsMapping(ref reader, diagnostics, source);
                    }
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    ParseStringMapping(
                        ref reader,
                        diagnostics,
                        "workflow env must be mapping",
                        ExpressionValidationContext.Workflow);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("permissions"u8) ||
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

            var keyText = Encoding.UTF8.GetString(keyUtf8);
            reader.Read(); // consume key

            AddError(diagnostics, $"unexpected workflow key: {keyText}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (!hasOn)
        {
            AddError(diagnostics, "required key 'on' is missing", new TextPosition(0, 1, 1));
        }

        if (!hasJobs)
        {
            AddError(diagnostics, "required key 'jobs' is missing", new TextPosition(0, 1, 1));
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

    private static void ParseJobsMapping(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
    {
        // current is MappingStart
        reader.Read();

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "job id must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var jobIdMark = reader.CurrentStart;
            var jobId = reader.GetScalarSlice();
            reader.Read(); // consume job id

            if (reader.End)
            {
                break;
            }

            ParseJobNode(ref reader, diagnostics, source, jobId, jobIdMark);
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }
    }

    private static void ParseJobNode(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, TextPosition jobIdMark)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        var hasRunsOn = false;
        var hasSteps = false;
        var hasUses = false;
        var hasWith = false;
        var hasSecrets = false;
        string? stepsOnlyKeyInReusable = null;
        TextPosition stepsOnlyKeyInReusableMark = default;

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();

            if (keyUtf8.SequenceEqual("runs-on"u8))
            {
                reader.Read(); // consume key
                hasRunsOn = true;
                if (!reader.End)
                {
                    if (reader.CurrentKind is not YamlEventKind.Scalar and not YamlEventKind.SequenceStart and not YamlEventKind.MappingStart)
                    {
                        AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' runs-on has invalid type", reader.CurrentStart);
                    }
                    reader.SkipCurrentNode();
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    ParseStringMapping(
                        ref reader,
                        diagnostics,
                        $"job '{DecodeUtf8(source, jobId)}' env must be mapping",
                        ExpressionValidationContext.Job);
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
                    if (reader.CurrentKind != YamlEventKind.SequenceStart)
                    {
                        AddError(diagnostics, $"job '{jobId}' steps must be sequence", reader.CurrentStart);
                        reader.SkipCurrentNode();
                    }
                    else
                    {
                        ParseSteps(ref reader, diagnostics, source, jobId);
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
                    if (reader.CurrentKind != YamlEventKind.Scalar)
                    {
                        AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' uses must be scalar", reader.CurrentStart);
                    }
                    reader.SkipCurrentNode();
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("if"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    ParseConditionalExpression(
                        ref reader,
                        diagnostics,
                        ExpressionValidationContext.Job,
                        $"job '{DecodeUtf8(source, jobId)}' if must be scalar");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("strategy"u8))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    if (reader.CurrentKind != YamlEventKind.MappingStart)
                    {
                        AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy must be mapping", reader.CurrentStart);
                        reader.SkipCurrentNode();
                    }
                    else
                    {
                        ParseStrategy(ref reader, diagnostics, source, jobId);
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
                    ParseContainerLike(ref reader, diagnostics, source, jobId, default, isService: false, requireImage: true);
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
                    ParseServices(ref reader, diagnostics, source, jobId);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("with"u8))
            {
                reader.Read(); // consume key
                hasWith = true;
                if (!reader.End)
                {
                    ParseStringMapping(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' with must be mapping");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("secrets"u8))
            {
                reader.Read(); // consume key
                hasSecrets = true;
                if (!reader.End)
                {
                    ParseJobSecrets(ref reader, diagnostics, source, jobId);
                }
                continue;
            }

            reader.Read(); // consume key

            if (stepsOnlyKeyInReusable is null && TryGetStepsOnlyReusableJobKeyName(keyUtf8, out var stepsOnlyKeyName))
            {
                stepsOnlyKeyInReusable = stepsOnlyKeyName;
                stepsOnlyKeyInReusableMark = keyMark;
            }

            if (IsKnownJobKey(keyUtf8))
            {
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var key = Encoding.UTF8.GetString(keyUtf8);

            AddError(diagnostics, $"unexpected job key '{key}' in job '{DecodeUtf8(source, jobId)}'", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (hasUses && hasSteps)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' cannot have both uses and steps", jobIdMark);
        }

        if (hasUses && hasRunsOn)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' cannot have both uses and runs-on", jobIdMark);
        }

        if (hasUses && stepsOnlyKeyInReusable is not null)
        {
            AddError(
                diagnostics,
                $"when job '{DecodeUtf8(source, jobId)}' calls reusable workflow with uses, key '{stepsOnlyKeyInReusable}' is not allowed",
                stepsOnlyKeyInReusableMark);
        }

        if (!hasUses && !hasRunsOn)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' requires runs-on (or uses)", jobIdMark);
        }

        if (!hasUses && !hasSteps)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' requires steps (or uses)", jobIdMark);
        }

        if (!hasUses && hasWith)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' key 'with' requires uses", jobIdMark);
        }

        if (!hasUses && hasSecrets)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' key 'secrets' requires uses", jobIdMark);
        }
    }

    private static void ParseSteps(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
    {
        // current is SequenceStart
        reader.Read();

        var stepIndex = 0;
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            stepIndex++;
            ParseStep(ref reader, diagnostics, source, jobId, stepIndex);
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }
    }

    private static void ParseStep(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, int stepIndex)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        var hasRun = false;
        var hasUses = false;

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();

            if (keyUtf8.SequenceEqual("run"u8))
            {
                reader.Read();
                hasRun = true;
                if (!reader.End)
                {
                    if (reader.CurrentKind != YamlEventKind.Scalar)
                    {
                        AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] run must be scalar", reader.CurrentStart);
                        reader.SkipCurrentNode();
                    }
                    else
                    {
                        var valueMark = reader.CurrentStart;
                        var valueUtf8 = reader.GetScalarUtf8();
                        ValidateExpressionText(
                            valueUtf8,
                            BuildScalarLocation(valueMark, valueUtf8.Length),
                            ExpressionValidationContext.Step,
                            diagnostics,
                            parseWholeValueIfNoEmbedded: false);
                        reader.Read();
                    }
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("uses"u8))
            {
                reader.Read();
                hasUses = true;
                if (!reader.End)
                {
                    if (reader.CurrentKind != YamlEventKind.Scalar)
                    {
                        AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] uses must be scalar", reader.CurrentStart);
                    }
                    reader.SkipCurrentNode();
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("if"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    ParseConditionalExpression(
                        ref reader,
                        diagnostics,
                        ExpressionValidationContext.Step,
                        $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] if must be scalar");
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("with"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    ParseStringMapping(
                        ref reader,
                        diagnostics,
                        $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] with must be mapping",
                        ExpressionValidationContext.Step);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    ParseStringMapping(
                        ref reader,
                        diagnostics,
                        $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] env must be mapping",
                        ExpressionValidationContext.Step);
                }
                continue;
            }

            reader.Read();

            if (IsKnownStepKey(keyUtf8))
            {
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var key = Encoding.UTF8.GetString(keyUtf8);

            AddError(diagnostics, $"unexpected step key '{key}' in job '{DecodeUtf8(source, jobId)}' step[{stepIndex}]", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (hasRun && hasUses)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] cannot have both run and uses", reader.CurrentStart);
        }

        if (!hasRun && !hasUses)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] requires run or uses", reader.CurrentStart);
        }
    }

    private static bool IsKnownJobKey(ReadOnlySpan<byte> keyUtf8)
    {
        return keyUtf8.SequenceEqual("name"u8)
            || keyUtf8.SequenceEqual("needs"u8)
            || keyUtf8.SequenceEqual("if"u8)
            || keyUtf8.SequenceEqual("permissions"u8)
            || keyUtf8.SequenceEqual("env"u8)
            || keyUtf8.SequenceEqual("defaults"u8)
            || keyUtf8.SequenceEqual("timeout-minutes"u8)
            || keyUtf8.SequenceEqual("strategy"u8)
            || keyUtf8.SequenceEqual("concurrency"u8)
            || keyUtf8.SequenceEqual("container"u8)
            || keyUtf8.SequenceEqual("services"u8)
            || keyUtf8.SequenceEqual("outputs"u8)
            || keyUtf8.SequenceEqual("secrets"u8)
            || keyUtf8.SequenceEqual("with"u8);
    }

    private static bool TryGetStepsOnlyReusableJobKeyName(ReadOnlySpan<byte> keyUtf8, out string keyName)
    {
        if (keyUtf8.SequenceEqual("runs-on"u8)) { keyName = "runs-on"; return true; }
        if (keyUtf8.SequenceEqual("environment"u8)) { keyName = "environment"; return true; }
        if (keyUtf8.SequenceEqual("outputs"u8)) { keyName = "outputs"; return true; }
        if (keyUtf8.SequenceEqual("env"u8)) { keyName = "env"; return true; }
        if (keyUtf8.SequenceEqual("defaults"u8)) { keyName = "defaults"; return true; }
        if (keyUtf8.SequenceEqual("steps"u8)) { keyName = "steps"; return true; }
        if (keyUtf8.SequenceEqual("timeout-minutes"u8)) { keyName = "timeout-minutes"; return true; }
        if (keyUtf8.SequenceEqual("continue-on-error"u8)) { keyName = "continue-on-error"; return true; }
        if (keyUtf8.SequenceEqual("container"u8)) { keyName = "container"; return true; }

        keyName = string.Empty;
        return false;
    }

    private static bool IsKnownStepKey(ReadOnlySpan<byte> keyUtf8)
    {
        return keyUtf8.SequenceEqual("name"u8)
            || keyUtf8.SequenceEqual("id"u8)
            || keyUtf8.SequenceEqual("if"u8)
            || keyUtf8.SequenceEqual("with"u8)
            || keyUtf8.SequenceEqual("env"u8)
            || keyUtf8.SequenceEqual("shell"u8)
            || keyUtf8.SequenceEqual("working-directory"u8)
            || keyUtf8.SequenceEqual("timeout-minutes"u8)
            || keyUtf8.SequenceEqual("continue-on-error"u8);
    }

    private static Utf8Slice ReadScalarOrSkip(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, string errorMessage)
    {
        if (reader.End)
        {
            return default;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var slice = reader.GetScalarSlice();
        reader.Read();
        return slice;
    }

    private static void ParseOn(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var eventMark = reader.CurrentStart;
            var eventInfo = ReadOnEventInfo(ref reader);
            ValidateKnownOnEvent(in eventInfo, eventMark, diagnostics);
            reader.Read();
            return;
        }

        if (reader.CurrentKind == YamlEventKind.SequenceStart)
        {
            ParseOnSequence(ref reader, diagnostics);
            return;
        }

        if (reader.CurrentKind == YamlEventKind.MappingStart)
        {
            ParseOnMapping(ref reader, diagnostics);
            return;
        }

        AddError(diagnostics, "on must be scalar, sequence, or mapping", reader.CurrentStart);
        reader.SkipCurrentNode();
    }

    private static void ParseOnSequence(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics)
    {
        reader.Read(); // consume SequenceStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on sequence item must be scalar event name", reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            var eventMark = reader.CurrentStart;
            var eventInfo = ReadOnEventInfo(ref reader);
            ValidateKnownOnEvent(in eventInfo, eventMark, diagnostics);
            reader.Read();
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }
    }

    private static void ParseOnMapping(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics)
    {
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on mapping key must be scalar event name", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var eventInfo = ReadOnEventInfo(ref reader);
            var eventMark = reader.CurrentStart;
            ValidateKnownOnEvent(in eventInfo, eventMark, diagnostics);
            reader.Read(); // consume event key

            if (reader.End)
            {
                break;
            }

            if (reader.CurrentKind == YamlEventKind.MappingStart)
            {
                ParseOnEventOptions(ref reader, diagnostics, in eventInfo, eventMark);
                continue;
            }

            if (reader.CurrentKind is YamlEventKind.Scalar or YamlEventKind.SequenceStart)
            {
                // Some events can be represented as scalar/sequence/null-like value; accept shape and skip.
                reader.SkipCurrentNode();
                continue;
            }

            AddError(diagnostics, $"on.{eventInfo.Name} must be scalar, sequence, or mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }
    }

    private static void ParseOnEventOptions(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, in OnEventInfo eventInfo, TextPosition eventMark)
    {
        var hasBranches = false;
        var hasBranchesIgnore = false;
        var hasTags = false;
        var hasTagsIgnore = false;
        var hasPaths = false;
        var hasPathsIgnore = false;

        reader.Read(); // consume MappingStart

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"on.{eventInfo.Name} option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();

            if (keyUtf8.SequenceEqual("types"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeOptionSupported())
                {
                    AddError(diagnostics, $"on.{eventInfo.Name}.types is not supported", keyMark);
                    reader.SkipCurrentNode();
                    continue;
                }

                ParseOnTypes(ref reader, diagnostics, in eventInfo);
                continue;
            }

            if (eventInfo.IsKnown && !eventInfo.Spec.IsOptionAllowed(keyUtf8))
            {
                var key = Encoding.UTF8.GetString(keyUtf8);
                reader.Read();
                AddError(diagnostics, $"on.{eventInfo.Name} does not support option: {key}", keyMark);
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
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.branches must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("branches-ignore"u8))
            {
                reader.Read();
                hasBranchesIgnore = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.branches-ignore must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("tags"u8))
            {
                reader.Read();
                hasTags = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.tags must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("tags-ignore"u8))
            {
                reader.Read();
                hasTagsIgnore = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.tags-ignore must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("paths"u8))
            {
                reader.Read();
                hasPaths = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.paths must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("paths-ignore"u8))
            {
                reader.Read();
                hasPathsIgnore = true;
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.paths-ignore must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("workflows"u8))
            {
                reader.Read();
                ParseScalarOrScalarSequence(ref reader, diagnostics, $"on.{eventInfo.Name}.workflows must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("inputs"u8) || keyUtf8.SequenceEqual("secrets"u8) || keyUtf8.SequenceEqual("outputs"u8))
            {
                var key = keyUtf8.SequenceEqual("inputs"u8)
                    ? "inputs"
                    : keyUtf8.SequenceEqual("secrets"u8)
                        ? "secrets"
                        : "outputs";
                reader.Read();
                if (reader.CurrentKind != YamlEventKind.MappingStart)
                {
                    AddError(diagnostics, $"on.{eventInfo.Name}.{key} must be mapping", reader.CurrentStart);
                }
                reader.SkipCurrentNode();
                continue;
            }

            if (reader.End)
            {
                break;
            }

            var unknownKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected on.{eventInfo.Name} option: {unknownKey}", keyMark);
            reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (hasBranches && hasBranchesIgnore)
        {
            AddError(diagnostics, $"on.{eventInfo.Name} cannot use both branches and branches-ignore", eventMark);
        }

        if (hasTags && hasTagsIgnore)
        {
            AddError(diagnostics, $"on.{eventInfo.Name} cannot use both tags and tags-ignore", eventMark);
        }

        if (hasPaths && hasPathsIgnore)
        {
            AddError(diagnostics, $"on.{eventInfo.Name} cannot use both paths and paths-ignore", eventMark);
        }
    }

    private static void ParseScalarOrScalarSequence(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        string error,
        Utf8ScalarValidator? scalarValidator = null)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            if (scalarValidator is not null)
            {
                var valueUtf8 = reader.GetScalarUtf8();
                var validationError = scalarValidator(valueUtf8);
                if (validationError is not null)
                {
                    AddError(diagnostics, validationError, reader.CurrentStart);
                }
            }
            reader.Read();
            return;
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            if (scalarValidator is not null)
            {
                var valueUtf8 = reader.GetScalarUtf8();
                var validationError = scalarValidator(valueUtf8);
                if (validationError is not null)
                {
                    AddError(diagnostics, validationError, reader.CurrentStart);
                }
            }

            reader.Read();
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }
    }

    private static void ParseStrategy(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
    {
        reader.Read(); // consume MappingStart

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();

            if (keyUtf8.SequenceEqual("matrix"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                ParseMatrix(ref reader, diagnostics, source, jobId);
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

            var key = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected strategy key '{key}' in job '{DecodeUtf8(source, jobId)}'", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }
    }

    private static void ParseMatrix(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            reader.Read();
            return;
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix must be scalar or mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read(); // consume matrix mapping
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
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
                if (reader.CurrentKind is not YamlEventKind.SequenceStart and not YamlEventKind.Scalar)
                {
                    var keyTextForDiagnostic = isInclude ? "include" : "exclude";
                    AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{keyTextForDiagnostic} must be sequence or scalar", reader.CurrentStart);
                }
                reader.SkipCurrentNode();
                continue;
            }

            if (reader.CurrentKind is not YamlEventKind.SequenceStart and not YamlEventKind.Scalar)
            {
                var keyTextForDiagnostic = Encoding.UTF8.GetString(keyUtf8);
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{keyTextForDiagnostic} must be sequence or scalar", reader.CurrentStart);
            }
            reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }
    }

    private static void ParseServices(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' services must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read(); // consume services mapping
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' services key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var serviceName = reader.GetScalarSlice();
            reader.Read();
            if (reader.End)
            {
                break;
            }

            ParseContainerLike(ref reader, diagnostics, source, jobId, serviceName, isService: true, requireImage: true);
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }
    }

    private static void ParseContainerLike(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        Utf8Slice jobId,
        Utf8Slice serviceName,
        bool isService,
        bool requireImage)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            reader.Read();
            return;
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} must be scalar or mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        var hasImage = false;
        reader.Read(); // consume mapping

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();

            if (keyUtf8.SequenceEqual("image"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                hasImage = true;
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.image must be scalar", reader.CurrentStart);
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

                ParseCredentials(ref reader, diagnostics, source, jobId, serviceName, isService);
                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                if (reader.CurrentKind != YamlEventKind.MappingStart)
                {
                    AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.env must be mapping", reader.CurrentStart);
                }
                reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("ports"u8) || keyUtf8.SequenceEqual("volumes"u8))
            {
                var optionKey = keyUtf8.SequenceEqual("ports"u8) ? "ports" : "volumes";
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                ParseScalarOrScalarSequence(ref reader, diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.{optionKey} must be scalar or sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("options"u8))
            {
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.options must be scalar", reader.CurrentStart);
                }
                reader.SkipCurrentNode();
                continue;
            }

            var key = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            if (reader.End)
            {
                break;
            }

            AddError(diagnostics, $"unexpected {FormatContainerSectionName(source, jobId, serviceName, isService)} key: {key}", keyMark);
            reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (requireImage && !hasImage)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.image is required", new TextPosition(0, 1, 1));
        }
    }

    private static void ParseCredentials(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        Utf8Slice jobId,
        Utf8Slice serviceName,
        bool isService)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        var hasUsername = false;
        var hasPassword = false;
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
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
                var unexpectedKey = Encoding.UTF8.GetString(keyUtf8);
                AddError(diagnostics, $"unexpected {FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials key: {unexpectedKey}", keyMark);
            }

            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                var fieldName = keyUtf8.SequenceEqual("username"u8)
                    ? "username"
                    : keyUtf8.SequenceEqual("password"u8)
                        ? "password"
                        : Encoding.UTF8.GetString(keyUtf8);
                AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials.{fieldName} must be scalar", reader.CurrentStart);
            }
            reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (!hasUsername || !hasPassword)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials requires both username and password", new TextPosition(0, 1, 1));
        }
    }

    private static void ParseStringMapping(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        string error,
        ExpressionValidationContext? expressionContext = null)
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
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

            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            if (expressionContext.HasValue)
            {
                var valueMark = reader.CurrentStart;
                var valueUtf8 = reader.GetScalarUtf8();
                ValidateExpressionText(
                    valueUtf8,
                    BuildScalarLocation(valueMark, valueUtf8.Length),
                    expressionContext.Value,
                    diagnostics,
                    parseWholeValueIfNoEmbedded: false);
                reader.Read();
                continue;
            }

            reader.Read();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }
    }

    private static void ParseJobSecrets(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var valueUtf8 = reader.GetScalarUtf8();
            if (!valueUtf8.SequenceEqual("inherit"u8))
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' secrets scalar must be 'inherit'", reader.CurrentStart);
            }
            reader.Read();
            return;
        }

        ParseStringMapping(ref reader, diagnostics, $"job '{DecodeUtf8(source, jobId)}' secrets must be mapping or scalar 'inherit'");
    }

    private static void ParseConditionalExpression(
        ref VYamlStreamAdapter reader,
        List<Diagnostic> diagnostics,
        ExpressionValidationContext context,
        string shapeError)
    {
        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, shapeError, reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        var valueMark = reader.CurrentStart;
        var valueUtf8 = reader.GetScalarUtf8();
        var valueLocation = BuildScalarLocation(valueMark, valueUtf8.Length);
        ValidateExpressionText(valueUtf8, valueLocation, context, diagnostics, parseWholeValueIfNoEmbedded: true);
        reader.Read();
    }

    private static void ValidateExpressionText(
        ReadOnlySpan<byte> valueUtf8,
        TextRange valueLocation,
        ExpressionValidationContext context,
        List<Diagnostic> diagnostics,
        bool parseWholeValueIfNoEmbedded)
    {
        var hasEmbedded = false;
        var i = 0;
        while (i + 3 < valueUtf8.Length)
        {
            if (valueUtf8[i] == (byte)'$' && valueUtf8[i + 1] == (byte)'{' && valueUtf8[i + 2] == (byte)'{')
            {
                hasEmbedded = true;
                var exprStart = i + 3;
                var end = IndexOf(valueUtf8, exprStart, "}}"u8);
                if (end < 0)
                {
                    break;
                }

                var trimmed = TrimAsciiWhiteSpace(valueUtf8, exprStart, end - exprStart);
                if (trimmed.Length > 0)
                {
                    var expressionUtf8 = valueUtf8.Slice(trimmed.Offset, trimmed.Length);
                    var expressionLocation = ShiftLocation(valueLocation, trimmed.Offset, trimmed.Length);
                    ParseAndValidateExpression(expressionUtf8, expressionLocation, context, diagnostics);
                }

                i = end + 2;
                continue;
            }

            i++;
        }

        if (!hasEmbedded && parseWholeValueIfNoEmbedded)
        {
            var trimmed = TrimAsciiWhiteSpace(valueUtf8, 0, valueUtf8.Length);
            if (trimmed.Length <= 0)
            {
                return;
            }

            ParseAndValidateExpression(
                valueUtf8.Slice(trimmed.Offset, trimmed.Length),
                ShiftLocation(valueLocation, trimmed.Offset, trimmed.Length),
                context,
                diagnostics);
        }
    }

    private static void ParseAndValidateExpression(
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        ExpressionValidationContext context,
        List<Diagnostic> diagnostics)
    {
        var parseResult = ExpressionParser.Parse(expressionUtf8);
        for (var i = 0; i < parseResult.Diagnostics.Length; i++)
        {
            var parseDiagnostic = parseResult.Diagnostics[i];
            diagnostics.Add(new Diagnostic(
                parseDiagnostic.Severity,
                $"expression parse error: {parseDiagnostic.Message}",
                ShiftLocation(expressionLocation, parseDiagnostic.Location.Start, parseDiagnostic.Location.Length)));
        }

        var semanticDiagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expressionUtf8,
            expressionLocation,
            context);
        for (var i = 0; i < semanticDiagnostics.Length; i++)
        {
            diagnostics.Add(semanticDiagnostics[i]);
        }
    }

    private static TextRange BuildScalarLocation(TextPosition mark, int length)
    {
        var safeLength = length <= 0 ? 1 : length;
        return new TextRange(
            Start: mark.Position,
            Length: safeLength,
            StartLine: mark.Line,
            StartColumn: mark.Col,
            EndLine: mark.Line,
            EndColumn: mark.Col + safeLength - 1);
    }

    private static TextRange ShiftLocation(TextRange baseLocation, int relativeOffset, int length)
    {
        var safeLength = length <= 0 ? 1 : length;
        return new TextRange(
            Start: baseLocation.Start + relativeOffset,
            Length: safeLength,
            StartLine: baseLocation.StartLine,
            StartColumn: baseLocation.StartColumn + relativeOffset,
            EndLine: baseLocation.EndLine,
            EndColumn: baseLocation.StartColumn + relativeOffset + safeLength - 1);
    }

    private static Utf8Slice TrimAsciiWhiteSpace(ReadOnlySpan<byte> source, int offset, int length)
    {
        if (length <= 0)
        {
            return new Utf8Slice(offset, 0);
        }

        var start = offset;
        var end = offset + length - 1;

        while (start <= end && IsAsciiWhiteSpace(source[start]))
        {
            start++;
        }

        while (end >= start && IsAsciiWhiteSpace(source[end]))
        {
            end--;
        }

        if (end < start)
        {
            return new Utf8Slice(offset, 0);
        }

        return new Utf8Slice(start, end - start + 1);
    }

    private static int IndexOf(ReadOnlySpan<byte> source, int start, ReadOnlySpan<byte> pattern)
    {
        if (pattern.IsEmpty || start >= source.Length)
        {
            return -1;
        }

        for (var i = start; i <= source.Length - pattern.Length; i++)
        {
            if (source.Slice(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsAsciiWhiteSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static string DecodeUtf8(ReadOnlySpan<byte> source, Utf8Slice slice)
    {
        return Encoding.UTF8.GetString(slice.AsSpan(source));
    }

    private static string FormatContainerSectionName(ReadOnlySpan<byte> source, Utf8Slice jobId, Utf8Slice serviceName, bool isService)
    {
        var jobIdText = DecodeUtf8(source, jobId);
        if (!isService)
        {
            return $"job '{jobIdText}' container";
        }

        return $"job '{jobIdText}' service '{DecodeUtf8(source, serviceName)}'";
    }

    private static void AddError(List<Diagnostic> diagnostics, string message, TextPosition mark)
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

    private static void ValidateKnownOnEvent(in OnEventInfo eventInfo, TextPosition eventMark, List<Diagnostic> diagnostics)
    {
        if (!eventInfo.IsKnown)
        {
            AddError(diagnostics, $"unknown event in on: {eventInfo.Name}", eventMark);
        }
    }

    private static OnEventInfo ReadOnEventInfo(ref VYamlStreamAdapter reader)
    {
        try
        {
            var eventNameUtf8 = reader.GetScalarUtf8();
            if (OnEventSpecs.TryGet(eventNameUtf8, out var knownEventName, out var knownSpec))
            {
                return new OnEventInfo(knownEventName, isKnown: true, knownSpec);
            }

            return new OnEventInfo(Encoding.UTF8.GetString(eventNameUtf8), isKnown: false, default);
        }
        catch
        {
            // Fall back to scalar string for odd scalar representations.
        }

        return new OnEventInfo(reader.GetScalarString() ?? string.Empty, isKnown: false, default);
    }

    private static void ParseOnTypes(ref VYamlStreamAdapter reader, List<Diagnostic> diagnostics, in OnEventInfo eventInfo)
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var valueUtf8 = reader.GetScalarUtf8();
            if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeAllowed(valueUtf8))
            {
                AddError(diagnostics, $"on.{eventInfo.Name}.types contains unsupported activity type: {Encoding.UTF8.GetString(valueUtf8)}", reader.CurrentStart);
            }

            reader.Read();
            return;
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, $"on.{eventInfo.Name}.types must be scalar or sequence of scalar", reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"on.{eventInfo.Name}.types must be scalar or sequence of scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            var valueUtf8 = reader.GetScalarUtf8();
            if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeAllowed(valueUtf8))
            {
                AddError(diagnostics, $"on.{eventInfo.Name}.types contains unsupported activity type: {Encoding.UTF8.GetString(valueUtf8)}", reader.CurrentStart);
            }

            reader.Read();
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }
    }
}
