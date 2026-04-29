using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private enum StepMappingKey : byte
    {
        Run = 0,
        Uses = 1,
        Name = 2,
        Id = 3,
        If = 4,
        With = 5,
        Shell = 6,
        WorkingDirectory = 7,
        TimeoutMinutes = 8,
        ContinueOnError = 9,
        Env = 10,
    }

    private readonly struct StepMappingKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 11;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "run"u8,
            1 => "uses"u8,
            2 => "name"u8,
            3 => "id"u8,
            4 => "if"u8,
            5 => "with"u8,
            6 => "shell"u8,
            7 => "working-directory"u8,
            8 => "timeout-minutes"u8,
            9 => "continue-on-error"u8,
            10 => "env"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private const string ActionStepExpectedKeys = Generated.ExpectedKeys.ActionStepKeys;
    private const string RunStepExpectedKeys = Generated.ExpectedKeys.RunStepKeys;

    /// <summary>Formats a diagnostic prefix like <c>jobs.'build'.steps[1]</c>. When jobId is empty (action metadata), returns <c>steps[1]</c>.</summary>
    private static string FormatStepPrefix(ReadOnlySpan<byte> source, Utf8Slice jobId, int stepIndex)
    {
        return jobId.Length > 0
            ? $"jobs.'{DecodeUtf8(source, jobId)}'.steps[{stepIndex}]"
            : $"steps[{stepIndex}]";
    }

    private static Step[] ParseSteps<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var steps = new PooledBuffer<Step>(8);
        try
        {
            reader.Read(); // consume SequenceStart

            var stepIndex = 0;
            while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
            {
                stepIndex++;
                var step = ParseStep(ref reader, arena, diagnostics, source, jobId, stepIndex);
                if (step is not null)
                {
                    steps.Add(step);
                }
            }

            if (reader.CurrentKind == YamlEventKind.SequenceEnd)
            {
                reader.Read();
            }

            return steps.ToArray();
        }
        finally { steps.Dispose(); }
    }

    private static Step? ParseStep<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, int stepIndex)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            var prefix = FormatStepPrefix(source, jobId, stepIndex);
            // Null scalar, bare dash, or other non-mapping → "element should not be empty"
            if (reader.CurrentKind == YamlEventKind.Scalar
                && (reader.GetScalarTag() == ScalarTag.Null || reader.GetScalarUtf8().Length == 0))
            {
                AddError(diagnostics, $"{prefix} element of \"steps\" section should not be empty. please remove this section if it's unnecessary", reader.CurrentStart);
                AddError(diagnostics, $"{prefix} must run script with \"run\" section or run action with \"uses\" section", reader.CurrentStart);
            }
            else
            {
                AddError(diagnostics, $"{prefix} must be object", reader.CurrentStart);
                AddError(diagnostics, $"{prefix} must run script with \"run\" section or run action with \"uses\" section", reader.CurrentStart);
            }
            reader.SkipCurrentNode();
            return default;
        }

        var stepMark = reader.CurrentStart;
        var hasRun = false;
        var hasUses = false;
        // stepForm: 0=unknown, 1=run, 2=action (determined by run/uses key; last primary wins)
        var stepForm = 0;
        TextPosition firstPrimaryMark = default;
        TextPosition shellKeyMark = default;
        TextPosition wdKeyMark = default;
        TextPosition withKeyMark = default;
        // Deferred unknown keys: when stepForm==0 we don't know the step type yet,
        // so we defer reporting until after the mapping loop when stepForm is determined.
        string? deferredUnknownKey = null;
        TextPosition deferredUnknownMark = default;
        var hasAnyKey = false;
        StringNodeId idNode = default;
        StringNodeId ifNode = default;
        StringNodeId nameNode = default;
        Env? envNode = null;
        BoolNodeId continueOnErrorNode = default;
        FloatNodeId timeoutMinutesNode = default;
        StringNodeId runNode = default;
        StringNodeId usesNode = default;
        TextRange? usesKeyRange = null;
        StringNodeId shellNode = default;
        StringNodeId workingDirectoryNode = default;
        SliceMap<StringNodeId>? withInputs = null;
        StringNodeId dockerEntrypoint = default;
        StringNodeId dockerArgs = default;

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            hasAnyKey = true;
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();

            if (IsMergeKey(keyUtf8, keyMark, diagnostics, FormatStepPrefix(source, jobId, stepIndex)))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<StepMappingKeyTable>(keyUtf8, out var stepKeyOrd))
            {
                var keyLen = keyUtf8.Length;
                reader.Read();
                switch ((StepMappingKey)stepKeyOrd)
                {
                    case StepMappingKey.Run:
                        if (stepForm == 2) // was action, now becomes run; flag previous primary (uses)
                        {
                            AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} unexpected key \"uses\" for step to run shell command. expected one of {RunStepExpectedKeys}", firstPrimaryMark);
                        }
                        firstPrimaryMark = keyMark;
                        stepForm = 1;
                        hasRun = true;
                        if (!reader.End)
                        {
                            runNode = ParseStringAndValidateExpression(
                                ref reader, arena, diagnostics,
                                ExpressionValidationContext.StepRun,
                                out var runErr,
                                out var runMark,
                                parseWholeValueIfNoEmbedded: false);
                            if (runErr) AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} run must be string", runMark);
                        }

                        break;

                    case StepMappingKey.Uses:
                        usesKeyRange = BuildScalarLocation(keyMark, keyLen);
                        if (stepForm == 1) // was run, now becomes action; flag previous primary (run)
                        {
                            AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} unexpected key \"run\" for step to execute action. expected one of {ActionStepExpectedKeys}", firstPrimaryMark);
                        }
                        firstPrimaryMark = keyMark;
                        stepForm = 2;
                        hasUses = true;
                        if (!reader.End)
                        {
                            usesNode = ParseString(ref reader, arena, out var usesErr, out var usesMark);
                            if (usesErr) AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} uses must be string", usesMark);
                        }

                        break;

                    case StepMappingKey.Name:
                        if (!reader.End)
                        {
                            nameNode = ParseString(ref reader, arena, out var nameErr, out var nameMark);
                            if (nameErr) AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} name must be string", nameMark);
                        }

                        break;

                    case StepMappingKey.Id:
                        if (!reader.End)
                        {
                            idNode = ParseString(ref reader, arena, out var idErr, out var idMark);
                            if (idErr)
                            {
                                var idMsg = idNode.HasValue
                                    ? $"{FormatStepPrefix(source, jobId, stepIndex)} step id should not be empty"
                                    : $"{FormatStepPrefix(source, jobId, stepIndex)} id must be string";
                                AddError(diagnostics, idMsg, idMark);
                            }
                        }

                        break;

                    case StepMappingKey.If:
                        if (!reader.End)
                        {
                            ifNode = ParseExpression(
                                ref reader, arena, diagnostics,
                                ExpressionValidationContext.StepIf,
                                out var ifErr,
                                out var ifMark);
                            if (ifErr) AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} if must be string", ifMark);
                        }

                        break;

                    case StepMappingKey.With:
                        withKeyMark = keyMark;
                        if (!reader.End)
                        {
                            withInputs = ParseStepWithInputsNode(
                                ref reader, arena, diagnostics,
                                source,
                                jobId,
                                stepIndex,
                                out var entrypoint,
                                out var args);
                            dockerEntrypoint = entrypoint;
                            dockerArgs = args;
                        }

                        break;

                    case StepMappingKey.Shell:
                        shellKeyMark = keyMark;
                        if (!reader.End)
                        {
                            shellNode = ParseString(ref reader, arena, out var shellErr, out var shellMark);
                            if (shellErr) AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} shell must be string", shellMark);
                        }

                        break;

                    case StepMappingKey.WorkingDirectory:
                        wdKeyMark = keyMark;
                        if (!reader.End)
                        {
                            workingDirectoryNode = ParseStringAndValidateExpression(
                                ref reader, arena, diagnostics,
                                ExpressionValidationContext.StepWorkingDirectory,
                                out var wdErr,
                                out var wdMark,
                                parseWholeValueIfNoEmbedded: false);
                            if (wdErr) AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} working-directory must be string", wdMark);
                        }

                        break;

                    case StepMappingKey.TimeoutMinutes:
                        if (!reader.End)
                        {
                            timeoutMinutesNode = ParseFloatOrExpression(ref reader, arena, diagnostics, ExpressionValidationContext.StepTimeoutMinutes, out var tmErr, out var tmMark);
                            if (tmErr) AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} timeout-minutes must be number or expression", tmMark);
                            if (timeoutMinutesNode.HasValue && !arena.GetFloatExpression(timeoutMinutesNode).HasValue && arena.GetFloatValue(timeoutMinutesNode) <= 0)
                            {
                                AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} timeout-minutes must be greater than 0", keyMark);
                            }
                        }

                        break;

                    case StepMappingKey.ContinueOnError:
                        if (!reader.End)
                        {
                            continueOnErrorNode = ParseBoolOrExpression(ref reader, arena, diagnostics, ExpressionValidationContext.StepContinueOnError, out var coeErr, out var coeMark);
                            if (coeErr) AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} continue-on-error must be bool or expression", coeMark);
                        }

                        break;

                    case StepMappingKey.Env:
                        if (!reader.End)
                        {
                            envNode = ParseEnvNode(
                                ref reader, arena, diagnostics,
                                source,
                                $"{FormatStepPrefix(source, jobId, stepIndex)} env must be object",
                                ExpressionValidationContext.StepEnv,
                                $"{FormatStepPrefix(source, jobId, stepIndex)} env");
                        }

                        break;
                }

                continue;
            }

            // keyUtf8 span is invalidated by reader.Read(); capture what we need BEFORE advancing.
            var isKnownButNotHandled = IsKnownStepKey(keyUtf8);
            var unknownKey = isKnownButNotHandled ? null : Encoding.UTF8.GetString(keyUtf8);

            reader.Read();

            if (isKnownButNotHandled)
            {
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            if (stepForm == 2)
            {
                AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} unexpected key \"{unknownKey}\" for step to execute action. expected one of {ActionStepExpectedKeys}", keyMark);
            }
            else if (stepForm == 1)
            {
                AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} unexpected key \"{unknownKey}\" for step to run shell command. expected one of {RunStepExpectedKeys}", keyMark);
            }
            else if (deferredUnknownKey is null)
            {
                // stepForm == 0: defer until post-mapping when step type is known
                deferredUnknownKey = unknownKey;
                deferredUnknownMark = keyMark;
            }
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        // Post-mapping: report deferred unknown keys now that step type is known
        if (deferredUnknownKey is not null && stepForm != 0)
        {
            if (stepForm == 2)
                AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} unexpected key \"{deferredUnknownKey}\" for step to execute action. expected one of {ActionStepExpectedKeys}", deferredUnknownMark);
            else
                AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} unexpected key \"{deferredUnknownKey}\" for step to run shell command. expected one of {RunStepExpectedKeys}", deferredUnknownMark);
        }

        // Post-mapping: report secondary key conflicts based on step form
        if (stepForm == 2) // action step: shell and working-directory are unexpected
        {
            if (shellKeyMark != default)
                AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} unexpected key \"shell\" for step to execute action. expected one of {ActionStepExpectedKeys}", shellKeyMark);
            if (wdKeyMark != default)
                AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} unexpected key \"working-directory\" for step to execute action. expected one of {ActionStepExpectedKeys}", wdKeyMark);
        }
        else if (stepForm == 1) // run step: with is unexpected
        {
            if (withKeyMark != default)
                AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} unexpected key \"with\" for step to run shell command. expected one of {RunStepExpectedKeys}", withKeyMark);
        }

        // Empty mapping (e.g. `- {}`)
        // VYaml may report MappingStart mark past the closing '}' for flow mappings.
        // Correct by scanning backward in source bytes for '{'.
        var emptyMark = stepMark;
        if (!hasAnyKey && stepMark.Offset > 0 && stepMark.Offset <= source.Length)
        {
            for (var i = stepMark.Offset - 1; i >= 0 && i > stepMark.Offset - 80; i--)
            {
                if (source[i] == (byte)'{')
                {
                    emptyMark = reader.ComputePositionFromOffset(i);
                    break;
                }
            }
        }

        if (!hasAnyKey)
        {
            AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} element of \"steps\" section should not be empty. please remove this section if it's unnecessary", emptyMark);
        }

        // spec §3.12: a step must choose one execution form: `run` or `uses`
        if (!hasRun && !hasUses)
        {
            AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} must run script with \"run\" section or run action with \"uses\" section", hasAnyKey ? stepMark : emptyMark);
        }

        StepExec exec;
        if (hasRun)
        {
            exec = new ExecRun
            {
                Kind = StepExecKind.Run,
                Run = runNode.HasValue ? runNode : arena.AddString(default, false, default),
                Shell = shellNode,
                WorkingDirectory = workingDirectoryNode,
                Range = runNode.HasValue ? arena.GetStringRange(runNode) : default,
            };
        }
        else
        {
            exec = new ExecAction
            {
                Kind = StepExecKind.Action,
                Uses = usesNode.HasValue ? usesNode : arena.AddString(default, false, default),
                UsesKeyRange = usesKeyRange,
                Inputs = withInputs,
                Entrypoint = dockerEntrypoint,
                Args = dockerArgs,
                Range = usesNode.HasValue ? arena.GetStringRange(usesNode) : default,
            };
        }

        return new Step
        {
            Id = idNode,
            If = ifNode,
            Name = nameNode,
            Exec = exec,
            Env = envNode,
            ContinueOnError = continueOnErrorNode,
            TimeoutMinutes = timeoutMinutesNode,
            Range = exec.Range,
        };
    }

    private static SliceMap<StringNodeId>? ParseStepWithInputsNode<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, int stepIndex, out StringNodeId entrypoint, out StringNodeId args)
        where TReader : IYamlStreamReader, allows ref struct
    {
        entrypoint = default;
        args = default;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "\"with\" section is scalar node but mapping node is expected", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var map = new PooledBuffer<SliceMap<StringNodeId>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} with must be object", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }
                    continue;
                }

                var keyMark = reader.CurrentStart;
                var keySlice = reader.GetScalarSlice();
                var keyUtf8 = reader.GetScalarUtf8();
                var isEntrypoint = keyUtf8.SequenceEqual("entrypoint"u8);
                var isArgs = keyUtf8.SequenceEqual("args"u8);

                if (!TryRegisterDynamicKey(
                    source,
                    keyUtf8,
                    keySlice.Offset,
                    keySlice.Length,
                    keyMark,
                    diagnostics,
                    keyStore,
                    ref keyCount,
                    caseSensitive: false,
                    "with"))
                {
                    reader.Read();
                    if (!reader.End)
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

                var value = ParseStringAndValidateExpression(
                    ref reader, arena, diagnostics,
                    ExpressionValidationContext.StepWith,
                    out var withErr,
                    out var withMark,
                    parseWholeValueIfNoEmbedded: false);
                if (withErr) AddError(diagnostics, $"{FormatStepPrefix(source, jobId, stepIndex)} with.{Encoding.UTF8.GetString(keyUtf8)} must be string", withMark);

                if (!value.HasValue)
                {
                    continue;
                }

                map.Add(new SliceMap<StringNodeId>.Entry(keySlice, value));
                if (isEntrypoint)
                {
                    entrypoint = value;
                }
                else if (isArgs)
                {
                    args = value;
                }
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            return new SliceMap<StringNodeId>(map.ToArray(), caseSensitive: false);
        }
        finally { map.Dispose(); }
    }

}
