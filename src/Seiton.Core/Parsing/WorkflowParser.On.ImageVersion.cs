// on.image_version — names and versions lists.

using System.Text;
using Seiton.Core.Generated;
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

            if (keyUtf8.SequenceEqual("names"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "on.image_version contains duplicate key: names", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                names = ParseStringSequence(ref reader, arena, diagnostics, "on.image_version.names must be sequence of scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("versions"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "on.image_version contains duplicate key: versions", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                versions = ParseStringSequence(ref reader, arena, diagnostics, "on.image_version.versions must be sequence of scalar");
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

        return new ImageVersionEvent { EventName = nameNode, Names = names, Versions = versions, Range = arena.GetStringRange(nameNode) };
    }
}
