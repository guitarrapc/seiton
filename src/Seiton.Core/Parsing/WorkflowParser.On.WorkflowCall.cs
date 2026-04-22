// on.workflow_call — inputs, secrets, outputs for reusable workflow triggers.

using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static WorkflowCallEvent ParseWorkflowCallEvent<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowCallEvent { EventName = nameNode, Inputs = null, Secrets = null, Outputs = null, Range = arena.GetStringRange(nameNode) };
        }

        WorkflowCallEventInput[]? inputs = null;
        SliceMap<WorkflowCallEventSecret>? secrets = null;
        SliceMap<WorkflowCallEventOutput>? outputs = null;
        ulong seen = 0;

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_call option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "on.workflow_call"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("inputs"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "on.workflow_call contains duplicate key: inputs", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                inputs = ParseWorkflowCallInputs(ref reader, arena, diagnostics, source);
                continue;
            }

            if (keyUtf8.SequenceEqual("secrets"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.workflow_call contains duplicate key: secrets", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                secrets = ParseWorkflowCallSecrets(ref reader, arena, diagnostics, source);
                continue;
            }

            if (keyUtf8.SequenceEqual("outputs"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, "on.workflow_call contains duplicate key: outputs", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                outputs = ParseWorkflowCallOutputs(ref reader, arena, diagnostics, source);
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"on.workflow_call does not support option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new WorkflowCallEvent
        {
            EventName = nameNode,
            Inputs = inputs,
            Secrets = secrets,
            Outputs = outputs,
            Range = arena.GetStringRange(nameNode),
        };
    }

    private static WorkflowCallEventInput[]? ParseWorkflowCallInputs<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call.inputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var list = new PooledBuffer<WorkflowCallEventInput>(4);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            reader.Read(); // consume MappingStart
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, "on.workflow_call.inputs key must be scalar", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }
                    continue;
                }

                var idMark = reader.CurrentStart;
                var idSlice = reader.GetScalarSlice();
                var idUtf8 = reader.GetScalarUtf8();
                if (!TryRegisterDynamicKey(
                    source,
                    idUtf8,
                    idSlice.Offset,
                    idSlice.Length,
                    idMark,
                    diagnostics,
                    keyStore,
                    ref keyCount,
                    caseSensitive: false,
                    "on.workflow_call.inputs"))
                {
                    reader.Read();
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var id = Utf8String.FromLowerAscii(idUtf8);
                var nameNode = arena.AddString(idSlice, reader.IsScalarQuoted(), BuildScalarLocation(idMark, idUtf8.Length));
                var idText = Encoding.UTF8.GetString(idUtf8);
                reader.Read();

                list.Add(ParseWorkflowCallInput(ref reader, arena, diagnostics, nameNode, id, idText));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            return list.ToArray();
        }
        finally { list.Dispose(); }
    }

    private static WorkflowCallEventInput ParseWorkflowCallInput<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, StringNodeId nameNode, Utf8String id, string idText)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNodeId description = default;
        BoolNodeId required = default;
        StringNodeId defaultValue = default;
        var type = WorkflowCallInputType.Invalid;
        var hasType = false;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call input must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowCallEventInput { Name = nameNode, Id = id, Description = description, Required = required, Default = defaultValue, Type = type, Range = arena.GetStringRange(nameNode) };
        }

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_call input option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "on.workflow_call input"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("description"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "on.workflow_call input contains duplicate key: description", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                description = ParseString(ref reader, arena, diagnostics, "on.workflow_call input description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("required"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.workflow_call input contains duplicate key: required", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                required = ParseBoolNode(ref reader, arena, diagnostics, "on.workflow_call input required must be bool");
                continue;
            }

            if (keyUtf8.SequenceEqual("default"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, "on.workflow_call input contains duplicate key: default", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                defaultValue = ParseString(ref reader, arena, diagnostics, "on.workflow_call input default must be scalar", allowEmpty: true);
                continue;
            }

            if (keyUtf8.SequenceEqual("type"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 3)) { AddError(diagnostics, "on.workflow_call input contains duplicate key: type", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                type = ParseWorkflowCallInputType(ref reader, arena, diagnostics);
                hasType = true;
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected on.workflow_call input option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        // spec ・ゑｽｧ11.15 / ・ゑｽｧ12: workflow_call input requires `type`
        if (!hasType)
        {
            AddError(
                diagnostics,
                $"on.workflow_call.inputs.{idText}.type is required",
                new TextPosition(arena.GetStringRange(nameNode).Start, arena.GetStringRange(nameNode).StartLine, arena.GetStringRange(nameNode).StartColumn));
        }

        return new WorkflowCallEventInput
        {
            Name = nameNode,
            Id = id,
            Description = description,
            Required = required,
            Default = defaultValue,
            Type = type,
            Range = arena.GetStringRange(nameNode),
        };
    }

    private static WorkflowCallInputType ParseWorkflowCallInputType<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, "on.workflow_call input type must be scalar", reader.CurrentStart);
            reader.SkipCurrentNode();
            return WorkflowCallInputType.Invalid;
        }

        var valueUtf8 = reader.GetScalarUtf8();
        var type = valueUtf8.SequenceEqual("boolean"u8) ? WorkflowCallInputType.Boolean
            : valueUtf8.SequenceEqual("number"u8) ? WorkflowCallInputType.Number
            : valueUtf8.SequenceEqual("string"u8) ? WorkflowCallInputType.String
            : WorkflowCallInputType.Invalid;
        if (type == WorkflowCallInputType.Invalid)
        {
            AddError(diagnostics, "on.workflow_call input type must be one of boolean, number, string", reader.CurrentStart);
        }

        reader.Read();
        return type;
    }

    private static SliceMap<WorkflowCallEventSecret>? ParseWorkflowCallSecrets<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call.secrets must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var map = new PooledBuffer<SliceMap<WorkflowCallEventSecret>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            reader.Read(); // consume MappingStart
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, "on.workflow_call.secrets key must be scalar", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }
                    continue;
                }

                var idMark = reader.CurrentStart;
                var idSlice = reader.GetScalarSlice();
                var idUtf8 = reader.GetScalarUtf8();
                if (!TryRegisterDynamicKey(
                    source,
                    idUtf8,
                    idSlice.Offset,
                    idSlice.Length,
                    idMark,
                    diagnostics,
                    keyStore,
                    ref keyCount,
                    caseSensitive: false,
                    "on.workflow_call.secrets"))
                {
                    reader.Read();
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var nameNode = arena.AddString(idSlice, reader.IsScalarQuoted(), BuildScalarLocation(idMark, idUtf8.Length));
                reader.Read();

                map.Add(new SliceMap<WorkflowCallEventSecret>.Entry(idSlice, ParseWorkflowCallSecret(ref reader, arena, diagnostics, nameNode)));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            return new SliceMap<WorkflowCallEventSecret>(map.ToArray(), caseSensitive: false);
        }
        finally { map.Dispose(); }
    }

    private static WorkflowCallEventSecret ParseWorkflowCallSecret<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNodeId description = default;
        BoolNodeId required = default;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call secret must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowCallEventSecret { Name = nameNode, Description = description, Required = required, Range = arena.GetStringRange(nameNode) };
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_call secret option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "on.workflow_call secret"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("description"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "on.workflow_call secret contains duplicate key: description", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                description = ParseString(ref reader, arena, diagnostics, "on.workflow_call secret description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("required"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.workflow_call secret contains duplicate key: required", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                required = ParseBoolNode(ref reader, arena, diagnostics, "on.workflow_call secret required must be bool");
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected on.workflow_call secret option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new WorkflowCallEventSecret
        {
            Name = nameNode,
            Description = description,
            Required = required,
            Range = arena.GetStringRange(nameNode),
        };
    }

    private static SliceMap<WorkflowCallEventOutput>? ParseWorkflowCallOutputs<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call.outputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var map = new PooledBuffer<SliceMap<WorkflowCallEventOutput>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, "on.workflow_call.outputs key must be scalar", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }
                    continue;
                }

                var idMark = reader.CurrentStart;
                var idSlice = reader.GetScalarSlice();
                var idUtf8 = reader.GetScalarUtf8();
                if (!TryRegisterDynamicKey(
                    source,
                    idUtf8,
                    idSlice.Offset,
                    idSlice.Length,
                    idMark,
                    diagnostics,
                    keyStore,
                    ref keyCount,
                    caseSensitive: false,
                    "on.workflow_call.outputs"))
                {
                    reader.Read();
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var nameNode = arena.AddString(idSlice, reader.IsScalarQuoted(), BuildScalarLocation(idMark, idUtf8.Length));
                var idText = Encoding.UTF8.GetString(idUtf8);
                reader.Read();

                map.Add(new SliceMap<WorkflowCallEventOutput>.Entry(idSlice, ParseWorkflowCallOutput(ref reader, arena, diagnostics, nameNode, idText)));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            return new SliceMap<WorkflowCallEventOutput>(map.ToArray(), caseSensitive: false);
        }
        finally { map.Dispose(); }
    }

    private static WorkflowCallEventOutput ParseWorkflowCallOutput<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, StringNodeId nameNode, string idText)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNodeId description = default;
        StringNodeId value = default;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call output must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowCallEventOutput { Name = nameNode, Description = description, Value = value, Range = arena.GetStringRange(nameNode) };
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_call output option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "on.workflow_call output"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("description"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "on.workflow_call output contains duplicate key: description", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                description = ParseString(ref reader, arena, diagnostics, "on.workflow_call output description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("value"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.workflow_call output contains duplicate key: value", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                value = ParseStringAndValidateExpression(
                    ref reader, arena, diagnostics,
                    ExpressionValidationContext.WorkflowCallOutput,
                    "on.workflow_call output value must be scalar",
                    parseWholeValueIfNoEmbedded: false);
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected on.workflow_call output option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        // spec ・ゑｽｧ11.17 / ・ゑｽｧ12: workflow_call output requires `value`
        if (!value.HasValue)
        {
            AddError(
                diagnostics,
                $"on.workflow_call.outputs.{idText}.value is required",
                new TextPosition(arena.GetStringRange(nameNode).Start, arena.GetStringRange(nameNode).StartLine, arena.GetStringRange(nameNode).StartColumn));
        }

        return new WorkflowCallEventOutput
        {
            Name = nameNode,
            Description = description,
            Value = value,
            Range = arena.GetStringRange(nameNode),
        };
    }
}
