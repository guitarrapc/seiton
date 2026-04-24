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
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var hasRun = false;
        var hasUses = false;
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

            if (Utf8MappingDispatch.TryMatchFirstOrdered<StepMappingKeyTable>(keyUtf8, out var stepKeyOrd))
            {
                var keyLen = keyUtf8.Length;
                reader.Read();
                switch ((StepMappingKey)stepKeyOrd)
                {
                    case StepMappingKey.Run:
                        hasRun = true;
                        if (!reader.End)
                        {
                            runNode = ParseStringAndValidateExpression(
                                ref reader, arena, diagnostics,
                                ExpressionValidationContext.Step,
                                out var runErr,
                                out var runMark,
                                parseWholeValueIfNoEmbedded: false);
                            if (runErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] run must be scalar", runMark);
                        }

                        break;

                    case StepMappingKey.Uses:
                        usesKeyRange = BuildScalarLocation(keyMark, keyLen);
                        hasUses = true;
                        if (!reader.End)
                        {
                            usesNode = ParseString(ref reader, arena, out var usesErr, out var usesMark);
                            if (usesErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] uses must be scalar", usesMark);
                        }

                        break;

                    case StepMappingKey.Name:
                        if (!reader.End)
                        {
                            nameNode = ParseString(ref reader, arena, out var nameErr, out var nameMark);
                            if (nameErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] name must be scalar", nameMark);
                        }

                        break;

                    case StepMappingKey.Id:
                        if (!reader.End)
                        {
                            idNode = ParseString(ref reader, arena, out var idErr, out var idMark);
                            if (idErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] id must be scalar", idMark);
                        }

                        break;

                    case StepMappingKey.If:
                        if (!reader.End)
                        {
                            ifNode = ParseExpression(
                                ref reader, arena, diagnostics,
                                ExpressionValidationContext.Step,
                                out var ifErr,
                                out var ifMark);
                            if (ifErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] if must be scalar", ifMark);
                        }

                        break;

                    case StepMappingKey.With:
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
                        if (!reader.End)
                        {
                            shellNode = ParseString(ref reader, arena, out var shellErr, out var shellMark);
                            if (shellErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] shell must be scalar", shellMark);
                        }

                        break;

                    case StepMappingKey.WorkingDirectory:
                        if (!reader.End)
                        {
                            workingDirectoryNode = ParseStringAndValidateExpression(
                                ref reader, arena, diagnostics,
                                ExpressionValidationContext.Step,
                                out var wdErr,
                                out var wdMark,
                                parseWholeValueIfNoEmbedded: false);
                            if (wdErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] working-directory must be scalar", wdMark);
                        }

                        break;

                    case StepMappingKey.TimeoutMinutes:
                        if (!reader.End)
                        {
                            timeoutMinutesNode = ParseFloatOrExpression(ref reader, arena, diagnostics, ExpressionValidationContext.Step, out var tmErr, out var tmMark);
                            if (tmErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] timeout-minutes must be number or expression", tmMark);
                            if (timeoutMinutesNode.HasValue && !arena.GetFloatExpression(timeoutMinutesNode).HasValue && arena.GetFloatValue(timeoutMinutesNode) <= 0)
                            {
                                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] timeout-minutes must be greater than 0", keyMark);
                            }
                        }

                        break;

                    case StepMappingKey.ContinueOnError:
                        if (!reader.End)
                        {
                            continueOnErrorNode = ParseBoolOrExpression(ref reader, arena, diagnostics, ExpressionValidationContext.Step, out var coeErr, out var coeMark);
                            if (coeErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] continue-on-error must be bool or expression", coeMark);
                        }

                        break;

                    case StepMappingKey.Env:
                        if (!reader.End)
                        {
                            envNode = ParseEnvNode(
                                ref reader, arena, diagnostics,
                                source,
                                $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] env must be mapping",
                                ExpressionValidationContext.Step);
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

            AddError(diagnostics, $"unexpected step key '{unknownKey}' in job '{DecodeUtf8(source, jobId)}' step[{stepIndex}]", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        // spec §3.12: a step resolves to either ExecRun or ExecAction, never both
        if (hasRun && hasUses)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] cannot have both run and uses", reader.CurrentStart);
        }

        // spec §3.12: a step must choose one execution form: `run` or `uses`
        if (!hasRun && !hasUses)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] requires run or uses", reader.CurrentStart);
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
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] with must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var map = new PooledBuffer<SliceMap<StringNodeId>.Entry>(8);
        try
        {
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] with must be mapping", reader.CurrentStart);
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

                reader.Read();
                if (reader.End)
                {
                    break;
                }

                var value = ParseStringAndValidateExpression(
                    ref reader, arena, diagnostics,
                    ExpressionValidationContext.Step,
                    out var withErr,
                    out var withMark,
                    parseWholeValueIfNoEmbedded: false);
                if (withErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] with.{Encoding.UTF8.GetString(keyUtf8)} must be scalar", withMark);

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
