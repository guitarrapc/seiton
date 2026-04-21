using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static Event[] ParseOnEvents<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var eventMark = reader.CurrentStart;
            var eventInfo = ReadOnEventInfo(ref reader); // try-catch inside for non-UTF8 scalars
            ValidateKnownOnEvent(in eventInfo, eventMark, diagnostics);
            Utf8Slice eventSlice;
            int eventByteLen;
            try { var u = reader.GetScalarUtf8(); eventSlice = reader.GetScalarSlice(); eventByteLen = u.Length; }
            catch { eventSlice = default; eventByteLen = 0; }
            var nameNode = new StringNode { Value = eventSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(eventMark, eventByteLen) };
            reader.Read();
            // spec §3.4.1: schedule requires mapping form; scalar form is an error
            if (eventInfo.IsKnown && eventInfo.Spec.Id == WebhookTypes.EventId.Schedule)
            {
                AddError(diagnostics, "on.schedule must be mapping", eventMark);
                return [];
            }
            return [BuildSimpleEvent(in eventInfo, nameNode)];
        }

        if (reader.CurrentKind == YamlEventKind.SequenceStart)
        {
            reader.Read(); // consume SequenceStart
            var events = new List<Event>(4);
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
                Utf8Slice eventSlice;
                int eventByteLen;
                try { var u = reader.GetScalarUtf8(); eventSlice = reader.GetScalarSlice(); eventByteLen = u.Length; }
                catch { eventSlice = default; eventByteLen = 0; }
                var nameNode = new StringNode { Value = eventSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(eventMark, eventByteLen) };
                reader.Read();
                // spec §3.4.1: schedule requires mapping form; scalar form is an error
                if (eventInfo.IsKnown && eventInfo.Spec.Id == WebhookTypes.EventId.Schedule)
                {
                    AddError(diagnostics, "on.schedule must be mapping", eventMark);
                    continue;
                }
                events.Add(BuildSimpleEvent(in eventInfo, nameNode));
            }

            if (reader.CurrentKind == YamlEventKind.SequenceEnd) { reader.Read(); }
            return events.ToArray();
        }

        if (reader.CurrentKind == YamlEventKind.MappingStart)
        {
            reader.Read(); // consume MappingStart
            var events = new List<Event>(4);
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, "on mapping key must be scalar event name", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd) { reader.SkipCurrentNode(); }
                    continue;
                }

                var eventMark = reader.CurrentStart;
                var eventKeySlice = reader.GetScalarSlice();
                var eventKeyUtf8 = reader.GetScalarUtf8();
                if (!TryRegisterDynamicKey(
                    source,
                    eventKeyUtf8,
                    eventKeySlice.Offset,
                    eventKeySlice.Length,
                    eventMark,
                    diagnostics,
                    keyStore,
                    ref keyCount,
                    caseSensitive: false,
                    "on"))
                {
                    reader.Read();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var eventInfo = ReadOnEventInfo(ref reader);
                ValidateKnownOnEvent(in eventInfo, eventMark, diagnostics);
                Utf8Slice eventSlice;
                int eventByteLen;
                try { var u = reader.GetScalarUtf8(); eventSlice = reader.GetScalarSlice(); eventByteLen = u.Length; }
                catch { eventSlice = default; eventByteLen = 0; }
                var nameNode = new StringNode { Value = eventSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(eventMark, eventByteLen) };
                reader.Read(); // consume event key

                if (reader.End)
                {
                    events.Add(BuildSimpleEvent(in eventInfo, nameNode));
                    break;
                }

                if (IsSpecialOnEvent(in eventInfo))
                {
                    events.Add(ParseOnEventWithOptions(ref reader, diagnostics, source, in eventInfo, eventMark, nameNode));
                    continue;
                }

                if (reader.CurrentKind == YamlEventKind.MappingStart)
                {
                    events.Add(ParseOnEventWithOptions(ref reader, diagnostics, source, in eventInfo, eventMark, nameNode));
                    continue;
                }

                if (reader.CurrentKind is YamlEventKind.Scalar or YamlEventKind.SequenceStart)
                {
                    // Some events have null-like / scalar options value; accept and build stub
                    reader.SkipCurrentNode();
                    events.Add(BuildSimpleEvent(in eventInfo, nameNode));
                    continue;
                }

                AddError(diagnostics, $"on.{eventInfo.Name} must be scalar, sequence, or mapping", reader.CurrentStart);
                reader.SkipCurrentNode();
                events.Add(BuildSimpleEvent(in eventInfo, nameNode));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd) { reader.Read(); }
            return events.ToArray();
        }

        AddError(diagnostics, "on must be scalar, sequence, or mapping", reader.CurrentStart);
        reader.SkipCurrentNode();
        return [];
    }

    private static Event BuildSimpleEvent(in OnEventInfo eventInfo, StringNode nameNode)
    {
        if (eventInfo.IsKnown)
        {
            return eventInfo.Spec.Id switch
            {
                WebhookTypes.EventId.Schedule => new ScheduledEvent { EventName = nameNode, Range = nameNode.Range },
                WebhookTypes.EventId.WorkflowDispatch => new WorkflowDispatchEvent { EventName = nameNode, Range = nameNode.Range },
                WebhookTypes.EventId.WorkflowCall => new WorkflowCallEvent { EventName = nameNode, Range = nameNode.Range },
                WebhookTypes.EventId.RepositoryDispatch => new RepositoryDispatchEvent { EventName = nameNode, Range = nameNode.Range },
                WebhookTypes.EventId.ImageVersion => new ImageVersionEvent { EventName = nameNode, Range = nameNode.Range },
                _ => new WebhookEvent { EventName = nameNode, Hook = nameNode, Range = nameNode.Range },
            };
        }

        return new WebhookEvent { EventName = nameNode, Hook = nameNode, Range = nameNode.Range };
    }

    private static Event ParseOnEventWithOptions<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, in OnEventInfo eventInfo, TextPosition eventMark, StringNode nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (!IsSpecialOnEvent(in eventInfo))
        {
            // Webhook event: build full AST with filters
            return ParseWebhookEventWithOptions(ref reader, diagnostics, in eventInfo, eventMark, nameNode);
        }

        if (reader.CurrentKind == YamlEventKind.Scalar
            && eventInfo.Spec.Id != WebhookTypes.EventId.Schedule)
        {
            var isNullLike = false;
            try
            {
                isNullLike = IsNullLikeOnEventOptionsScalar(reader.GetScalarUtf8());
            }
            catch
            {
                // Null scalar values may not provide UTF-8 bytes via adapter APIs.
                isNullLike = true;
            }

            if (!isNullLike)
            {
                return eventInfo.Spec.Id switch
                {
                    WebhookTypes.EventId.Schedule => ParseScheduleEvent(ref reader, diagnostics, nameNode),
                    WebhookTypes.EventId.WorkflowDispatch => ParseWorkflowDispatchEvent(ref reader, diagnostics, source, nameNode),
                    WebhookTypes.EventId.WorkflowCall => ParseWorkflowCallEvent(ref reader, diagnostics, source, nameNode),
                    WebhookTypes.EventId.RepositoryDispatch => ParseRepositoryDispatchEvent(ref reader, diagnostics, in eventInfo, nameNode),
                    WebhookTypes.EventId.ImageVersion => ParseImageVersionEvent(ref reader, diagnostics, nameNode),
                    _ => BuildSimpleEvent(in eventInfo, nameNode),
                };
            }

            // Allow scalar/null form such as "workflow_dispatch:" in on-mapping context.
            reader.SkipCurrentNode();
            return BuildSimpleEvent(in eventInfo, nameNode);
        }

        return eventInfo.Spec.Id switch
        {
            WebhookTypes.EventId.Schedule => ParseScheduleEvent(ref reader, diagnostics, nameNode),
            WebhookTypes.EventId.WorkflowDispatch => ParseWorkflowDispatchEvent(ref reader, diagnostics, source, nameNode),
            WebhookTypes.EventId.WorkflowCall => ParseWorkflowCallEvent(ref reader, diagnostics, source, nameNode),
            WebhookTypes.EventId.RepositoryDispatch => ParseRepositoryDispatchEvent(ref reader, diagnostics, in eventInfo, nameNode),
            WebhookTypes.EventId.ImageVersion => ParseImageVersionEvent(ref reader, diagnostics, nameNode),
            _ => BuildSimpleEvent(in eventInfo, nameNode),
        };
    }

    private static bool IsSpecialOnEvent(in OnEventInfo eventInfo)
    {
        return eventInfo.IsKnown
            && (eventInfo.Spec.Id == WebhookTypes.EventId.Schedule
                || eventInfo.Spec.Id == WebhookTypes.EventId.WorkflowDispatch
                || eventInfo.Spec.Id == WebhookTypes.EventId.WorkflowCall
                || eventInfo.Spec.Id == WebhookTypes.EventId.RepositoryDispatch
                || eventInfo.Spec.Id == WebhookTypes.EventId.ImageVersion);
    }

    private static bool IsNullLikeOnEventOptionsScalar(ReadOnlySpan<byte> scalarUtf8)
    {
        return scalarUtf8.Length == 0
            || scalarUtf8.SequenceEqual("~"u8)
            || scalarUtf8.SequenceEqual("null"u8)
            || scalarUtf8.SequenceEqual("Null"u8)
            || scalarUtf8.SequenceEqual("NULL"u8);
    }

    private static ScheduledEvent ParseScheduleEvent<TReader>(
        ref TReader reader,
        List<Diagnostic> diagnostics,
        StringNode nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, "on.schedule must be sequence", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new ScheduledEvent { EventName = nameNode, Schedules = [], Range = nameNode.Range };
        }

        var schedules = new List<ScheduleEntry>(2);
        reader.Read(); // consume SequenceStart

        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            if (reader.CurrentKind != YamlEventKind.MappingStart)
            {
                AddError(diagnostics, "on.schedule item must be mapping", reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            schedules.Add(ParseScheduleEntry(ref reader, diagnostics));
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }

        return new ScheduledEvent { EventName = nameNode, Schedules = schedules.ToArray(), Range = nameNode.Range };
    }

    private static ScheduleEntry ParseScheduleEntry<TReader>(ref TReader reader, List<Diagnostic> diagnostics)
        where TReader : IYamlStreamReader, allows ref struct
    {
        TextRange range = default;
        StringNode? cron = null;
        StringNode? timezone = null;
        ulong seen = 0;

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.schedule item key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "on.schedule"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("cron"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "on.schedule contains duplicate key: cron", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                cron = ParseString(ref reader, diagnostics, "on.schedule.cron must be scalar");
                if (cron is not null)
                {
                    range = cron.Range;
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("timezone"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.schedule contains duplicate key: timezone", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                timezone = ParseString(ref reader, diagnostics, "on.schedule.timezone must be scalar");
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected on.schedule option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (cron is null)
        {
            AddError(diagnostics, "on.schedule item requires cron", reader.CurrentStart);
        }

        return new ScheduleEntry
        {
            Cron = cron,
            Timezone = timezone,
            Range = range,
        };
    }

    private static WorkflowDispatchEvent ParseWorkflowDispatchEvent<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, StringNode nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_dispatch must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowDispatchEvent { EventName = nameNode, Inputs = null, Range = nameNode.Range };
        }

        Dictionary<Utf8String, DispatchInput>? inputs = null;
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
                inputs = ParseWorkflowDispatchInputs(ref reader, diagnostics, source);
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

        return new WorkflowDispatchEvent { EventName = nameNode, Inputs = inputs, Range = nameNode.Range };
    }

    private static Dictionary<Utf8String, DispatchInput>? ParseWorkflowDispatchInputs<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_dispatch.inputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new Dictionary<Utf8String, DispatchInput>();
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
            var key = Utf8String.FromLowerAscii(idUtf8);
            var nameNode = new StringNode { Value = idSlice, Quoted = reader.IsScalarQuoted(), Range = idRange };
            reader.Read(); // consume input id

            var input = ParseWorkflowDispatchInput(ref reader, diagnostics, nameNode);
            map[key] = input;
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return map;
    }

    private static DispatchInput ParseWorkflowDispatchInput<TReader>(ref TReader reader, List<Diagnostic> diagnostics, StringNode nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNode? description = null;
        BoolNode? required = null;
        StringNode? defaultValue = null;
        DispatchInputType type = DispatchInputType.None;
        StringNode[]? options = null;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_dispatch input must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new DispatchInput { Name = nameNode, Description = description, Required = required, Default = defaultValue, Type = type, Options = options, Range = nameNode.Range };
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
                description = ParseString(ref reader, diagnostics, "on.workflow_dispatch input description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("required"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.workflow_dispatch input contains duplicate key: required", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                required = ParseBoolNode(ref reader, diagnostics, "on.workflow_dispatch input required must be bool");
                continue;
            }

            if (keyUtf8.SequenceEqual("default"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, "on.workflow_dispatch input contains duplicate key: default", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                defaultValue = ParseString(ref reader, diagnostics, "on.workflow_dispatch input default must be scalar", allowEmpty: true);
                continue;
            }

            if (keyUtf8.SequenceEqual("type"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 3)) { AddError(diagnostics, "on.workflow_dispatch input contains duplicate key: type", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                type = ParseDispatchInputType(ref reader, diagnostics);
                continue;
            }

            if (keyUtf8.SequenceEqual("options"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 4)) { AddError(diagnostics, "on.workflow_dispatch input contains duplicate key: options", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                // allowElemEmpty: true because choice-type inputs legitimately use '' as the "no selection" option
                options = ParseStringOrStringSequence(ref reader, diagnostics, "on.workflow_dispatch input options must be scalar or sequence of scalar", allowElemEmpty: true);
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
            Range = nameNode.Range,
        };
    }

    private static DispatchInputType ParseDispatchInputType<TReader>(ref TReader reader, List<Diagnostic> diagnostics)
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

    private static BoolNode? ParseBoolNode<TReader>(ref TReader reader, List<Diagnostic> diagnostics, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        if (!TryParseBool(valueUtf8, tag, out var value))
        {
            AddError(diagnostics, errorMessage, mark);
            reader.Read();
            return null;
        }

        var node = new BoolNode
        {
            Value = value,
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };
        reader.Read();
        return node;
    }

    private static WorkflowCallEvent ParseWorkflowCallEvent<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, StringNode nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowCallEvent { EventName = nameNode, Inputs = null, Secrets = null, Outputs = null, Range = nameNode.Range };
        }

        WorkflowCallEventInput[]? inputs = null;
        Dictionary<Utf8String, WorkflowCallEventSecret>? secrets = null;
        Dictionary<Utf8String, WorkflowCallEventOutput>? outputs = null;
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
                inputs = ParseWorkflowCallInputs(ref reader, diagnostics, source);
                continue;
            }

            if (keyUtf8.SequenceEqual("secrets"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.workflow_call contains duplicate key: secrets", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                secrets = ParseWorkflowCallSecrets(ref reader, diagnostics, source);
                continue;
            }

            if (keyUtf8.SequenceEqual("outputs"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, "on.workflow_call contains duplicate key: outputs", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                outputs = ParseWorkflowCallOutputs(ref reader, diagnostics, source);
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
            Range = nameNode.Range,
        };
    }

    private static WorkflowCallEventInput[]? ParseWorkflowCallInputs<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call.inputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var list = new List<WorkflowCallEventInput>(4);
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
            var nameNode = new StringNode { Value = idSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(idMark, idUtf8.Length) };
            var idText = Encoding.UTF8.GetString(idUtf8);
            reader.Read();

            list.Add(ParseWorkflowCallInput(ref reader, diagnostics, nameNode, id, idText));
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return list.ToArray();
    }

    private static WorkflowCallEventInput ParseWorkflowCallInput<TReader>(ref TReader reader, List<Diagnostic> diagnostics, StringNode nameNode, Utf8String id, string idText)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNode? description = null;
        BoolNode? required = null;
        StringNode? defaultValue = null;
        var type = WorkflowCallInputType.Invalid;
        var hasType = false;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call input must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowCallEventInput { Name = nameNode, Id = id, Description = description, Required = required, Default = defaultValue, Type = type, Range = nameNode.Range };
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
                description = ParseString(ref reader, diagnostics, "on.workflow_call input description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("required"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.workflow_call input contains duplicate key: required", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                required = ParseBoolNode(ref reader, diagnostics, "on.workflow_call input required must be bool");
                continue;
            }

            if (keyUtf8.SequenceEqual("default"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, "on.workflow_call input contains duplicate key: default", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                defaultValue = ParseString(ref reader, diagnostics, "on.workflow_call input default must be scalar", allowEmpty: true);
                continue;
            }

            if (keyUtf8.SequenceEqual("type"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 3)) { AddError(diagnostics, "on.workflow_call input contains duplicate key: type", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                type = ParseWorkflowCallInputType(ref reader, diagnostics);
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
                new TextPosition(nameNode.Range.Start, nameNode.Range.StartLine, nameNode.Range.StartColumn));
        }

        return new WorkflowCallEventInput
        {
            Name = nameNode,
            Id = id,
            Description = description,
            Required = required,
            Default = defaultValue,
            Type = type,
            Range = nameNode.Range,
        };
    }

    private static WorkflowCallInputType ParseWorkflowCallInputType<TReader>(ref TReader reader, List<Diagnostic> diagnostics)
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

    private static Dictionary<Utf8String, WorkflowCallEventSecret>? ParseWorkflowCallSecrets<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call.secrets must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new Dictionary<Utf8String, WorkflowCallEventSecret>();
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

            var key = Utf8String.FromLowerAscii(idUtf8);
            var nameNode = new StringNode { Value = idSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(idMark, idUtf8.Length) };
            reader.Read();

            map[key] = ParseWorkflowCallSecret(ref reader, diagnostics, nameNode);
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return map;
    }

    private static WorkflowCallEventSecret ParseWorkflowCallSecret<TReader>(ref TReader reader, List<Diagnostic> diagnostics, StringNode nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNode? description = null;
        BoolNode? required = null;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call secret must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowCallEventSecret { Name = nameNode, Description = description, Required = required, Range = nameNode.Range };
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
                description = ParseString(ref reader, diagnostics, "on.workflow_call secret description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("required"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.workflow_call secret contains duplicate key: required", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                required = ParseBoolNode(ref reader, diagnostics, "on.workflow_call secret required must be bool");
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
            Range = nameNode.Range,
        };
    }

    private static Dictionary<Utf8String, WorkflowCallEventOutput>? ParseWorkflowCallOutputs<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call.outputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new Dictionary<Utf8String, WorkflowCallEventOutput>();
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

            var key = Utf8String.FromLowerAscii(idUtf8);
            var nameNode = new StringNode { Value = idSlice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(idMark, idUtf8.Length) };
            var idText = Encoding.UTF8.GetString(idUtf8);
            reader.Read();

            map[key] = ParseWorkflowCallOutput(ref reader, diagnostics, nameNode, idText);
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return map;
    }

    private static WorkflowCallEventOutput ParseWorkflowCallOutput<TReader>(ref TReader reader, List<Diagnostic> diagnostics, StringNode nameNode, string idText)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNode? description = null;
        StringNode? value = null;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.workflow_call output must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new WorkflowCallEventOutput { Name = nameNode, Description = description, Value = value, Range = nameNode.Range };
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
                description = ParseString(ref reader, diagnostics, "on.workflow_call output description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("value"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.workflow_call output contains duplicate key: value", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                value = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
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
        if (value is null)
        {
            AddError(
                diagnostics,
                $"on.workflow_call.outputs.{idText}.value is required",
                new TextPosition(nameNode.Range.Start, nameNode.Range.StartLine, nameNode.Range.StartColumn));
        }

        return new WorkflowCallEventOutput
        {
            Name = nameNode,
            Description = description,
            Value = value,
            Range = nameNode.Range,
        };
    }

    private static RepositoryDispatchEvent ParseRepositoryDispatchEvent<TReader>(ref TReader reader, List<Diagnostic> diagnostics, in OnEventInfo eventInfo, StringNode nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.repository_dispatch must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new RepositoryDispatchEvent { EventName = nameNode, Types = null, Range = nameNode.Range };
        }

        StringNode[]? types = null;
        ulong seen = 0;
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.repository_dispatch option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "on.repository_dispatch"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("types"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "on.repository_dispatch contains duplicate key: types", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                types = ParseOnTypesNodes(ref reader, diagnostics, in eventInfo);
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"on.repository_dispatch does not support option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new RepositoryDispatchEvent { EventName = nameNode, Types = types, Range = nameNode.Range };
    }

    private static ImageVersionEvent ParseImageVersionEvent<TReader>(ref TReader reader, List<Diagnostic> diagnostics, StringNode nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.image_version must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new ImageVersionEvent { EventName = nameNode, Names = null, Versions = null, Range = nameNode.Range };
        }

        StringNode[]? names = null;
        StringNode[]? versions = null;
        ulong seen = 0;
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "on.image_version option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "on.image_version"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("names"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "on.image_version contains duplicate key: names", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                names = ParseStringSequence(ref reader, diagnostics, "on.image_version.names must be sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("versions"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.image_version contains duplicate key: versions", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                versions = ParseStringSequence(ref reader, diagnostics, "on.image_version.versions must be sequence of scalar");
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"on.image_version does not support option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new ImageVersionEvent { EventName = nameNode, Names = names, Versions = versions, Range = nameNode.Range };
    }

    private static WebhookEvent ParseWebhookEventWithOptions<TReader>(ref TReader reader, List<Diagnostic> diagnostics, in OnEventInfo eventInfo, TextPosition eventMark, StringNode nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var hasBranches = false;
        var hasBranchesIgnore = false;
        var hasTags = false;
        var hasTagsIgnore = false;
        var hasPaths = false;
        var hasPathsIgnore = false;

        StringNode[]? types = null;
        WebhookEventFilter? branches = null;
        WebhookEventFilter? branchesIgnore = null;
        WebhookEventFilter? tags = null;
        WebhookEventFilter? tagsIgnore = null;
        WebhookEventFilter? paths = null;
        WebhookEventFilter? pathsIgnore = null;
        StringNode[]? workflows = null;
        ulong seen = 0;

        reader.Read(); // consume MappingStart

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"on.{eventInfo.Name} option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd) { reader.SkipCurrentNode(); }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keySlice = reader.GetScalarSlice();
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, $"on.{eventInfo.Name}"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            // Pre-compute key identity before advancing reader (spans may be invalidated after Read)
            var isTypes = keyUtf8.SequenceEqual("types"u8);
            var isBranches = keyUtf8.SequenceEqual("branches"u8);
            var isBranchesIgnore = keyUtf8.SequenceEqual("branches-ignore"u8);
            var isTags = keyUtf8.SequenceEqual("tags"u8);
            var isTagsIgnore = keyUtf8.SequenceEqual("tags-ignore"u8);
            var isPaths = keyUtf8.SequenceEqual("paths"u8);
            var isPathsIgnore = keyUtf8.SequenceEqual("paths-ignore"u8);
            var isWorkflows = keyUtf8.SequenceEqual("workflows"u8);
            var isOptionNotAllowed = eventInfo.IsKnown && !eventInfo.Spec.IsOptionAllowed(keyUtf8);

            // Decode unknown key string while span is still valid (diagnostic path only)
            string? unknownKeyText = (!isTypes && !isBranches && !isBranchesIgnore && !isTags && !isTagsIgnore
                && !isPaths && !isPathsIgnore && !isWorkflows)
                ? Encoding.UTF8.GetString(keyUtf8)
                : null;

            reader.Read(); // consume key - after this keyUtf8 may be invalid

            if (reader.End) { break; }

            if (isTypes)
            {
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: types", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeOptionSupported())
                {
                    AddError(diagnostics, $"on.{eventInfo.Name}.types is not supported", keyMark);
                    reader.SkipCurrentNode();
                    continue;
                }

                types = ParseOnTypesNodes(ref reader, diagnostics, in eventInfo);
                continue;
            }

            if (isOptionNotAllowed)
            {
                var key = unknownKeyText ?? string.Empty;
                AddError(diagnostics, $"on.{eventInfo.Name} does not support option: {key}", keyMark);
                if (!reader.End) { reader.SkipCurrentNode(); }
                continue;
            }

            if (isBranches)
            {
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: branches", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                hasBranches = true;
                var filterNameNode = new StringNode { Value = keySlice, Quoted = false, Range = BuildScalarLocation(keyMark, "branches"u8.Length) };
                var values = ParseStringOrStringSequence(ref reader, diagnostics, out var brErr, out var brMark);
                if (brErr) AddError(diagnostics, $"on.{eventInfo.Name}.branches must be scalar or sequence of scalar", brMark);
                branches = new WebhookEventFilter { Name = filterNameNode, Values = values };
                continue;
            }

            if (isBranchesIgnore)
            {
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: branches-ignore", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                hasBranchesIgnore = true;
                var filterNameNode = new StringNode { Value = keySlice, Quoted = false, Range = BuildScalarLocation(keyMark, "branches-ignore"u8.Length) };
                var values = ParseStringOrStringSequence(ref reader, diagnostics, out var biErr, out var biMark);
                if (biErr) AddError(diagnostics, $"on.{eventInfo.Name}.branches-ignore must be scalar or sequence of scalar", biMark);
                branchesIgnore = new WebhookEventFilter { Name = filterNameNode, Values = values };
                continue;
            }

            if (isTags)
            {
                if (!TrySetBit(ref seen, 3)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: tags", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                hasTags = true;
                var filterNameNode = new StringNode { Value = keySlice, Quoted = false, Range = BuildScalarLocation(keyMark, "tags"u8.Length) };
                var values = ParseStringOrStringSequence(ref reader, diagnostics, out var tErr, out var tMark);
                if (tErr) AddError(diagnostics, $"on.{eventInfo.Name}.tags must be scalar or sequence of scalar", tMark);
                tags = new WebhookEventFilter { Name = filterNameNode, Values = values };
                continue;
            }

            if (isTagsIgnore)
            {
                if (!TrySetBit(ref seen, 4)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: tags-ignore", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                hasTagsIgnore = true;
                var filterNameNode = new StringNode { Value = keySlice, Quoted = false, Range = BuildScalarLocation(keyMark, "tags-ignore"u8.Length) };
                var values = ParseStringOrStringSequence(ref reader, diagnostics, out var tiErr, out var tiMark);
                if (tiErr) AddError(diagnostics, $"on.{eventInfo.Name}.tags-ignore must be scalar or sequence of scalar", tiMark);
                tagsIgnore = new WebhookEventFilter { Name = filterNameNode, Values = values };
                continue;
            }

            if (isPaths)
            {
                if (!TrySetBit(ref seen, 5)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: paths", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                hasPaths = true;
                var filterNameNode = new StringNode { Value = keySlice, Quoted = false, Range = BuildScalarLocation(keyMark, "paths"u8.Length) };
                var values = ParseStringOrStringSequence(ref reader, diagnostics, out var pErr, out var pMark);
                if (pErr) AddError(diagnostics, $"on.{eventInfo.Name}.paths must be scalar or sequence of scalar", pMark);
                paths = new WebhookEventFilter { Name = filterNameNode, Values = values };
                continue;
            }

            if (isPathsIgnore)
            {
                if (!TrySetBit(ref seen, 6)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: paths-ignore", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                hasPathsIgnore = true;
                var filterNameNode = new StringNode { Value = keySlice, Quoted = false, Range = BuildScalarLocation(keyMark, "paths-ignore"u8.Length) };
                var values = ParseStringOrStringSequence(ref reader, diagnostics, out var piErr, out var piMark);
                if (piErr) AddError(diagnostics, $"on.{eventInfo.Name}.paths-ignore must be scalar or sequence of scalar", piMark);
                pathsIgnore = new WebhookEventFilter { Name = filterNameNode, Values = values };
                continue;
            }

            if (isWorkflows)
            {
                if (!TrySetBit(ref seen, 7)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: workflows", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                workflows = ParseStringOrStringSequence(ref reader, diagnostics, out var wErr, out var wMark);
                if (wErr) AddError(diagnostics, $"on.{eventInfo.Name}.workflows must be scalar or sequence of scalar", wMark);
                continue;
            }

            AddError(diagnostics, $"unexpected on.{eventInfo.Name} option: {unknownKeyText}", keyMark);
            if (!reader.End) { reader.SkipCurrentNode(); }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd) { reader.Read(); }

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

        return new WebhookEvent
        {
            EventName = nameNode,
            Hook = nameNode,
            Types = types,
            Branches = branches,
            BranchesIgnore = branchesIgnore,
            Tags = tags,
            TagsIgnore = tagsIgnore,
            Paths = paths,
            PathsIgnore = pathsIgnore,
            Workflows = workflows,
            Range = nameNode.Range,
        };
    }

    private static StringNode[] ParseOnTypesNodes<TReader>(ref TReader reader, List<Diagnostic> diagnostics, in OnEventInfo eventInfo)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var mark = reader.CurrentStart;
            var slice = reader.GetScalarSlice();
            var valueUtf8 = reader.GetScalarUtf8();
            if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeAllowed(valueUtf8))
            {
                AddError(diagnostics, $"on.{eventInfo.Name}.types contains unsupported activity type: {Encoding.UTF8.GetString(valueUtf8)}", mark);
            }

            var node = new StringNode { Value = slice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(mark, valueUtf8.Length) };
            reader.Read();
            return [node];
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, $"on.{eventInfo.Name}.types must be scalar or sequence of scalar", reader.CurrentStart);
            reader.SkipCurrentNode();
            return [];
        }

        reader.Read();
        var list = new List<StringNode>(4);
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"on.{eventInfo.Name}.types must be scalar or sequence of scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            var mark = reader.CurrentStart;
            var slice = reader.GetScalarSlice();
            var valueUtf8 = reader.GetScalarUtf8();
            if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeAllowed(valueUtf8))
            {
                AddError(diagnostics, $"on.{eventInfo.Name}.types contains unsupported activity type: {Encoding.UTF8.GetString(valueUtf8)}", mark);
            }

            list.Add(new StringNode { Value = slice, Quoted = reader.IsScalarQuoted(), Range = BuildScalarLocation(mark, valueUtf8.Length) });
            reader.Read();
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd) { reader.Read(); }
        return list.ToArray();
    }

    private static StringNode[] ParseStringSequence<TReader>(ref TReader reader, List<Diagnostic> diagnostics, string errorMessage, bool allowEmpty = false, bool allowElemEmpty = false)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.End)
        {
            return [];
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return [];
        }

        var list = new List<StringNode>(4);
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            var node = ParseString(ref reader, diagnostics, errorMessage, allowElemEmpty);
            if (node is not null)
            {
                list.Add(node);
            }
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }

        if (!allowEmpty && list.Count == 0)
        {
            AddError(diagnostics, errorMessage, reader.CurrentStart);
        }

        return list.ToArray();
    }

    private static void ParseOnEventOptions<TReader>(ref TReader reader, List<Diagnostic> diagnostics, in OnEventInfo eventInfo, TextPosition eventMark)
        where TReader : IYamlStreamReader, allows ref struct
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


    private static void ValidateKnownOnEvent(in OnEventInfo eventInfo, TextPosition eventMark, List<Diagnostic> diagnostics)
    {
        if (!eventInfo.IsKnown)
        {
            AddError(diagnostics, $"unknown event in on: {eventInfo.Name}", eventMark);
        }
    }

    private static OnEventInfo ReadOnEventInfo<TReader>(ref TReader reader)
        where TReader : IYamlStreamReader, allows ref struct
    {
        try
        {
            var eventNameUtf8 = reader.GetScalarUtf8();
            if (WebhookTypes.TryGet(eventNameUtf8, out var knownEventName, out var knownSpec))
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

    private static void ParseOnTypes<TReader>(ref TReader reader, List<Diagnostic> diagnostics, in OnEventInfo eventInfo)
        where TReader : IYamlStreamReader, allows ref struct
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
