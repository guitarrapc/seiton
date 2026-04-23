// on.image_version — names and versions lists.

using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static ImageVersionEvent ParseImageVersionEvent<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "on.image_version must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new ImageVersionEvent { EventName = nameNode, Names = null, Versions = null, Range = arena.GetStringRange(nameNode) };
        }

        StringNodeId[]? names = null;
        StringNodeId[]? versions = null;
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

            if (Utf8MappingDispatch.TryMatchFirstOrdered<OnImageVersionKeyTable>(keyUtf8, out var ivOrdinal))
            {
                reader.Read();
                var ivk = (OnImageVersionMappingKey)ivOrdinal;
                if (!TrySetBit(ref seen, ivOrdinal))
                {
                    var dupName = ivk == OnImageVersionMappingKey.Names ? "names" : "versions";
                    AddError(diagnostics, $"on.image_version contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (ivk)
                {
                    case OnImageVersionMappingKey.Names:
                        names = ParseStringSequence(ref reader, arena, diagnostics, "on.image_version.names must be sequence of scalar");
                        continue;
                    case OnImageVersionMappingKey.Versions:
                        versions = ParseStringSequence(ref reader, arena, diagnostics, "on.image_version.versions must be sequence of scalar");
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

        return new ImageVersionEvent { EventName = nameNode, Names = names, Versions = versions, Range = arena.GetStringRange(nameNode) };
    }
}
