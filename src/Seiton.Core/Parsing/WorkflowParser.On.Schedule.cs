// on.schedule — scheduled event and cron entry parsing.

using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static ScheduledEvent ParseScheduleEvent<TReader>(
        ref TReader reader,
        AstArena arena,
        ref PooledBuffer<Diagnostic> diagnostics,
        StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(ref diagnostics, "on.schedule must be array", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new ScheduledEvent { EventName = nameNode, Schedules = [], Range = arena.GetStringRange(nameNode) };
        }

        var schedules = new PooledBuffer<ScheduleEntry>(2);
        try
        {
            var seqMark = reader.CurrentStart;
            reader.Read(); // consume SequenceStart

            while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
            {
                if (reader.CurrentKind != YamlEventKind.MappingStart)
                {
                    AddError(ref diagnostics, "on.schedule item must be object", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    continue;
                }

                schedules.Add(ParseScheduleEntry(ref reader, arena, ref diagnostics));
            }

            if (reader.CurrentKind == YamlEventKind.SequenceEnd)
            {
                reader.Read();
            }

            if (schedules.Count == 0)
            {
                AddError(ref diagnostics, "\"schedule\" section should not be empty", seqMark);
            }

            return new ScheduledEvent { EventName = nameNode, Schedules = schedules.ToArray(), Range = arena.GetStringRange(nameNode) };
        }
        finally { schedules.Dispose(); }
    }

    private static ScheduleEntry ParseScheduleEntry<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics)
        where TReader : IYamlStreamReader, allows ref struct
    {
        TextRange range = default;
        StringNodeId cron = default;
        StringNodeId timezone = default;
        ulong seen = 0;

        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, "on.schedule item key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "on.schedule"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<OnScheduleEntryKeyTable>(keyUtf8, out var schedKeyOrdinal))
            {
                reader.Read();
                var sk = (OnScheduleEntryMappingKey)schedKeyOrdinal;
                if (!TrySetBit(ref seen, schedKeyOrdinal))
                {
                    var dupName = sk == OnScheduleEntryMappingKey.Cron ? "cron" : "timezone";
                    AddError(ref diagnostics, $"on.schedule contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (sk)
                {
                    case OnScheduleEntryMappingKey.Cron:
                        cron = ParseString(ref reader, arena, ref diagnostics, "on.schedule.cron must be string", allowEmpty: true);
                        if (cron.HasValue)
                        {
                            range = arena.GetStringRange(cron);
                            if (arena.GetStringValue(cron).Length == 0)
                            {
                                AddError(ref diagnostics, "\"schedule\" section should not be empty", new TextPosition(range.Start, range.StartLine, range.StartColumn));
                            }
                        }

                        continue;
                    case OnScheduleEntryMappingKey.Timezone:
                        timezone = ParseString(ref reader, arena, ref diagnostics, "on.schedule.timezone must be string", allowEmpty: true);
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
            var schedSuggestion = SuggestionHelper.FindClosestFromFormattedKeys(unknown, Generated.ExpectedKeys.ScheduleEntryKeys);
            var schedMsg = schedSuggestion is not null
                ? $"on.schedule unexpected key \"{unknown}\" for \"schedule\" section. did you mean \"{schedSuggestion}\"? expected one of {Generated.ExpectedKeys.ScheduleEntryKeys}"
                : $"on.schedule unexpected key \"{unknown}\" for \"schedule\" section. expected one of {Generated.ExpectedKeys.ScheduleEntryKeys}";
            AddError(ref diagnostics, schedMsg, keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        if (!cron.HasValue)
        {
            AddError(ref diagnostics, "on.schedule item requires cron", reader.CurrentStart);
        }

        return new ScheduleEntry
        {
            Cron = cron,
            Timezone = timezone,
            Range = range,
        };
    }
}
