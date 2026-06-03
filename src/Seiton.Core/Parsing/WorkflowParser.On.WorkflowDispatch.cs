// on.workflow_dispatch — inputs and dispatch input field parsing.

using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static WorkflowDispatchEvent ParseWorkflowDispatchEvent<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, "on.workflow_dispatch must be object", reader.CurrentStart);
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
                AddError(ref diagnostics, "on.workflow_dispatch option key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "on.workflow_dispatch"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<OnWorkflowDispatchTopKeyTable>(keyUtf8, out _))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(ref diagnostics, "on.workflow_dispatch contains duplicate key: inputs", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                inputs = ParseWorkflowDispatchInputs(ref reader, arena, ref diagnostics, source);
                continue;
            }

            var keySlice = reader.GetScalarSlice();
            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            var wdSuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknown, Generated.ExpectedKeys.OnWorkflowDispatchKeys);
            var wdMsg = wdSuggestion is not null
                ? $"on.workflow_dispatch has unexpected key \"{unknown}\" for \"workflow_dispatch\" section. did you mean \"{wdSuggestion}\"? expected \"inputs\""
                : $"on.workflow_dispatch has unexpected key \"{unknown}\" for \"workflow_dispatch\" section. expected \"inputs\"";
            var wdFix = wdSuggestion is not null
                ? new DiagnosticFix($"replace '{unknown}' with '{wdSuggestion}'", [new TextEdit(keySlice.Offset, keySlice.Length, wdSuggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, wdMsg, keyMark, wdFix);
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

    private static SliceMap<DispatchInput>? ParseWorkflowDispatchInputs<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, "on.workflow_dispatch.inputs must be object", reader.CurrentStart);
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
                    AddError(ref diagnostics, "on.workflow_dispatch.inputs key must be string", reader.CurrentStart);
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
                    ref diagnostics,
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

                var input = ParseWorkflowDispatchInput(ref reader, arena, ref diagnostics, nameNode);
                map.Add(new SliceMap<DispatchInput>.Entry(idSlice, input));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            var (dispEntries, dispCount) = map.DetachArray();
            arena.RegisterSliceMapBuffer(dispEntries);
            return new SliceMap<DispatchInput>(dispEntries, dispCount, caseSensitive: false);
        }
        finally { map.Dispose(); }
    }

    private static DispatchInput ParseWorkflowDispatchInput<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNodeId description = default;
        BoolNodeId required = default;
        StringNodeId defaultValue = default;
        DispatchInputType type = DispatchInputType.None;
        IReadOnlyList<StringNodeId>? options = null;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, "on.workflow_dispatch input must be object", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new DispatchInput { Name = nameNode, Description = description, Required = required, Default = defaultValue, Type = type, Options = options, Range = arena.GetStringRange(nameNode) };
        }

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, "on.workflow_dispatch input option key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "on.workflow_dispatch input"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<WorkflowDispatchInputFieldKeyTable>(keyUtf8, out var dispatchFieldOrdinal))
            {
                reader.Read();
                var fk = (WorkflowDispatchInputFieldKey)dispatchFieldOrdinal;
                if (!TrySetBit(ref seen, dispatchFieldOrdinal))
                {
                    var dupName = fk switch
                    {
                        WorkflowDispatchInputFieldKey.Description => "description",
                        WorkflowDispatchInputFieldKey.Required => "required",
                        WorkflowDispatchInputFieldKey.Default => "default",
                        WorkflowDispatchInputFieldKey.Type => "type",
                        WorkflowDispatchInputFieldKey.Options => "options",
                        _ => "option",
                    };
                    AddError(ref diagnostics, $"on.workflow_dispatch input contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (fk)
                {
                    case WorkflowDispatchInputFieldKey.Description:
                        description = ParseString(ref reader, arena, ref diagnostics, "on.workflow_dispatch input description must be string");
                        continue;
                    case WorkflowDispatchInputFieldKey.Required:
                        required = ParseBoolNode(ref reader, arena, ref diagnostics, "on.workflow_dispatch input required must be bool");
                        continue;
                    case WorkflowDispatchInputFieldKey.Default:
                        defaultValue = ParseString(ref reader, arena, ref diagnostics, "on.workflow_dispatch input default must be string", allowEmpty: true);
                        continue;
                    case WorkflowDispatchInputFieldKey.Type:
                        type = ParseDispatchInputType(ref reader, arena, ref diagnostics);
                        continue;
                    case WorkflowDispatchInputFieldKey.Options:
                        {
                            var optSeqMark = reader.CurrentStart;
                            var inputName = Encoding.UTF8.GetString(arena.GetStringValue(nameNode));
                            options = ParseStringOrStringSequence(ref reader, arena, ref diagnostics, out var optErr, out var optMark, allowElemEmpty: true);
                            if (optErr)
                                AddError(ref diagnostics, "on.workflow_dispatch input options must be string or array of strings", optMark);
                            else if (options is { Count: 0 })
                                AddError(ref diagnostics, "\"options\" section should not be empty", optSeqMark);
                            continue;
                        }
                    default:
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                }
            }

            var keySlice = reader.GetScalarSlice();
            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            var wdInputSuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknown, Generated.ExpectedKeys.WorkflowDispatchInputFieldKeys);
            var wdInputMsg = wdInputSuggestion is not null
                ? $"on.workflow_dispatch.inputs has unexpected key \"{unknown}\" for \"inputs\" section. did you mean \"{wdInputSuggestion}\"? expected one of {Generated.ExpectedKeys.WorkflowDispatchInputFieldKeys}"
                : $"on.workflow_dispatch.inputs has unexpected key \"{unknown}\" for \"inputs\" section. expected one of {Generated.ExpectedKeys.WorkflowDispatchInputFieldKeys}";
            var wdInputFix = wdInputSuggestion is not null
                ? new DiagnosticFix($"replace '{unknown}' with '{wdInputSuggestion}'", [new TextEdit(keySlice.Offset, keySlice.Length, wdInputSuggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, wdInputMsg, keyMark, wdInputFix);
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

    private static DispatchInputType ParseDispatchInputType<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(ref diagnostics, "on.workflow_dispatch input type must be string", reader.CurrentStart);
            reader.SkipCurrentNode();
            return DispatchInputType.None;
        }

        var valueUtf8 = reader.GetScalarUtf8();
        DispatchInputType type;
        if (Utf8MappingDispatch.TryMatchFirstOrdered<DispatchInputTypeScalarKeyTable>(valueUtf8, out var typeOrd))
        {
            type = typeOrd switch
            {
                0 => DispatchInputType.Boolean,
                1 => DispatchInputType.Choice,
                2 => DispatchInputType.Environment,
                3 => DispatchInputType.Number,
                4 => DispatchInputType.String,
                _ => DispatchInputType.None,
            };
        }
        else
        {
            type = DispatchInputType.None;
        }

        if (type == DispatchInputType.None)
        {
            var valueText = Encoding.UTF8.GetString(valueUtf8);
            AddError(ref diagnostics, $"on.workflow_dispatch input type '{valueText}' is invalid; must be one of string, number, boolean, choice, environment", reader.CurrentStart);
        }

        reader.Read();
        return type;
    }
}
