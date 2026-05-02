// on.workflow_call — inputs, secrets, outputs for reusable workflow triggers.

using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static WorkflowCallEvent ParseWorkflowCallEvent<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, "on.workflow_call must be object", reader.CurrentStart);
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
                AddError(ref diagnostics, "on.workflow_call option key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "on.workflow_call"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<WorkflowCallEventKeyTable>(keyUtf8, out var wceOrdinal))
            {
                reader.Read();
                var wck = (WorkflowCallEventMappingKey)wceOrdinal;
                if (!TrySetBit(ref seen, wceOrdinal))
                {
                    var dupName = wck == WorkflowCallEventMappingKey.Inputs ? "inputs" : wck == WorkflowCallEventMappingKey.Secrets ? "secrets" : "outputs";
                    AddError(ref diagnostics, $"on.workflow_call contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (wck)
                {
                    case WorkflowCallEventMappingKey.Inputs:
                        inputs = ParseWorkflowCallInputs(ref reader, arena, ref diagnostics, source);
                        continue;
                    case WorkflowCallEventMappingKey.Secrets:
                        secrets = ParseWorkflowCallSecrets(ref reader, arena, ref diagnostics, source);
                        continue;
                    case WorkflowCallEventMappingKey.Outputs:
                        outputs = ParseWorkflowCallOutputs(ref reader, arena, ref diagnostics, source);
                        continue;
                    default:
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                }
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(ref diagnostics, $"unexpected key \"{unknown}\" for \"workflow_call\" section. expected one of {Generated.ExpectedKeys.OnWorkflowCallKeys}", keyMark);
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

    private static WorkflowCallEventInput[]? ParseWorkflowCallInputs<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, "on.workflow_call.inputs must be object", reader.CurrentStart);
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
                    AddError(ref diagnostics, "on.workflow_call.inputs key must be string", reader.CurrentStart);
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

                list.Add(ParseWorkflowCallInput(ref reader, arena, ref diagnostics, nameNode, id, idText));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            return list.ToArray();
        }
        finally { list.Dispose(); }
    }

    private static WorkflowCallEventInput ParseWorkflowCallInput<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, StringNodeId nameNode, Utf8String id, string idText)
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
            // Null/empty body (e.g. `input0:` followed by next key) — treat as empty input.
            // Still require type field; report "type is required" instead of "must be object".
            if (reader.CurrentKind == YamlEventKind.Scalar)
            {
                var bodyUtf8 = reader.GetScalarUtf8();
                if (IsNullLikeOnEventOptionsScalar(bodyUtf8) || bodyUtf8.Length == 0)
                {
                    reader.Read(); // consume null scalar
                }
                else
                {
                    AddError(ref diagnostics, "on.workflow_call input must be object", reader.CurrentStart);
                    reader.SkipCurrentNode();
                }
            }
            else
            {
                AddError(ref diagnostics, "on.workflow_call input must be object", reader.CurrentStart);
                reader.SkipCurrentNode();
            }
            // Report missing type
            AddError(
                ref diagnostics,
                $"on.workflow_call input \"{idText}\" is missing \"type\"",
                new TextPosition(arena.GetStringRange(nameNode).Start, arena.GetStringRange(nameNode).StartLine, arena.GetStringRange(nameNode).StartColumn));
            return new WorkflowCallEventInput { Name = nameNode, Id = id, Description = description, Required = required, Default = defaultValue, Type = type, Range = arena.GetStringRange(nameNode) };
        }

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, "on.workflow_call input option key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "on.workflow_call input"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<WorkflowCallInputFieldKeyTable>(keyUtf8, out var wcifOrdinal))
            {
                reader.Read();
                var ifk = (WorkflowCallInputFieldKey)wcifOrdinal;
                if (!TrySetBit(ref seen, wcifOrdinal))
                {
                    var dupName = ifk switch
                    {
                        WorkflowCallInputFieldKey.Description => "description",
                        WorkflowCallInputFieldKey.Required => "required",
                        WorkflowCallInputFieldKey.Default => "default",
                        WorkflowCallInputFieldKey.Type => "type",
                        _ => "option",
                    };
                    AddError(ref diagnostics, $"on.workflow_call input contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (ifk)
                {
                    case WorkflowCallInputFieldKey.Description:
                        description = ParseString(ref reader, arena, ref diagnostics, "on.workflow_call input description must be string");
                        continue;
                    case WorkflowCallInputFieldKey.Required:
                        required = ParseBoolNode(ref reader, arena, ref diagnostics, "on.workflow_call input required must be bool");
                        continue;
                    case WorkflowCallInputFieldKey.Default:
                        defaultValue = ParseString(ref reader, arena, ref diagnostics, "on.workflow_call input default must be string", allowEmpty: true);
                        continue;
                    case WorkflowCallInputFieldKey.Type:
                        type = ParseWorkflowCallInputType(ref reader, arena, ref diagnostics);
                        hasType = true;
                        continue;
                    default:
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                }
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(ref diagnostics, $"unexpected key \"{unknown}\" for inputs at workflow_call event. expected one of {Generated.ExpectedKeys.WorkflowCallInputFieldKeys}", keyMark);
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
                ref diagnostics,
                $"on.workflow_call input \"{idText}\" is missing \"type\"",
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

    private static WorkflowCallInputType ParseWorkflowCallInputType<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(ref diagnostics, "on.workflow_call input type must be string", reader.CurrentStart);
            reader.SkipCurrentNode();
            return WorkflowCallInputType.Invalid;
        }

        var valueUtf8 = reader.GetScalarUtf8();
        WorkflowCallInputType type;
        if (Utf8MappingDispatch.TryMatchFirstOrdered<WorkflowCallInputTypeScalarKeyTable>(valueUtf8, out var typeOrd))
        {
            type = typeOrd switch
            {
                0 => WorkflowCallInputType.Boolean,
                1 => WorkflowCallInputType.Number,
                2 => WorkflowCallInputType.String,
                _ => WorkflowCallInputType.Invalid,
            };
        }
        else
        {
            type = WorkflowCallInputType.Invalid;
        }

        if (type == WorkflowCallInputType.Invalid)
        {
            var valueText = Encoding.UTF8.GetString(valueUtf8);
            AddError(ref diagnostics, $"on.workflow_call input type '{valueText}' is invalid; must be one of boolean, number, string", reader.CurrentStart);
        }

        reader.Read();
        return type;
    }

    private static SliceMap<WorkflowCallEventSecret>? ParseWorkflowCallSecrets<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            // Empty/null secrets: treat as "no secrets declared" (strict empty object)
            if (reader.CurrentKind == YamlEventKind.Scalar)
            {
                var scalarUtf8 = reader.GetScalarUtf8();
                if (IsNullLikeOnEventOptionsScalar(scalarUtf8) || scalarUtf8.Length == 0)
                {
                    reader.Read(); // consume null scalar
                    return new SliceMap<WorkflowCallEventSecret>();
                }
            }

            AddError(ref diagnostics, "on.workflow_call.secrets must be object", reader.CurrentStart);
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
                    AddError(ref diagnostics, "on.workflow_call.secrets key must be string", reader.CurrentStart);
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

                map.Add(new SliceMap<WorkflowCallEventSecret>.Entry(idSlice, ParseWorkflowCallSecret(ref reader, arena, ref diagnostics, nameNode)));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            var (wcSecretEntries, wcSecretCount) = map.DetachBuffer();
            return new SliceMap<WorkflowCallEventSecret>(wcSecretEntries, wcSecretCount, caseSensitive: false);
        }
        finally { map.Dispose(); }
    }

    private static WorkflowCallEventSecret ParseWorkflowCallSecret<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNodeId description = default;
        BoolNodeId required = default;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            // Null/empty body (e.g. `secret0:` followed by next key) — accept silently.
            // Secrets have no required fields (description and required are both optional).
            if (reader.CurrentKind == YamlEventKind.Scalar)
            {
                var bodyUtf8 = reader.GetScalarUtf8();
                if (IsNullLikeOnEventOptionsScalar(bodyUtf8) || bodyUtf8.Length == 0)
                {
                    reader.Read(); // consume null scalar
                }
                else
                {
                    AddError(ref diagnostics, "on.workflow_call secret must be object", reader.CurrentStart);
                    reader.SkipCurrentNode();
                }
            }
            else
            {
                AddError(ref diagnostics, "on.workflow_call secret must be object", reader.CurrentStart);
                reader.SkipCurrentNode();
            }
            return new WorkflowCallEventSecret { Name = nameNode, Description = description, Required = required, Range = arena.GetStringRange(nameNode) };
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, "on.workflow_call secret option key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "on.workflow_call secret"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<WorkflowCallSecretFieldKeyTable>(keyUtf8, out var wcsfOrdinal))
            {
                reader.Read();
                var sfk = (WorkflowCallSecretFieldKey)wcsfOrdinal;
                if (!TrySetBit(ref seen, wcsfOrdinal))
                {
                    var dupName = sfk == WorkflowCallSecretFieldKey.Description ? "description" : "required";
                    AddError(ref diagnostics, $"on.workflow_call secret contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (sfk)
                {
                    case WorkflowCallSecretFieldKey.Description:
                        description = ParseString(ref reader, arena, ref diagnostics, "on.workflow_call secret description must be string");
                        continue;
                    case WorkflowCallSecretFieldKey.Required:
                        required = ParseBoolNode(ref reader, arena, ref diagnostics, "on.workflow_call secret required must be bool");
                        continue;
                    default:
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                }
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(ref diagnostics, $"on.workflow_call.secrets unexpected key \"{unknown}\" for \"secrets\" section. expected one of {Generated.ExpectedKeys.WorkflowCallSecretFieldKeys}", keyMark);
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

    private static SliceMap<WorkflowCallEventOutput>? ParseWorkflowCallOutputs<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, "on.workflow_call.outputs must be object", reader.CurrentStart);
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
                    AddError(ref diagnostics, "on.workflow_call.outputs key must be string", reader.CurrentStart);
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

                map.Add(new SliceMap<WorkflowCallEventOutput>.Entry(idSlice, ParseWorkflowCallOutput(ref reader, arena, ref diagnostics, nameNode, idText)));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            var (wcOutputEntries, wcOutputCount) = map.DetachBuffer();
            return new SliceMap<WorkflowCallEventOutput>(wcOutputEntries, wcOutputCount, caseSensitive: false);
        }
        finally { map.Dispose(); }
    }

    private static WorkflowCallEventOutput ParseWorkflowCallOutput<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, StringNodeId nameNode, string idText)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNodeId description = default;
        StringNodeId value = default;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            // Null/empty body (e.g. `missing-all:` followed by next key) — treat as empty output.
            // Still require value field.
            if (reader.CurrentKind == YamlEventKind.Scalar)
            {
                var bodyUtf8 = reader.GetScalarUtf8();
                if (IsNullLikeOnEventOptionsScalar(bodyUtf8) || bodyUtf8.Length == 0)
                {
                    reader.Read(); // consume null scalar
                }
                else
                {
                    AddError(ref diagnostics, "on.workflow_call output must be object", reader.CurrentStart);
                    reader.SkipCurrentNode();
                }
            }
            else
            {
                AddError(ref diagnostics, "on.workflow_call output must be object", reader.CurrentStart);
                reader.SkipCurrentNode();
            }
            // Report missing value
            AddError(
                ref diagnostics,
                $"on.workflow_call output \"{idText}\" is missing \"value\"",
                new TextPosition(arena.GetStringRange(nameNode).Start, arena.GetStringRange(nameNode).StartLine, arena.GetStringRange(nameNode).StartColumn));
            return new WorkflowCallEventOutput { Name = nameNode, Description = description, Value = value, Range = arena.GetStringRange(nameNode) };
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, "on.workflow_call output option key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "on.workflow_call output"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<WorkflowCallOutputFieldKeyTable>(keyUtf8, out var wcofOrdinal))
            {
                reader.Read();
                var ofk = (WorkflowCallOutputFieldKey)wcofOrdinal;
                if (!TrySetBit(ref seen, wcofOrdinal))
                {
                    var dupName = ofk == WorkflowCallOutputFieldKey.Description ? "description" : "value";
                    AddError(ref diagnostics, $"on.workflow_call output contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (ofk)
                {
                    case WorkflowCallOutputFieldKey.Description:
                        description = ParseString(ref reader, arena, ref diagnostics, "on.workflow_call output description must be string");
                        continue;
                    case WorkflowCallOutputFieldKey.Value:
                        value = ParseStringAndValidateExpression(
                            ref reader, arena, ref diagnostics,
                            ExpressionValidationContext.WorkflowCallOutputsValue,
                            "on.workflow_call output value must be string",
                            parseWholeValueIfNoEmbedded: false);
                        continue;
                    default:
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                }
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(ref diagnostics, $"unexpected key \"{unknown}\" for outputs at workflow_call event. expected one of {Generated.ExpectedKeys.WorkflowCallOutputFieldKeys}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        // spec §11.17 / §12: workflow_call output requires `value`
        if (!value.HasValue)
        {
            AddError(
                ref diagnostics,
                $"on.workflow_call output \"{idText}\" is missing \"value\"",
                new TextPosition(arena.GetStringRange(nameNode).Start, arena.GetStringRange(nameNode).StartLine, arena.GetStringRange(nameNode).StartColumn));
        }
        else if (value.HasValue && arena.GetStringValue(value).Length == 0)
        {
            var valueRange = arena.GetStringRange(value);
            AddError(ref diagnostics, $"on.workflow_call output \"{idText}\" value should not be empty", new TextPosition(valueRange.Start, valueRange.StartLine, valueRange.StartColumn));
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
