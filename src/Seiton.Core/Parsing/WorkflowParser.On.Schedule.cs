// on.schedule — scheduled event and cron entry parsing.

using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static ScheduledEvent ParseScheduleEvent<TReader>(
        ref TReader reader,
        AstArena arena,
        List<Diagnostic> diagnostics,
        StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, "on.schedule must be sequence", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new ScheduledEvent { EventName = nameNode, Schedules = [], Range = arena.GetStringRange(nameNode) };
        }

        var schedules = new PooledBuffer<ScheduleEntry>(2);
        try
        {
            reader.Read(); // consume SequenceStart

            while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
            {
                if (reader.CurrentKind != YamlEventKind.MappingStart)
                {
                    AddError(diagnostics, "on.schedule item must be mapping", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    continue;
                }

                schedules.Add(ParseScheduleEntry(ref reader, arena, diagnostics));
            }

            if (reader.CurrentKind == YamlEventKind.SequenceEnd)
            {
                reader.Read();
            }

            return new ScheduledEvent { EventName = nameNode, Schedules = schedules.ToArray(), Range = arena.GetStringRange(nameNode) };
        }
        finally { schedules.Dispose(); }
    }

    private static ScheduleEntry ParseScheduleEntry<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics)
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
                cron = ParseString(ref reader, arena, diagnostics, "on.schedule.cron must be scalar");
                if (cron.HasValue)
                {
                    range = arena.GetStringRange(cron);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("timezone"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.schedule contains duplicate key: timezone", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                timezone = ParseString(ref reader, arena, diagnostics, "on.schedule.timezone must be scalar");
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

        if (!cron.HasValue)
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
}
