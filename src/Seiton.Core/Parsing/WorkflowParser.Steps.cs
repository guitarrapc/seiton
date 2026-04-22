using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static Step[] ParseSteps<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
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
            var step = ParseStep(ref reader, diagnostics, source, jobId, stepIndex);
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

    private static Step? ParseStep<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, int stepIndex)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var hasRun = false;
        var hasUses = false;
        StringNode? idNode = null;
        StringNode? ifNode = null;
        StringNode? nameNode = null;
        Env? envNode = null;
        BoolNode? continueOnErrorNode = null;
        FloatNode? timeoutMinutesNode = null;
        StringNode? runNode = null;
        StringNode? usesNode = null;
        TextRange? usesKeyRange = null;
        StringNode? shellNode = null;
        StringNode? workingDirectoryNode = null;
        SliceMap<StringNode>? withInputs = null;
        StringNode? dockerEntrypoint = null;
        StringNode? dockerArgs = null;

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

            if (keyUtf8.SequenceEqual("run"u8))
            {
                reader.Read();
                hasRun = true;
                if (!reader.End)
                {
                    runNode = ParseStringAndValidateExpression(
                        ref reader,
                        diagnostics,
                        ExpressionValidationContext.Step,
                        out var runErr,
                        out var runMark,
                        parseWholeValueIfNoEmbedded: false);
                    if (runErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] run must be scalar", runMark);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("uses"u8))
            {
                usesKeyRange = BuildScalarLocation(keyMark, keyUtf8.Length);
                reader.Read();
                hasUses = true;
                if (!reader.End)
                {
                    usesNode = ParseString(ref reader, out var usesErr, out var usesMark);
                    if (usesErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] uses must be scalar", usesMark);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("name"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    nameNode = ParseString(ref reader, out var nameErr, out var nameMark);
                    if (nameErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] name must be scalar", nameMark);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("id"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    idNode = ParseString(ref reader, out var idErr, out var idMark);
                    if (idErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] id must be scalar", idMark);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("if"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    ifNode = ParseExpression(
                        ref reader,
                        diagnostics,
                        ExpressionValidationContext.Step,
                        out var ifErr,
                        out var ifMark);
                    if (ifErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] if must be scalar", ifMark);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("with"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    withInputs = ParseStepWithInputsNode(
                        ref reader,
                        diagnostics,
                        source,
                        jobId,
                        stepIndex,
                        out var entrypoint,
                        out var args);
                    dockerEntrypoint = entrypoint;
                    dockerArgs = args;
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("shell"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    shellNode = ParseString(ref reader, out var shellErr, out var shellMark);
                    if (shellErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] shell must be scalar", shellMark);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("working-directory"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    workingDirectoryNode = ParseStringAndValidateExpression(
                        ref reader,
                        diagnostics,
                        ExpressionValidationContext.Step,
                        out var wdErr,
                        out var wdMark,
                        parseWholeValueIfNoEmbedded: false);
                    if (wdErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] working-directory must be scalar", wdMark);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("timeout-minutes"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    timeoutMinutesNode = ParseFloat(ref reader, out var tmErr, out var tmMark);
                    if (tmErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] timeout-minutes must be number", tmMark);
                    if (timeoutMinutesNode is not null && timeoutMinutesNode.Value <= 0)
                    {
                        AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] timeout-minutes must be greater than 0", keyMark);
                    }
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("continue-on-error"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    continueOnErrorNode = ParseBoolOrExpression(ref reader, diagnostics, ExpressionValidationContext.Step, out var coeErr, out var coeMark);
                    if (coeErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] continue-on-error must be bool or expression", coeMark);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read();
                if (!reader.End)
                {
                    if (reader.CurrentKind != YamlEventKind.MappingStart)
                    {
                        AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] env must be mapping", reader.CurrentStart);
                        reader.SkipCurrentNode();
                    }
                    else
                    {
                        envNode = ParseEnvNode(
                            ref reader,
                            diagnostics,
                            source,
                            $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] env must be mapping",
                            ExpressionValidationContext.Step);
                    }
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
                Run = runNode ?? new StringNode { Value = default, Quoted = false, Range = default },
                Shell = shellNode,
                WorkingDirectory = workingDirectoryNode,
                Range = runNode?.Range ?? default,
            };
        }
        else
        {
            exec = new ExecAction
            {
                Kind = StepExecKind.Action,
                Uses = usesNode ?? new StringNode { Value = default, Quoted = false, Range = default },
                UsesKeyRange = usesKeyRange,
                Inputs = withInputs,
                Entrypoint = dockerEntrypoint,
                Args = dockerArgs,
                Range = usesNode?.Range ?? default,
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

    private static SliceMap<StringNode>? ParseStepWithInputsNode<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, int stepIndex, out StringNode? entrypoint, out StringNode? args)
        where TReader : IYamlStreamReader, allows ref struct
    {
        entrypoint = null;
        args = null;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] with must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new PooledBuffer<SliceMap<StringNode>.Entry>(8);
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
                ref reader,
                diagnostics,
                ExpressionValidationContext.Step,
                out var withErr,
                out var withMark,
                parseWholeValueIfNoEmbedded: false);
            if (withErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' step[{stepIndex}] with.{Encoding.UTF8.GetString(keyUtf8)} must be scalar", withMark);

            if (value is null)
            {
                continue;
            }

            map.Add(new SliceMap<StringNode>.Entry(keySlice, value));
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

        return new SliceMap<StringNode>(map.ToArray(), caseSensitive: false);
        }
        finally { map.Dispose(); }
    }

}
