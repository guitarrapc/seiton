// on: top-level shape (scalar | sequence | mapping), dispatch to per-event parsers, event name resolution.

using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static Event[] ParseOnEvents<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
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
            var nameNode = arena.AddString(eventSlice, reader.IsScalarQuoted(), BuildScalarLocation(eventMark, eventByteLen));
            reader.Read();
            // spec §3.4.1: schedule requires mapping form; scalar form is an error
            if (eventInfo.IsKnown && eventInfo.Spec.Id == WebhookTypes.EventId.Schedule)
            {
                AddError(diagnostics, "schedule event must be configured with mapping", eventMark);
                return [];
            }
            return [BuildSimpleEvent(arena, in eventInfo, nameNode)];
        }

        if (reader.CurrentKind == YamlEventKind.SequenceStart)
        {
            reader.Read(); // consume SequenceStart
            var events = new PooledBuffer<Event>(4);
            try
            {
                while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
                {
                    if (reader.CurrentKind != YamlEventKind.Scalar)
                    {
                        AddError(diagnostics, "on sequence item must be string event name", reader.CurrentStart);
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
                    var nameNode = arena.AddString(eventSlice, reader.IsScalarQuoted(), BuildScalarLocation(eventMark, eventByteLen));
                    reader.Read();
                    // spec §3.4.1: schedule requires mapping form; scalar form is an error
                    if (eventInfo.IsKnown && eventInfo.Spec.Id == WebhookTypes.EventId.Schedule)
                    {
                        AddError(diagnostics, "schedule event must be configured with mapping", eventMark);
                        continue;
                    }
                    events.Add(BuildSimpleEvent(arena, in eventInfo, nameNode));
                }

                if (reader.CurrentKind == YamlEventKind.SequenceEnd) { reader.Read(); }
                return events.ToArray();
            }
            finally { events.Dispose(); }
        }

        if (reader.CurrentKind == YamlEventKind.MappingStart)
        {
            reader.Read(); // consume MappingStart
            var events = new PooledBuffer<Event>(4);
            try
            {
                Span<long> keyStore = stackalloc long[64];
                var keyCount = 0;
                while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    if (reader.CurrentKind != YamlEventKind.Scalar)
                    {
                        AddError(diagnostics, "on mapping key must be string event name", reader.CurrentStart);
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
                    var nameNode = arena.AddString(eventSlice, reader.IsScalarQuoted(), BuildScalarLocation(eventMark, eventByteLen));
                    reader.Read(); // consume event key

                    if (reader.End)
                    {
                        events.Add(BuildSimpleEvent(arena, in eventInfo, nameNode));
                        break;
                    }

                    if (IsSpecialOnEvent(in eventInfo))
                    {
                        events.Add(ParseOnEventWithOptions(ref reader, arena, diagnostics, source, in eventInfo, eventMark, nameNode));
                        continue;
                    }

                    if (reader.CurrentKind == YamlEventKind.MappingStart)
                    {
                        events.Add(ParseOnEventWithOptions(ref reader, arena, diagnostics, source, in eventInfo, eventMark, nameNode));
                        continue;
                    }

                    if (reader.CurrentKind is YamlEventKind.Scalar or YamlEventKind.SequenceStart)
                    {
                        // Some events have null-like / scalar options value; accept and build stub
                        reader.SkipCurrentNode();
                        events.Add(BuildSimpleEvent(arena, in eventInfo, nameNode));
                        continue;
                    }

                    AddError(diagnostics, $"on.{eventInfo.Name} must be string, sequence, or mapping", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    events.Add(BuildSimpleEvent(arena, in eventInfo, nameNode));
                }

                if (reader.CurrentKind == YamlEventKind.MappingEnd) { reader.Read(); }
                return events.ToArray();
            }
            finally { events.Dispose(); }
        }

        AddError(diagnostics, "on must be string, sequence, or mapping", reader.CurrentStart);
        reader.SkipCurrentNode();
        return [];
    }

    private static Event BuildSimpleEvent(AstArena arena, in OnEventInfo eventInfo, StringNodeId nameNode)
    {
        if (eventInfo.IsKnown)
        {
            return eventInfo.Spec.Id switch
            {
                WebhookTypes.EventId.Schedule => new ScheduledEvent { EventName = nameNode, Range = arena.GetStringRange(nameNode) },
                WebhookTypes.EventId.WorkflowDispatch => new WorkflowDispatchEvent { EventName = nameNode, Range = arena.GetStringRange(nameNode) },
                WebhookTypes.EventId.WorkflowCall => new WorkflowCallEvent { EventName = nameNode, Range = arena.GetStringRange(nameNode) },
                WebhookTypes.EventId.RepositoryDispatch => new RepositoryDispatchEvent { EventName = nameNode, Range = arena.GetStringRange(nameNode) },
                WebhookTypes.EventId.ImageVersion => new ImageVersionEvent { EventName = nameNode, Range = arena.GetStringRange(nameNode) },
                _ => new WebhookEvent { EventName = nameNode, Hook = nameNode, Range = arena.GetStringRange(nameNode) },
            };
        }

        return new WebhookEvent { EventName = nameNode, Hook = nameNode, Range = arena.GetStringRange(nameNode) };
    }

    private static Event ParseOnEventWithOptions<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, in OnEventInfo eventInfo, TextPosition eventMark, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (!IsSpecialOnEvent(in eventInfo))
        {
            // Webhook event: build full AST with filters
            return ParseWebhookEventWithOptions(ref reader, arena, diagnostics, in eventInfo, eventMark, nameNode);
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
                    WebhookTypes.EventId.Schedule => ParseScheduleEvent(ref reader, arena, diagnostics, nameNode),
                    WebhookTypes.EventId.WorkflowDispatch => ParseWorkflowDispatchEvent(ref reader, arena, diagnostics, source, nameNode),
                    WebhookTypes.EventId.WorkflowCall => ParseWorkflowCallEvent(ref reader, arena, diagnostics, source, nameNode),
                    WebhookTypes.EventId.RepositoryDispatch => ParseRepositoryDispatchEvent(ref reader, arena, diagnostics, in eventInfo, nameNode),
                    WebhookTypes.EventId.ImageVersion => ParseImageVersionEvent(ref reader, arena, diagnostics, nameNode),
                    _ => BuildSimpleEvent(arena, in eventInfo, nameNode),
                };
            }

            // Allow scalar/null form such as "workflow_dispatch:" in on-mapping context.
            reader.SkipCurrentNode();
            return BuildSimpleEvent(arena, in eventInfo, nameNode);
        }

        return eventInfo.Spec.Id switch
        {
            WebhookTypes.EventId.Schedule => ParseScheduleEvent(ref reader, arena, diagnostics, nameNode),
            WebhookTypes.EventId.WorkflowDispatch => ParseWorkflowDispatchEvent(ref reader, arena, diagnostics, source, nameNode),
            WebhookTypes.EventId.WorkflowCall => ParseWorkflowCallEvent(ref reader, arena, diagnostics, source, nameNode),
            WebhookTypes.EventId.RepositoryDispatch => ParseRepositoryDispatchEvent(ref reader, arena, diagnostics, in eventInfo, nameNode),
            WebhookTypes.EventId.ImageVersion => ParseImageVersionEvent(ref reader, arena, diagnostics, nameNode),
            _ => BuildSimpleEvent(arena, in eventInfo, nameNode),
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

    private static void ValidateKnownOnEvent(in OnEventInfo eventInfo, TextPosition eventMark, List<Diagnostic> diagnostics)
    {
        if (!eventInfo.IsKnown)
        {
            AddError(diagnostics, $"unknown event \"{eventInfo.Name}\". see https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows for list of all event names", eventMark);
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
