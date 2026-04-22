// on.workflow_dispatch — inputs and dispatch input field parsing.

using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static WorkflowDispatchEvent ParseWorkflowDispatchEvent<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_dispatch must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowDispatchEvent { EventName = nameNode, Inputs = null, Range = arena.GetStringRange(nameNode) };
        }

        SliceMap<DispatchInput>? inputs = null;
        ulong seen = 0;
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_dispatch option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "on.workflow_dispatch"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("inputs"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "on.workflow_dispatch contains duplicate key: inputs", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                inputs = ParseWorkflowDispatchInputs(ref reader, arena, diagnostics, source);
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"on.workflow_dispatch does not support option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new WorkflowDispatchEvent { EventName = nameNode, Inputs = inputs, Range = arena.GetStringRange(nameNode) };
    }

    private static SliceMap<DispatchInput>? ParseWorkflowDispatchInputs<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_dispatch.inputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var map = new PooledBuffer<SliceMap<DispatchInput>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            reader.Read(); // consume MappingStart
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, "on.workflow_dispatch.inputs key must be scalar", reader.CurrentStart);
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
                    "on.workflow_dispatch.inputs"))
                {
                    reader.Read();
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var idRange = BuildScalarLocation(idMark, idUtf8.Length);
                var nameNode = arena.AddString(idSlice, reader.IsScalarQuoted(), idRange);
                reader.Read(); // consume input id

                var input = ParseWorkflowDispatchInput(ref reader, arena, diagnostics, nameNode);
                map.Add(new SliceMap<DispatchInput>.Entry(idSlice, input));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            return new SliceMap<DispatchInput>(map.ToArray(), caseSensitive: false);
        }
        finally { map.Dispose(); }
    }

    private static DispatchInput ParseWorkflowDispatchInput<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNodeId description = default;
        BoolNodeId required = default;
        StringNodeId defaultValue = default;
        DispatchInputType type = DispatchInputType.None;
        StringNodeId[]? options = null;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_dispatch input must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new DispatchInput { Name = nameNode, Description = description, Required = required, Default = defaultValue, Type = type, Options = options, Range = arena.GetStringRange(nameNode) };
        }

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.workflow_dispatch input option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "on.workflow_dispatch input"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("description"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "on.workflow_dispatch input contains duplicate key: description", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                description = ParseString(ref reader, arena, diagnostics, "on.workflow_dispatch input description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("required"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.workflow_dispatch input contains duplicate key: required", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                required = ParseBoolNode(ref reader, arena, diagnostics, "on.workflow_dispatch input required must be bool");
                continue;
            }

            if (keyUtf8.SequenceEqual("default"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, "on.workflow_dispatch input contains duplicate key: default", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                defaultValue = ParseString(ref reader, arena, diagnostics, "on.workflow_dispatch input default must be scalar", allowEmpty: true);
                continue;
            }

            if (keyUtf8.SequenceEqual("type"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 3)) { AddError(diagnostics, "on.workflow_dispatch input contains duplicate key: type", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                type = ParseDispatchInputType(ref reader, arena, diagnostics);
                continue;
            }

            if (keyUtf8.SequenceEqual("options"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 4)) { AddError(diagnostics, "on.workflow_dispatch input contains duplicate key: options", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                // allowElemEmpty: true because choice-type inputs legitimately use '' as the "no selection" option
                options = ParseStringOrStringSequence(ref reader, arena, diagnostics, "on.workflow_dispatch input options must be scalar or sequence of scalar", allowElemEmpty: true);
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected on.workflow_dispatch input option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new DispatchInput
        {
            Name = nameNode,
            Description = description,
            Required = required,
            Default = defaultValue,
            Type = type,
            Options = options,
            Range = arena.GetStringRange(nameNode),
        };
    }

    private static DispatchInputType ParseDispatchInputType<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, "on.workflow_dispatch input type must be scalar", reader.CurrentStart);
            reader.SkipCurrentNode();
            return DispatchInputType.None;
        }

        var valueUtf8 = reader.GetScalarUtf8();
        var type = valueUtf8.SequenceEqual("string"u8) ? DispatchInputType.String
            : valueUtf8.SequenceEqual("number"u8) ? DispatchInputType.Number
            : valueUtf8.SequenceEqual("boolean"u8) ? DispatchInputType.Boolean
            : valueUtf8.SequenceEqual("choice"u8) ? DispatchInputType.Choice
            : valueUtf8.SequenceEqual("environment"u8) ? DispatchInputType.Environment
            : DispatchInputType.None;

        if (type == DispatchInputType.None)
        {
            AddError(diagnostics, "on.workflow_dispatch input type must be one of string, number, boolean, choice, environment", reader.CurrentStart);
        }

        reader.Read();
        return type;
    }
}
