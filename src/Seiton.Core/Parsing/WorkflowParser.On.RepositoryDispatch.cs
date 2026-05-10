// on.repository_dispatch — types and options.

using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static RepositoryDispatchEvent ParseRepositoryDispatchEvent<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, in OnEventInfo eventInfo, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, "on.repository_dispatch must be object", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new RepositoryDispatchEvent { EventName = nameNode, Types = null, Range = arena.GetStringRange(nameNode) };
        }

        StringNodeId[]? types = null;
        ulong seen = 0;
        reader.Read(); // consume MappingStart
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, "on.repository_dispatch option key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "on.repository_dispatch"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<OnRepositoryDispatchKeyTable>(keyUtf8, out _))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(ref diagnostics, "on.repository_dispatch contains duplicate key: types", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                types = ParseOnTypesNodes(ref reader, arena, ref diagnostics, in eventInfo);
                continue;
            }

            var unknown = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(ref diagnostics, $"on.repository_dispatch unexpected key \"{unknown}\" for \"repository_dispatch\" section. expected \"types\"", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new RepositoryDispatchEvent { EventName = nameNode, Types = types, Range = arena.GetStringRange(nameNode) };
    }
}
