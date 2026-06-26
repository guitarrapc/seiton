using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private enum StepMappingKey : byte
    {
        Background = 0,
        Cancel = 1,
        ContinueOnError = 2,
        Env = 3,
        Id = 4,
        If = 5,
        Name = 6,
        Parallel = 7,
        Run = 8,
        Shell = 9,
        TimeoutMinutes = 10,
        Uses = 11,
        Wait = 12,
        WaitAll = 13,
        With = 14,
        WorkingDirectory = 15,
    }

    private readonly struct StepMappingKeyTable : IUtf8OrderedKeyTable
    {
        public static int KeyCount => 16;

        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch
        {
            0 => "background"u8,
            1 => "cancel"u8,
            2 => "continue-on-error"u8,
            3 => "env"u8,
            4 => "id"u8,
            5 => "if"u8,
            6 => "name"u8,
            7 => "parallel"u8,
            8 => "run"u8,
            9 => "shell"u8,
            10 => "timeout-minutes"u8,
            11 => "uses"u8,
            12 => "wait"u8,
            13 => "wait-all"u8,
            14 => "with"u8,
            15 => "working-directory"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };
    }

    private const string ActionStepExpectedKeys = StepSchema.ActionStepKeys;
    private const string RunStepExpectedKeys = StepSchema.RunStepKeys;
    private const string WaitStepExpectedKeys = StepSchema.WaitStepKeys;
    private const string WaitAllStepExpectedKeys = StepSchema.WaitAllStepKeys;
    private const string CancelStepExpectedKeys = StepSchema.CancelStepKeys;
    private const string ParallelStepExpectedKeys = StepSchema.ParallelStepKeys;

    private static string FormatStepPrefix(string stepPathPrefix, int stepIndex)
        => $"{stepPathPrefix}[{stepIndex}]";

    private static string GetExpectedKeys(StepSchema.FormId form) => form switch
    {
        StepSchema.FormId.Run => RunStepExpectedKeys,
        StepSchema.FormId.Uses => ActionStepExpectedKeys,
        StepSchema.FormId.Wait => WaitStepExpectedKeys,
        StepSchema.FormId.WaitAll => WaitAllStepExpectedKeys,
        StepSchema.FormId.Cancel => CancelStepExpectedKeys,
        StepSchema.FormId.Parallel => ParallelStepExpectedKeys,
        _ => RunStepExpectedKeys,
    };

    private static StepSchema.FormId FormForPrimaryKey(StepMappingKey key) => key switch
    {
        StepMappingKey.Run => StepSchema.FormId.Run,
        StepMappingKey.Uses => StepSchema.FormId.Uses,
        StepMappingKey.Wait => StepSchema.FormId.Wait,
        StepMappingKey.WaitAll => StepSchema.FormId.WaitAll,
        StepMappingKey.Cancel => StepSchema.FormId.Cancel,
        StepMappingKey.Parallel => StepSchema.FormId.Parallel,
        _ => StepSchema.FormId.Run,
    };

    private static bool IsPrimaryStepKey(StepMappingKey key) => key switch
    {
        StepMappingKey.Run or StepMappingKey.Uses or StepMappingKey.Wait or StepMappingKey.WaitAll
            or StepMappingKey.Cancel or StepMappingKey.Parallel => true,
        _ => false,
    };

    private static string BuildStepDuplicateKeyHelp(ReadOnlySpan<byte> keyUtf8)
    {
        if (keyUtf8.SequenceEqual("env"u8))
        {
            return "YAML mapping keys must be unique. Merge variables into a single env: block.";
        }

        return $"YAML mapping keys must be unique. Keep only one \"{Encoding.UTF8.GetString(keyUtf8)}\" key in this step.";
    }

    private static void ReportPrimaryConflict(
        ref PooledBuffer<Diagnostic> diagnostics,
        string stepPrefix,
        StepMappingKey firstPrimaryKey,
        TextPosition firstPrimaryMark,
        StepSchema.FormId incomingForm)
    {
        var firstKeyName = Encoding.UTF8.GetString(StepMappingKeyTable.Utf8Key((int)firstPrimaryKey));
        var expectedKeys = GetExpectedKeys(incomingForm);
        var desc = StepSchema.GetUnexpectedKeyDescription(incomingForm);
        AddError(
            ref diagnostics,
            $"{stepPrefix} has unexpected key \"{firstKeyName}\" for {desc}. expected one of {expectedKeys}",
            firstPrimaryMark);
    }

    private static void ReportDisallowedStepKey(
        ref PooledBuffer<Diagnostic> diagnostics,
        string stepPrefix,
        string keyName,
        TextPosition keyMark,
        StepSchema.FormId form)
    {
        if (keyMark == default)
        {
            return;
        }

        var expectedKeys = GetExpectedKeys(form);
        var desc = StepSchema.GetUnexpectedKeyDescription(form);
        AddError(
            ref diagnostics,
            $"{stepPrefix} has unexpected key \"{keyName}\" for {desc}. expected one of {expectedKeys}",
            keyMark);
    }

    private static void AddStepUnexpectedKeyError(
        ref PooledBuffer<Diagnostic> diagnostics,
        string stepPrefix,
        string unknownKey,
        Utf8Slice unknownKeySlice,
        TextPosition keyMark,
        StepSchema.FormId form)
    {
        var expectedKeys = GetExpectedKeys(form);
        var desc = StepSchema.GetUnexpectedKeyDescription(form);
        var suggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknownKey, expectedKeys);
        var msg = suggestion is not null
            ? $"{stepPrefix} has unexpected key \"{unknownKey}\" for {desc}. did you mean \"{suggestion}\"? expected one of {expectedKeys}"
            : $"{stepPrefix} has unexpected key \"{unknownKey}\" for {desc}. expected one of {expectedKeys}";
        var fix = suggestion is not null
            ? new DiagnosticFix($"replace '{unknownKey}' with '{suggestion}'", [new TextEdit(unknownKeySlice.Offset, unknownKeySlice.Length, suggestion)])
            : (DiagnosticFix?)null;
        AddError(ref diagnostics, msg, keyMark, fix);
    }

    private static ArenaList<Step> ParseSteps<TReader>(
        ref TReader reader,
        AstArena arena,
        ref PooledBuffer<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        string stepPathPrefix)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var steps = new PooledBuffer<Step>(8);
        try
        {
            reader.Read();

            var stepIndex = 0;
            while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
            {
                stepIndex++;
                var step = ParseStep(ref reader, arena, ref diagnostics, source, stepPathPrefix, stepIndex);
                if (step is not null)
                {
                    steps.Add(step);
                }
            }

            if (reader.CurrentKind == YamlEventKind.SequenceEnd)
            {
                reader.Read();
            }

            return DetachArenaList(ref steps, arena);
        }
        finally { steps.Dispose(); }
    }

    private static Step? ParseStep<TReader>(
        ref TReader reader,
        AstArena arena,
        ref PooledBuffer<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        string stepPathPrefix,
        int stepIndex)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var stepPrefix = FormatStepPrefix(stepPathPrefix, stepIndex);
        const string missingPrimaryMessage =
            "must run script with \"run\" section or run action with \"uses\" section, or use \"wait\", \"wait-all\", \"cancel\", or \"parallel\"";

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            if (reader.CurrentKind == YamlEventKind.Scalar
                && (reader.GetScalarTag() == ScalarTag.Null || reader.GetScalarUtf8().Length == 0))
            {
                AddError(ref diagnostics, $"{stepPrefix} element of \"steps\" section should not be empty. please remove this section if it's unnecessary", reader.CurrentStart);
                AddError(ref diagnostics, $"{stepPrefix} {missingPrimaryMessage}", reader.CurrentStart);
            }
            else if (reader.CurrentKind == YamlEventKind.Alias)
            {
                AddError(ref diagnostics, $"{stepPrefix} element of \"steps\" section is alias node but mapping node is expected", reader.CurrentStart);
                AddError(ref diagnostics, $"{stepPrefix} {missingPrimaryMessage}", reader.CurrentStart);
            }
            else
            {
                AddError(ref diagnostics, $"{stepPrefix} must be object", reader.CurrentStart);
                AddError(ref diagnostics, $"{stepPrefix} {missingPrimaryMessage}", reader.CurrentStart);
            }
            reader.SkipCurrentNode();
            return default;
        }

        var stepMark = reader.CurrentStart;
        StepSchema.FormId? stepForm = null;
        StepMappingKey firstPrimaryKey = default;
        TextPosition firstPrimaryMark = default;
        TextPosition shellKeyMark = default;
        TextPosition wdKeyMark = default;
        TextPosition withKeyMark = default;
        TextPosition backgroundKeyMark = default;
        string? deferredUnknownKey = null;
        TextPosition deferredUnknownMark = default;
        Utf8Slice deferredUnknownKeySlice = default;
        var hasAnyKey = false;
        var hasPrimary = false;
        StringNodeId idNode = default;
        StringNodeId ifNode = default;
        TextPosition ifKeyMark = default;
        StringNodeId nameNode = default;
        BoolNodeId backgroundNode = default;
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
        ArenaList<StringNodeId> waitTargets = default;
        StringNodeId cancelTarget = default;
        ArenaList<Step> parallelSteps = default;
        TextPosition parallelKeyMark = default;
        ulong seen = 0;
        Span<long> stepKeyFirstMark = stackalloc long[StepMappingKeyTable.KeyCount];

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            hasAnyKey = true;
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, $"{stepPrefix} key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, stepPrefix))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("wait-all"u8))
            {
                var keyLen = keyUtf8.Length;
                reader.Read();
                if (!TrySetBit(ref seen, (int)StepMappingKey.WaitAll))
                {
                    var dupName = StepMappingKeyTable.Utf8Key((int)StepMappingKey.WaitAll);
                    var keyName = Encoding.UTF8.GetString(dupName);
                    var prevMark = stepKeyFirstMark[(int)StepMappingKey.WaitAll];
                    var prevLine = (int)(prevMark >> 32);
                    var prevCol = (int)(prevMark & 0xFFFFFFFF);
                    AddError(
                        ref diagnostics,
                        $"{stepPrefix} key \"{keyName}\" is duplicated in step. previously defined at line:{prevLine},col:{prevCol}",
                        keyMark,
                        BuildStepDuplicateKeyHelp(dupName));
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                stepKeyFirstMark[(int)StepMappingKey.WaitAll] = ((long)keyMark.Line << 32) | (uint)keyMark.Col;
                firstPrimaryKey = StepMappingKey.WaitAll;
                firstPrimaryMark = keyMark;
                stepForm = StepSchema.FormId.WaitAll;
                hasPrimary = true;

                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    if (!TryParseNullaryStepValue(ref reader, out var waErr, out var waMark))
                    {
                        AddError(ref diagnostics, $"{stepPrefix} wait-all must be null, empty, or true", waMark);
                    }
                }

                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<StepMappingKeyTable>(keyUtf8, out var stepKeyOrd))
            {
                var keyLen = keyUtf8.Length;
                reader.Read();
                if (!TrySetBit(ref seen, stepKeyOrd))
                {
                    var dupName = StepMappingKeyTable.Utf8Key(stepKeyOrd);
                    var keyName = Encoding.UTF8.GetString(dupName);
                    var prevMark = stepKeyFirstMark[stepKeyOrd];
                    var prevLine = (int)(prevMark >> 32);
                    var prevCol = (int)(prevMark & 0xFFFFFFFF);
                    AddError(
                        ref diagnostics,
                        $"{stepPrefix} key \"{keyName}\" is duplicated in step. previously defined at line:{prevLine},col:{prevCol}",
                        keyMark,
                        BuildStepDuplicateKeyHelp(dupName));
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                if (stepKeyOrd < stepKeyFirstMark.Length)
                {
                    stepKeyFirstMark[stepKeyOrd] = ((long)keyMark.Line << 32) | (uint)keyMark.Col;
                }

                var stepKey = (StepMappingKey)stepKeyOrd;
                if (IsPrimaryStepKey(stepKey))
                {
                    var newForm = FormForPrimaryKey(stepKey);
                    if (stepForm is StepSchema.FormId existingForm && existingForm != newForm)
                    {
                        ReportPrimaryConflict(ref diagnostics, stepPrefix, firstPrimaryKey, firstPrimaryMark, newForm);
                    }

                    firstPrimaryKey = stepKey;
                    firstPrimaryMark = keyMark;
                    stepForm = newForm;
                    hasPrimary = true;
                }

                switch (stepKey)
                {
                    case StepMappingKey.Background:
                        backgroundKeyMark = keyMark;
                        if (!reader.End)
                        {
                            backgroundNode = ParseBool(ref reader, arena, out var bgErr, out var bgMark);
                            if (bgErr) AddError(ref diagnostics, $"{stepPrefix} background must be bool", bgMark);
                        }

                        break;

                    case StepMappingKey.Run:
                        if (!reader.End)
                        {
                            runNode = ParseStringAndValidateExpression(
                                ref reader, arena, ref diagnostics,
                                ExpressionValidationContext.StepRun,
                                out var runErr,
                                out var runMark,
                                parseWholeValueIfNoEmbedded: false);
                            if (runErr) AddError(ref diagnostics, $"{stepPrefix} run must be string", runMark);
                        }

                        break;

                    case StepMappingKey.Uses:
                        usesKeyRange = BuildScalarLocation(keyMark, keyLen);
                        if (!reader.End)
                        {
                            usesNode = ParseString(ref reader, arena, out var usesErr, out var usesMark);
                            if (usesErr) AddError(ref diagnostics, $"{stepPrefix} uses must be string", usesMark);
                        }

                        break;

                    case StepMappingKey.Wait:
                        if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                        {
                            waitTargets = ParseStringOrStringSequence(
                                ref reader, arena, ref diagnostics,
                                out var waitErr,
                                out var waitMark,
                                allowEmpty: false,
                                allowElemEmpty: false);
                            if (waitErr)
                            {
                                AddError(ref diagnostics, $"{stepPrefix} wait must be string or non-empty sequence of strings", waitMark);
                            }
                            else if (waitTargets.Count == 0)
                            {
                                AddError(ref diagnostics, $"{stepPrefix} wait must be string or non-empty sequence of strings", keyMark);
                            }
                        }
                        else if (!reader.End)
                        {
                            AddError(ref diagnostics, $"{stepPrefix} wait must be string or non-empty sequence of strings", keyMark);
                        }

                        break;

                    case StepMappingKey.WaitAll:
                        if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                        {
                            if (!TryParseNullaryStepValue(ref reader, out var waErr, out var waMark))
                            {
                                AddError(ref diagnostics, $"{stepPrefix} wait-all must be null, empty, or true", waMark);
                            }
                        }

                        break;

                    case StepMappingKey.Cancel:
                        if (!reader.End)
                        {
                            cancelTarget = ParseString(ref reader, arena, out var cancelErr, out var cancelMark, allowEmpty: false);
                            if (cancelErr)
                            {
                                var cancelMsg = cancelTarget.HasValue
                                    ? $"{stepPrefix} cancel target should not be empty"
                                    : $"{stepPrefix} cancel must be string";
                                AddError(ref diagnostics, cancelMsg, cancelMark);
                            }
                        }

                        break;

                    case StepMappingKey.Parallel:
                        parallelKeyMark = keyMark;
                        if (reader.CurrentKind != YamlEventKind.SequenceStart)
                        {
                            AddError(ref diagnostics, $"{stepPrefix} parallel must be non-empty sequence of steps", reader.CurrentStart);
                            if (!reader.End) reader.SkipCurrentNode();
                        }
                        else
                        {
                            parallelSteps = ParseSteps(ref reader, arena, ref diagnostics, source, $"{stepPrefix}.parallel");
                            if (parallelSteps.Count == 0)
                            {
                                AddError(ref diagnostics, $"{stepPrefix} parallel must be non-empty sequence of steps", parallelKeyMark);
                            }
                        }

                        break;

                    case StepMappingKey.Name:
                        if (!reader.End)
                        {
                            nameNode = ParseString(ref reader, arena, out var nameErr, out var nameMark);
                            if (nameErr) AddError(ref diagnostics, $"{stepPrefix} name must be string", nameMark);
                        }

                        break;

                    case StepMappingKey.Id:
                        if (!reader.End)
                        {
                            idNode = ParseString(ref reader, arena, out var idErr, out var idMark);
                            if (idErr)
                            {
                                var idMsg = idNode.HasValue
                                    ? $"{stepPrefix} step id should not be empty"
                                    : $"{stepPrefix} id must be string";
                                AddError(ref diagnostics, idMsg, idMark);
                            }
                        }

                        break;

                    case StepMappingKey.If:
                        ifKeyMark = keyMark;
                        if (!reader.End)
                        {
                            ifNode = ParseExpression(
                                ref reader, arena, ref diagnostics,
                                ExpressionValidationContext.StepIf,
                                out var ifErr,
                                out var ifMark);
                            if (ifErr) AddError(ref diagnostics, $"{stepPrefix} if must be string", ifMark);
                        }

                        break;

                    case StepMappingKey.With:
                        withKeyMark = keyMark;
                        if (!reader.End)
                        {
                            withInputs = ParseStepWithInputsNode(
                                ref reader, arena, ref diagnostics,
                                source,
                                stepPrefix,
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
                            if (shellErr) AddError(ref diagnostics, $"{stepPrefix} shell must be string", shellMark);
                        }

                        break;

                    case StepMappingKey.WorkingDirectory:
                        wdKeyMark = keyMark;
                        if (!reader.End)
                        {
                            workingDirectoryNode = ParseStringAndValidateExpression(
                                ref reader, arena, ref diagnostics,
                                ExpressionValidationContext.StepWorkingDirectory,
                                out var wdErr,
                                out var wdMark,
                                parseWholeValueIfNoEmbedded: false);
                            if (wdErr) AddError(ref diagnostics, $"{stepPrefix} working-directory must be string", wdMark);
                        }

                        break;

                    case StepMappingKey.TimeoutMinutes:
                        if (!reader.End)
                        {
                            timeoutMinutesNode = ParseFloatOrExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.StepTimeoutMinutes, out var tmErr, out var tmMark);
                            if (tmErr) AddError(ref diagnostics, $"{stepPrefix} timeout-minutes must be number or expression", tmMark);
                            if (timeoutMinutesNode.HasValue && !arena.GetFloatExpression(timeoutMinutesNode).HasValue && arena.GetFloatValue(timeoutMinutesNode) <= 0)
                            {
                                AddError(ref diagnostics, $"{stepPrefix} timeout-minutes must be greater than 0", keyMark);
                            }
                        }

                        break;

                    case StepMappingKey.ContinueOnError:
                        if (!reader.End)
                        {
                            continueOnErrorNode = ParseBoolOrExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.StepContinueOnError, out var coeErr, out var coeMark);
                            if (coeErr) AddError(ref diagnostics, $"{stepPrefix} continue-on-error must be bool or expression", coeMark);
                        }

                        break;

                    case StepMappingKey.Env:
                        if (!reader.End)
                        {
                            envNode = ParseEnvNode(
                                ref reader, arena, ref diagnostics,
                                source,
                                $"{stepPrefix} env must be object",
                                ExpressionValidationContext.StepEnv,
                                $"{stepPrefix} env");
                        }

                        break;
                }

                continue;
            }

            var isKnownButNotHandled = IsKnownStepKey(keyUtf8);
            var unknownKey = isKnownButNotHandled ? null : Encoding.UTF8.GetString(keyUtf8);
            var unknownKeySlice = isKnownButNotHandled ? default : reader.GetScalarSlice();

            reader.Read();

            if (isKnownButNotHandled)
            {
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            if (stepForm is StepSchema.FormId form)
            {
                AddStepUnexpectedKeyError(ref diagnostics, stepPrefix, unknownKey!, unknownKeySlice, keyMark, form);
            }
            else if (deferredUnknownKey is null)
            {
                deferredUnknownKey = unknownKey;
                deferredUnknownMark = keyMark;
                deferredUnknownKeySlice = unknownKeySlice;
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

        if (deferredUnknownKey is not null && stepForm is StepSchema.FormId deferredForm)
        {
            AddStepUnexpectedKeyError(ref diagnostics, stepPrefix, deferredUnknownKey, deferredUnknownKeySlice, deferredUnknownMark, deferredForm);
        }

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
            AddError(ref diagnostics, $"{stepPrefix} element of \"steps\" section should not be empty. please remove this section if it's unnecessary", emptyMark);
        }

        if (!hasPrimary)
        {
            AddError(ref diagnostics, $"{stepPrefix} {missingPrimaryMessage}", hasAnyKey ? stepMark : emptyMark);
        }

        if (stepForm is StepSchema.FormId resolvedForm)
        {
            switch (resolvedForm)
            {
                case StepSchema.FormId.Run:
                    ReportDisallowedStepKey(ref diagnostics, stepPrefix, "with", withKeyMark, resolvedForm);
                    break;
                case StepSchema.FormId.Uses:
                    ReportDisallowedStepKey(ref diagnostics, stepPrefix, "shell", shellKeyMark, resolvedForm);
                    ReportDisallowedStepKey(ref diagnostics, stepPrefix, "working-directory", wdKeyMark, resolvedForm);
                    break;
                default:
                    ReportDisallowedStepKey(ref diagnostics, stepPrefix, "shell", shellKeyMark, resolvedForm);
                    ReportDisallowedStepKey(ref diagnostics, stepPrefix, "working-directory", wdKeyMark, resolvedForm);
                    ReportDisallowedStepKey(ref diagnostics, stepPrefix, "with", withKeyMark, resolvedForm);
                    ReportDisallowedStepKey(ref diagnostics, stepPrefix, "background", backgroundKeyMark, resolvedForm);
                    break;
            }
        }

        StepExec exec;
        TextRange execRange = default;
        switch (stepForm)
        {
            case StepSchema.FormId.Run:
            {
                var execRun = arena.AllocExecRun();
                execRun.Kind = StepExecKind.Run;
                execRun.Run = runNode.HasValue ? runNode : arena.AddString(default, false, default);
                execRun.Shell = shellNode;
                execRun.WorkingDirectory = workingDirectoryNode;
                execRun.Range = runNode.HasValue ? arena.GetStringRange(runNode) : default;
                execRange = execRun.Range;
                exec = execRun;
                break;
            }
            case StepSchema.FormId.Uses:
            {
                var execAction = arena.AllocExecAction();
                execAction.Kind = StepExecKind.Action;
                execAction.Uses = usesNode.HasValue ? usesNode : arena.AddString(default, false, default);
                execAction.UsesKeyRange = usesKeyRange;
                execAction.Inputs = withInputs;
                execAction.Entrypoint = dockerEntrypoint;
                execAction.Args = dockerArgs;
                execAction.Range = usesNode.HasValue ? arena.GetStringRange(usesNode) : default;
                execRange = execAction.Range;
                exec = execAction;
                break;
            }
            case StepSchema.FormId.Wait:
            {
                var execWait = arena.AllocExecWait();
                execWait.Kind = StepExecKind.Wait;
                execWait.Targets = waitTargets;
                execWait.Range = waitTargets.Count > 0 ? arena.GetStringRange(waitTargets[0]) : default;
                execRange = execWait.Range;
                exec = execWait;
                break;
            }
            case StepSchema.FormId.WaitAll:
            {
                var execWaitAll = arena.AllocExecWaitAll();
                execWaitAll.Kind = StepExecKind.WaitAll;
                execWaitAll.Range = firstPrimaryMark != default ? BuildScalarLocation(firstPrimaryMark, 8) : default;
                execRange = execWaitAll.Range;
                exec = execWaitAll;
                break;
            }
            case StepSchema.FormId.Cancel:
            {
                var execCancel = arena.AllocExecCancel();
                execCancel.Kind = StepExecKind.Cancel;
                execCancel.Target = cancelTarget.HasValue ? cancelTarget : arena.AddString(default, false, default);
                execCancel.Range = cancelTarget.HasValue ? arena.GetStringRange(cancelTarget) : default;
                execRange = execCancel.Range;
                exec = execCancel;
                break;
            }
            case StepSchema.FormId.Parallel:
            {
                var execParallel = arena.AllocExecParallel();
                execParallel.Kind = StepExecKind.Parallel;
                execParallel.Steps = parallelSteps;
                execParallel.Range = parallelKeyMark != default ? BuildScalarLocation(parallelKeyMark, 8) : default;
                execRange = execParallel.Range;
                exec = execParallel;
                break;
            }
            default:
            {
                var execRun = arena.AllocExecRun();
                execRun.Kind = StepExecKind.Run;
                exec = execRun;
                break;
            }
        }

        var step = arena.AllocStep();
        step.Id = idNode;
        step.If = ifNode;
        step.IfKeyRange = ifNode.HasValue ? BuildScalarLocation(ifKeyMark, 2) : null;
        step.Name = nameNode;
        step.Background = backgroundNode;
        step.Exec = exec;
        step.Env = envNode;
        step.ContinueOnError = continueOnErrorNode;
        step.TimeoutMinutes = timeoutMinutesNode;
        step.Range = execRange;
        return step;
    }

    private static bool TryParseNullaryStepValue<TReader>(ref TReader reader, out bool needsError, out TextPosition errorMark)
        where TReader : IYamlStreamReader, allows ref struct
    {
        needsError = false;
        errorMark = default;

        if (reader.End)
        {
            return true;
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            return true;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            needsError = true;
            errorMark = reader.CurrentStart;
            reader.SkipCurrentNode();
            return false;
        }

        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        if (tag == ScalarTag.Null || valueUtf8.Length == 0)
        {
            reader.Read();
            return true;
        }

        if (TryParseBool(valueUtf8, tag, out var value) && value)
        {
            reader.Read();
            return true;
        }

        needsError = true;
        errorMark = reader.CurrentStart;
        reader.Read();
        return false;
    }

    private static SliceMap<StringNodeId>? ParseStepWithInputsNode<TReader>(
        ref TReader reader,
        AstArena arena,
        ref PooledBuffer<Diagnostic> diagnostics,
        ReadOnlySpan<byte> source,
        string stepPrefix,
        out StringNodeId entrypoint,
        out StringNodeId args)
        where TReader : IYamlStreamReader, allows ref struct
    {
        entrypoint = default;
        args = default;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, "\"with\" section is scalar node but mapping node is expected", reader.CurrentStart);
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
                    AddError(ref diagnostics, $"{stepPrefix} with must be object", reader.CurrentStart);
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
                    ref diagnostics,
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
                    ref reader, arena, ref diagnostics,
                    ExpressionValidationContext.StepWith,
                    out var withErr,
                    out var withMark,
                    parseWholeValueIfNoEmbedded: false);
                if (withErr) AddError(ref diagnostics, $"{stepPrefix} with.{Encoding.UTF8.GetString(keyUtf8)} must be string", withMark);

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

            var (withEntries, withCount) = map.DetachArray();
            arena.RegisterSliceMapBuffer(withEntries);
            return new SliceMap<StringNodeId>(withEntries, withCount, caseSensitive: false);
        }
        finally { map.Dispose(); }
    }
}
