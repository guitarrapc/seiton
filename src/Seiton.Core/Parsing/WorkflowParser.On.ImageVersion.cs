// on.image_version — names and versions lists.

using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static readonly string[] ImageVersionOptionNames = ["names", "versions"];

    private static ImageVersionEvent ParseImageVersionEvent<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, "on.image_version must be object", reader.CurrentStart);
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
                AddError(ref diagnostics, "on.image_version option key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var keyMark = reader.CurrentStart;
            var keySlice = reader.GetScalarSlice();
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "on.image_version"))
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
                    AddError(ref diagnostics, $"on.image_version contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (ivk)
                {
                    case OnImageVersionMappingKey.Names:
                        names = ParseStringSequence(ref reader, arena, ref diagnostics, "on.image_version.names must be array of strings", emptyMessage: "on.image_version.names should not be empty", emptyElementMessage: "\"names\" filter value should not be empty");
                        continue;
                    case OnImageVersionMappingKey.Versions:
                        versions = ParseStringSequence(ref reader, arena, ref diagnostics, "on.image_version.versions must be array of strings", emptyMessage: "on.image_version.versions should not be empty", emptyElementMessage: "\"versions\" filter value should not be empty");
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
            var suggestion = SuggestionHelper.FindClosest(unknown, ImageVersionOptionNames);
            var expectedList = SuggestionHelper.FormatExpectedOptions(ImageVersionOptionNames);
            var message = suggestion is not null
                ? $"on.image_version has unexpected key \"{unknown}\" for \"image_version\" section. did you mean \"{suggestion}\"? expected one of {expectedList}"
                : $"on.image_version has unexpected key \"{unknown}\" for \"image_version\" section. expected one of {expectedList}";
            var fix = suggestion is not null
                ? new DiagnosticFix($"replace '{unknown}' with '{suggestion}'", [new TextEdit(keySlice.Offset, keySlice.Length, suggestion)])
                : (DiagnosticFix?)null;
            AddError(ref diagnostics, message, keyMark, fix);
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
