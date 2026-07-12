// on.workflow_call — inputs, secrets, outputs for reusable workflow triggers.

using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static void ParseWorkflowCallEvent<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, "on.workflow_call must be object", reader.CurrentStart);
            reader.SkipCurrentNode();
            arena.AddEvent(new EventData { Kind = EventKind.WorkflowCall, EventName = nameNode, Range = arena.GetStringRange(nameNode), Payload = arena.AddWorkflowCallEvent(default) });
            return;
        }

        NodeRange inputs = default;
        NodeRange secrets = default;
        NodeRange outputs = default;
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

            var keySlice = reader.GetScalarSlice();
            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            var wcSuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknown, Generated.ExpectedKeys.OnWorkflowCallKeys);
            var wcMsg = wcSuggestion is not null
                ? $"on.workflow_call has unexpected key \"{unknown}\" for \"workflow_call\" section. did you mean \"{wcSuggestion}\"? expected one of {Generated.ExpectedKeys.OnWorkflowCallKeys}"
                : $"on.workflow_call has unexpected key \"{unknown}\" for \"workflow_call\" section. expected one of {Generated.ExpectedKeys.OnWorkflowCallKeys}";
            var wcFix = wcSuggestion is not null
                ? new DiagnosticFix($"replace '{unknown}' with '{wcSuggestion}'", [new TextEdit(keySlice.Offset, keySlice.Length, wcSuggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, wcMsg, keyMark, wcFix);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        var payload = arena.AddWorkflowCallEvent(new WorkflowCallEventData
        {
            Inputs = inputs,
            Secrets = secrets,
            Outputs = outputs,
        });
        arena.AddEvent(new EventData { Kind = EventKind.WorkflowCall, EventName = nameNode, Range = arena.GetStringRange(nameNode), Payload = payload });
    }

    private static NodeRange ParseWorkflowCallInputs<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, "on.workflow_call.inputs must be object", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var first = arena.WorkflowCallEventInputCount;
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
                ref keyStore,
                ref keyCount,
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

            arena.AddWorkflowCallEventInput(ParseWorkflowCallInput(ref reader, arena, ref diagnostics, nameNode, id, idText));
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new NodeRange(first, arena.WorkflowCallEventInputCount - first);
    }

    private static WorkflowCallEventInputData ParseWorkflowCallInput<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, StringNodeId nameNode, Utf8String id, string idText)
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
            return new WorkflowCallEventInputData { Name = nameNode, Id = id, Description = description, Required = required, Default = defaultValue, Type = type, Range = arena.GetStringRange(nameNode) };
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

            var keySlice = reader.GetScalarSlice();
            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            var wcInputSuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknown, Generated.ExpectedKeys.WorkflowCallInputFieldKeys);
            var wcInputMsg = wcInputSuggestion is not null
                ? $"on.workflow_call.inputs has unexpected key \"{unknown}\" for \"inputs\" section. did you mean \"{wcInputSuggestion}\"? expected one of {Generated.ExpectedKeys.WorkflowCallInputFieldKeys}"
                : $"on.workflow_call.inputs has unexpected key \"{unknown}\" for \"inputs\" section. expected one of {Generated.ExpectedKeys.WorkflowCallInputFieldKeys}";
            var wcInputFix = wcInputSuggestion is not null
                ? new DiagnosticFix($"replace '{unknown}' with '{wcInputSuggestion}'", [new TextEdit(keySlice.Offset, keySlice.Length, wcInputSuggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, wcInputMsg, keyMark, wcInputFix);
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

        return new WorkflowCallEventInputData
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

    private static NodeRange ParseWorkflowCallSecrets<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
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
                    return new NodeRange(arena.WorkflowCallEventSecretCount, 0);
                }
            }

            AddError(ref diagnostics, "on.workflow_call.secrets must be object", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var first = arena.WorkflowCallEventSecretCount;
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
                ref keyStore,
                ref keyCount,
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

            arena.AddWorkflowCallEventSecret(ParseWorkflowCallSecret(ref reader, arena, ref diagnostics, nameNode, idSlice));
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new NodeRange(first, arena.WorkflowCallEventSecretCount - first);
    }

    private static WorkflowCallEventSecretData ParseWorkflowCallSecret<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, StringNodeId nameNode, Utf8Slice key)
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
            return new WorkflowCallEventSecretData { Key = key, Name = nameNode, Description = description, Required = required, Range = arena.GetStringRange(nameNode) };
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

            var keySlice = reader.GetScalarSlice();
            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            var wcSecretSuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknown, Generated.ExpectedKeys.WorkflowCallSecretFieldKeys);
            var wcSecretMsg = wcSecretSuggestion is not null
                ? $"on.workflow_call.secrets has unexpected key \"{unknown}\" for \"secrets\" section. did you mean \"{wcSecretSuggestion}\"? expected one of {Generated.ExpectedKeys.WorkflowCallSecretFieldKeys}"
                : $"on.workflow_call.secrets has unexpected key \"{unknown}\" for \"secrets\" section. expected one of {Generated.ExpectedKeys.WorkflowCallSecretFieldKeys}";
            var wcSecretFix = wcSecretSuggestion is not null
                ? new DiagnosticFix($"replace '{unknown}' with '{wcSecretSuggestion}'", [new TextEdit(keySlice.Offset, keySlice.Length, wcSecretSuggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, wcSecretMsg, keyMark, wcSecretFix);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new WorkflowCallEventSecretData
        {
            Key = key,
            Name = nameNode,
            Description = description,
            Required = required,
            Range = arena.GetStringRange(nameNode),
        };
    }

    private static NodeRange ParseWorkflowCallOutputs<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, "on.workflow_call.outputs must be object", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var first = arena.WorkflowCallEventOutputCount;
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
                ref keyStore,
                ref keyCount,
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

            arena.AddWorkflowCallEventOutput(ParseWorkflowCallOutput(ref reader, arena, ref diagnostics, nameNode, idSlice, idText));
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new NodeRange(first, arena.WorkflowCallEventOutputCount - first);
    }

    private static WorkflowCallEventOutputData ParseWorkflowCallOutput<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, StringNodeId nameNode, Utf8Slice key, string idText)
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
            return new WorkflowCallEventOutputData { Key = key, Name = nameNode, Description = description, Value = value, Range = arena.GetStringRange(nameNode) };
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

            var keySlice = reader.GetScalarSlice();
            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            var wcOutputSuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknown, Generated.ExpectedKeys.WorkflowCallOutputFieldKeys);
            var wcOutputMsg = wcOutputSuggestion is not null
                ? $"on.workflow_call.outputs has unexpected key \"{unknown}\" for \"outputs\" section. did you mean \"{wcOutputSuggestion}\"? expected one of {Generated.ExpectedKeys.WorkflowCallOutputFieldKeys}"
                : $"on.workflow_call.outputs has unexpected key \"{unknown}\" for \"outputs\" section. expected one of {Generated.ExpectedKeys.WorkflowCallOutputFieldKeys}";
            var wcOutputFix = wcOutputSuggestion is not null
                ? new DiagnosticFix($"replace '{unknown}' with '{wcOutputSuggestion}'", [new TextEdit(keySlice.Offset, keySlice.Length, wcOutputSuggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, wcOutputMsg, keyMark, wcOutputFix);
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

        return new WorkflowCallEventOutputData
        {
            Key = key,
            Name = nameNode,
            Description = description,
            Value = value,
            Range = arena.GetStringRange(nameNode),
        };
    }
}
