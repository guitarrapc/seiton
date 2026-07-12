// on: top-level shape (scalar | sequence | mapping), dispatch to per-event parsers, event name resolution.

using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static NodeRange ParseOnEvents<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        // Event header rows for this `on:` section are appended contiguously: each
        // per-event parser appends its payload rows first, then exactly one header row.
        var eventsFirst = arena.EventCount;

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var eventMark = reader.CurrentStart;
            var eventInfo = ReadOnEventInfo(ref reader); // try-catch inside for non-UTF8 scalars
            Utf8Slice eventSlice;
            int eventByteLen;
            try { var u = reader.GetScalarUtf8(); eventSlice = reader.GetScalarSlice(); eventByteLen = u.Length; }
            catch { eventSlice = default; eventByteLen = 0; }
            ValidateKnownOnEvent(in eventInfo, eventMark, eventSlice, ref diagnostics);
            var nameNode = arena.AddString(eventSlice, reader.IsScalarQuoted(), BuildScalarLocation(eventMark, eventByteLen));
            reader.Read();
            // spec §3.4.1: schedule requires mapping form; scalar form is an error
            if (eventInfo.IsKnown && eventInfo.Spec.Id == WebhookTypes.EventId.Schedule)
            {
                AddError(ref diagnostics, "schedule event must be configured with mapping", eventMark);
                return new NodeRange(eventsFirst, 0);
            }
            AppendSimpleEvent(arena, in eventInfo, nameNode);
            return new NodeRange(eventsFirst, 1);
        }

        if (reader.CurrentKind == YamlEventKind.SequenceStart)
        {
            reader.Read(); // consume SequenceStart
            while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(ref diagnostics, "on sequence item must be string event name", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    continue;
                }

                var eventMark = reader.CurrentStart;
                var eventInfo = ReadOnEventInfo(ref reader);
                Utf8Slice eventSlice;
                int eventByteLen;
                try { var u = reader.GetScalarUtf8(); eventSlice = reader.GetScalarSlice(); eventByteLen = u.Length; }
                catch { eventSlice = default; eventByteLen = 0; }
                ValidateKnownOnEvent(in eventInfo, eventMark, eventSlice, ref diagnostics);
                var nameNode = arena.AddString(eventSlice, reader.IsScalarQuoted(), BuildScalarLocation(eventMark, eventByteLen));
                reader.Read();
                // spec §3.4.1: schedule requires mapping form; scalar form is an error
                if (eventInfo.IsKnown && eventInfo.Spec.Id == WebhookTypes.EventId.Schedule)
                {
                    AddError(ref diagnostics, "schedule event must be configured with mapping", eventMark);
                    continue;
                }
                AppendSimpleEvent(arena, in eventInfo, nameNode);
            }

            if (reader.CurrentKind == YamlEventKind.SequenceEnd) { reader.Read(); }
            return new NodeRange(eventsFirst, arena.EventCount - eventsFirst);
        }

        if (reader.CurrentKind == YamlEventKind.MappingStart)
        {
            reader.Read(); // consume MappingStart
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(ref diagnostics, "on mapping key must be string event name", reader.CurrentStart);
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
                    ref diagnostics,
                    keyStore,
                    ref keyCount,
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
                Utf8Slice eventSlice;
                int eventByteLen;
                try { var u = reader.GetScalarUtf8(); eventSlice = reader.GetScalarSlice(); eventByteLen = u.Length; }
                catch { eventSlice = default; eventByteLen = 0; }
                ValidateKnownOnEvent(in eventInfo, eventMark, eventSlice, ref diagnostics);
                var nameNode = arena.AddString(eventSlice, reader.IsScalarQuoted(), BuildScalarLocation(eventMark, eventByteLen));
                reader.Read(); // consume event key

                if (reader.End)
                {
                    AppendSimpleEvent(arena, in eventInfo, nameNode);
                    break;
                }

                if (IsSpecialOnEvent(in eventInfo))
                {
                    ParseOnEventWithOptions(ref reader, arena, ref diagnostics, source, in eventInfo, eventMark, nameNode);
                    continue;
                }

                if (reader.CurrentKind == YamlEventKind.MappingStart)
                {
                    ParseOnEventWithOptions(ref reader, arena, ref diagnostics, source, in eventInfo, eventMark, nameNode);
                    continue;
                }

                if (reader.CurrentKind is YamlEventKind.Scalar or YamlEventKind.SequenceStart)
                {
                    // Some events have null-like / scalar options value; accept and build stub
                    reader.SkipCurrentNode();
                    AppendSimpleEvent(arena, in eventInfo, nameNode);
                    continue;
                }

                AddError(ref diagnostics, $"on.{eventInfo.Name} must be string, sequence, or mapping", reader.CurrentStart);
                reader.SkipCurrentNode();
                AppendSimpleEvent(arena, in eventInfo, nameNode);
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd) { reader.Read(); }
            return new NodeRange(eventsFirst, arena.EventCount - eventsFirst);
        }

        AddError(ref diagnostics, "on must be string, sequence, or mapping", reader.CurrentStart);
        reader.SkipCurrentNode();
        return new NodeRange(eventsFirst, 0);
    }

    private static void AppendSimpleEvent(AstArena arena, in OnEventInfo eventInfo, StringNodeId nameNode)
    {
        var range = arena.GetStringRange(nameNode);
        if (eventInfo.IsKnown)
        {
            switch (eventInfo.Spec.Id)
            {
                case WebhookTypes.EventId.Schedule:
                    arena.AddEvent(new EventData { Kind = EventKind.Scheduled, EventName = nameNode, Range = range, Payload = arena.AddScheduledEvent(default) });
                    return;
                case WebhookTypes.EventId.WorkflowDispatch:
                    arena.AddEvent(new EventData { Kind = EventKind.WorkflowDispatch, EventName = nameNode, Range = range, Payload = arena.AddWorkflowDispatchEvent(default) });
                    return;
                case WebhookTypes.EventId.WorkflowCall:
                    arena.AddEvent(new EventData { Kind = EventKind.WorkflowCall, EventName = nameNode, Range = range, Payload = arena.AddWorkflowCallEvent(default) });
                    return;
                case WebhookTypes.EventId.RepositoryDispatch:
                    arena.AddEvent(new EventData { Kind = EventKind.RepositoryDispatch, EventName = nameNode, Range = range, Payload = arena.AddRepositoryDispatchEvent(default) });
                    return;
                case WebhookTypes.EventId.ImageVersion:
                    arena.AddEvent(new EventData { Kind = EventKind.ImageVersion, EventName = nameNode, Range = range, Payload = arena.AddImageVersionEvent(default) });
                    return;
            }
        }

        arena.AddEvent(new EventData
        {
            Kind = EventKind.Webhook,
            EventName = nameNode,
            Range = range,
            Payload = arena.AddWebhookEvent(new WebhookEventData { Hook = nameNode }),
        });
    }

    private static void ParseOnEventWithOptions<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, in OnEventInfo eventInfo, TextPosition eventMark, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (!IsSpecialOnEvent(in eventInfo))
        {
            // Webhook event: build full AST with filters
            ParseWebhookEventWithOptions(ref reader, arena, ref diagnostics, in eventInfo, eventMark, nameNode);
            return;
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
                DispatchSpecialOnEvent(ref reader, arena, ref diagnostics, source, in eventInfo, nameNode);
                return;
            }

            // Allow scalar/null form such as "workflow_dispatch:" in on-mapping context.
            reader.SkipCurrentNode();
            AppendSimpleEvent(arena, in eventInfo, nameNode);
            return;
        }

        DispatchSpecialOnEvent(ref reader, arena, ref diagnostics, source, in eventInfo, nameNode);
    }

    private static void DispatchSpecialOnEvent<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, in OnEventInfo eventInfo, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        switch (eventInfo.Spec.Id)
        {
            case WebhookTypes.EventId.Schedule:
                ParseScheduleEvent(ref reader, arena, ref diagnostics, nameNode);
                return;
            case WebhookTypes.EventId.WorkflowDispatch:
                ParseWorkflowDispatchEvent(ref reader, arena, ref diagnostics, source, nameNode);
                return;
            case WebhookTypes.EventId.WorkflowCall:
                ParseWorkflowCallEvent(ref reader, arena, ref diagnostics, source, nameNode);
                return;
            case WebhookTypes.EventId.RepositoryDispatch:
                ParseRepositoryDispatchEvent(ref reader, arena, ref diagnostics, in eventInfo, nameNode);
                return;
            case WebhookTypes.EventId.ImageVersion:
                ParseImageVersionEvent(ref reader, arena, ref diagnostics, nameNode);
                return;
            default:
                AppendSimpleEvent(arena, in eventInfo, nameNode);
                return;
        }
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

    private static void ValidateKnownOnEvent(in OnEventInfo eventInfo, TextPosition eventMark, Utf8Slice eventSlice, ref PooledBuffer<Diagnostic> diagnostics)
    {
        if (!eventInfo.IsKnown)
        {
            var suggestion = SuggestionHelper.FindClosest(eventInfo.Name, WebhookTypes.AllEventNames);
            var message = suggestion is not null
                ? $"on: unknown event \"{eventInfo.Name}\". did you mean \"{suggestion}\"? see https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows for list of all event names"
                : $"on: unknown event \"{eventInfo.Name}\". see https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows for list of all event names";
            var fix = suggestion is not null && eventSlice.Length > 0
                ? new DiagnosticFix($"replace '{eventInfo.Name}' with '{suggestion}'", [new TextEdit(eventSlice.Offset, eventSlice.Length, suggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, message, eventMark, fix);
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
}
