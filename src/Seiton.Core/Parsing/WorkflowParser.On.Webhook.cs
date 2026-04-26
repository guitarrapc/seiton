// Generic webhook on.* — filters, types, branches/tags/paths, and option validation helpers.

using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static WebhookEvent ParseWebhookEventWithOptions<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, in OnEventInfo eventInfo, TextPosition eventMark, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var hasBranches = false;
        var hasBranchesIgnore = false;
        var hasTags = false;
        var hasTagsIgnore = false;
        var hasPaths = false;
        var hasPathsIgnore = false;

        StringNodeId[]? types = null;
        WebhookEventFilter? branches = null;
        WebhookEventFilter? branchesIgnore = null;
        WebhookEventFilter? tags = null;
        WebhookEventFilter? tagsIgnore = null;
        WebhookEventFilter? paths = null;
        WebhookEventFilter? pathsIgnore = null;
        StringNodeId[]? workflows = null;
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

            var knownOption = Utf8MappingDispatch.TryMatchFirstOrdered<OnWebhookEventOptionKeyTable>(keyUtf8, out var whOptOrdinal);
            var whOpt = (OnWebhookEventOptionMappingKey)whOptOrdinal;
            var isOptionNotAllowed = eventInfo.IsKnown && !eventInfo.Spec.IsOptionAllowed(keyUtf8);
            string? unknownKeyText = (!knownOption || isOptionNotAllowed) ? Encoding.UTF8.GetString(keyUtf8) : null;

            reader.Read(); // consume key - after this keyUtf8 may be invalid

            if (reader.End) { break; }

            if (knownOption && whOpt == OnWebhookEventOptionMappingKey.Types)
            {
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: types", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeOptionSupported())
                {
                    AddError(diagnostics, $"on.{eventInfo.Name}.types is not supported", keyMark);
                    reader.SkipCurrentNode();
                    continue;
                }

                types = ParseOnTypesNodes(ref reader, arena, diagnostics, in eventInfo);
                continue;
            }

            if (isOptionNotAllowed)
            {
                var key = unknownKeyText ?? string.Empty;
                AddError(diagnostics, $"on.{eventInfo.Name} does not support option: {key}", keyMark);
                if (!reader.End) { reader.SkipCurrentNode(); }
                continue;
            }

            if (knownOption)
            {
                switch (whOpt)
                {
                    case OnWebhookEventOptionMappingKey.Branches:
                        if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: branches", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                        hasBranches = true;
                        var branchesNameNode = arena.AddString(keySlice, false, BuildScalarLocation(keyMark, "branches"u8.Length));
                        var brSeqMark = reader.CurrentStart;
                        var brValues = ParseStringOrStringSequence(ref reader, arena, diagnostics, out var brErr, out var brMark);
                        if (brErr) AddError(diagnostics, $"on.{eventInfo.Name}.branches must be scalar or sequence of scalar", brMark);
                        else if (brValues.Length == 0) AddError(diagnostics, "\"branches\" section should not be empty", brSeqMark);
                        branches = new WebhookEventFilter { Name = branchesNameNode, Values = brValues };
                        continue;
                    case OnWebhookEventOptionMappingKey.BranchesIgnore:
                        if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: branches-ignore", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                        hasBranchesIgnore = true;
                        var branchesIgnoreNameNode = arena.AddString(keySlice, false, BuildScalarLocation(keyMark, "branches-ignore"u8.Length));
                        var biValues = ParseStringOrStringSequence(ref reader, arena, diagnostics, out var biErr, out var biMark);
                        if (biErr) AddError(diagnostics, $"on.{eventInfo.Name}.branches-ignore must be scalar or sequence of scalar", biMark);
                        branchesIgnore = new WebhookEventFilter { Name = branchesIgnoreNameNode, Values = biValues };
                        continue;
                    case OnWebhookEventOptionMappingKey.Tags:
                        if (!TrySetBit(ref seen, 3)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: tags", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                        hasTags = true;
                        var tagsNameNode = arena.AddString(keySlice, false, BuildScalarLocation(keyMark, "tags"u8.Length));
                        var tValues = ParseStringOrStringSequence(ref reader, arena, diagnostics, out var tErr, out var tMark);
                        if (tErr) AddError(diagnostics, $"on.{eventInfo.Name}.tags must be scalar or sequence of scalar", tMark);
                        tags = new WebhookEventFilter { Name = tagsNameNode, Values = tValues };
                        continue;
                    case OnWebhookEventOptionMappingKey.TagsIgnore:
                        if (!TrySetBit(ref seen, 4)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: tags-ignore", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                        hasTagsIgnore = true;
                        var tagsIgnoreNameNode = arena.AddString(keySlice, false, BuildScalarLocation(keyMark, "tags-ignore"u8.Length));
                        var tiValues = ParseStringOrStringSequence(ref reader, arena, diagnostics, out var tiErr, out var tiMark);
                        if (tiErr) AddError(diagnostics, $"on.{eventInfo.Name}.tags-ignore must be scalar or sequence of scalar", tiMark);
                        tagsIgnore = new WebhookEventFilter { Name = tagsIgnoreNameNode, Values = tiValues };
                        continue;
                    case OnWebhookEventOptionMappingKey.Paths:
                        if (!TrySetBit(ref seen, 5)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: paths", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                        hasPaths = true;
                        var pathsNameNode = arena.AddString(keySlice, false, BuildScalarLocation(keyMark, "paths"u8.Length));
                        var pValues = ParseStringOrStringSequence(ref reader, arena, diagnostics, out var pErr, out var pMark);
                        if (pErr) AddError(diagnostics, $"on.{eventInfo.Name}.paths must be scalar or sequence of scalar", pMark);
                        paths = new WebhookEventFilter { Name = pathsNameNode, Values = pValues };
                        continue;
                    case OnWebhookEventOptionMappingKey.PathsIgnore:
                        if (!TrySetBit(ref seen, 6)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: paths-ignore", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                        hasPathsIgnore = true;
                        var pathsIgnoreNameNode = arena.AddString(keySlice, false, BuildScalarLocation(keyMark, "paths-ignore"u8.Length));
                        var piValues = ParseStringOrStringSequence(ref reader, arena, diagnostics, out var piErr, out var piMark);
                        if (piErr) AddError(diagnostics, $"on.{eventInfo.Name}.paths-ignore must be scalar or sequence of scalar", piMark);
                        pathsIgnore = new WebhookEventFilter { Name = pathsIgnoreNameNode, Values = piValues };
                        continue;
                    case OnWebhookEventOptionMappingKey.Workflows:
                        if (!TrySetBit(ref seen, 7)) { AddError(diagnostics, $"on.{eventInfo.Name} contains duplicate key: workflows", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                        var wSeqMark = reader.CurrentStart;
                        workflows = ParseStringOrStringSequence(ref reader, arena, diagnostics, out var wErr, out var wMark);
                        if (wErr) AddError(diagnostics, $"on.{eventInfo.Name}.workflows must be scalar or sequence of scalar", wMark);
                        else if (workflows is { Length: 0 }) AddError(diagnostics, "\"workflows\" section should not be empty", wSeqMark);
                        continue;
                    default:
                        if (!reader.End) { reader.SkipCurrentNode(); }
                        continue;
                }
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
            Range = arena.GetStringRange(nameNode),
        };
    }

    private static StringNodeId[] ParseOnTypesNodes<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, in OnEventInfo eventInfo)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var slice = reader.GetScalarSlice();
            var mark = reader.ComputePositionFromOffset(slice.Offset);
            var valueUtf8 = reader.GetScalarUtf8();
            if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeAllowed(valueUtf8))
            {
                AddError(diagnostics, $"on.{eventInfo.Name}.types contains unsupported activity type: {Encoding.UTF8.GetString(valueUtf8)}", mark);
            }

            var node = arena.AddString(slice, reader.IsScalarQuoted(), BuildScalarLocation(mark, valueUtf8.Length));
            reader.Read();
            return [node];
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, $"on.{eventInfo.Name}.types must be scalar or sequence of scalar", reader.CurrentStart);
            reader.SkipCurrentNode();
            return [];
        }

        var typesSeqMark = reader.CurrentStart;
        reader.Read();
        var list = new PooledBuffer<StringNodeId>(4);
        try
        {
            while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, $"on.{eventInfo.Name}.types must be scalar or sequence of scalar", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    continue;
                }

                var slice = reader.GetScalarSlice();
                var mark = reader.ComputePositionFromOffset(slice.Offset);
                var valueUtf8 = reader.GetScalarUtf8();
                if (eventInfo.IsKnown && !eventInfo.Spec.IsTypeAllowed(valueUtf8))
                {
                    AddError(diagnostics, $"on.{eventInfo.Name}.types contains unsupported activity type: {Encoding.UTF8.GetString(valueUtf8)}", mark);
                }

                list.Add(arena.AddString(slice, reader.IsScalarQuoted(), BuildScalarLocation(mark, valueUtf8.Length)));
                reader.Read();
            }

            if (reader.CurrentKind == YamlEventKind.SequenceEnd) { reader.Read(); }

            if (list.Count == 0)
            {
                AddError(diagnostics, "\"types\" section should not be empty", typesSeqMark);
            }

            return list.ToArray();
        }
        finally { list.Dispose(); }
    }

    private static StringNodeId[] ParseStringSequence<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, string errorMessage, bool allowEmpty = false, bool allowElemEmpty = false)
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

        var list = new PooledBuffer<StringNodeId>(4);
        try
        {
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
            {
                var node = ParseString(ref reader, arena, diagnostics, errorMessage, allowElemEmpty);
                if (node.HasValue)
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
        finally { list.Dispose(); }
    }

    private static void ParseOnEventOptions<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, in OnEventInfo eventInfo, TextPosition eventMark)
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
            var extMatch = Utf8MappingDispatch.TryMatchFirstOrdered<OnEventOptionsExtendedKeyTable>(keyUtf8, out var extOrd);
            var extKey = (OnEventOptionsExtendedMappingKey)extOrd;

            if (extMatch && extKey == OnEventOptionsExtendedMappingKey.Types)
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

                ParseOnTypes(ref reader, arena, diagnostics, in eventInfo);
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

            if (extMatch)
            {
                switch (extKey)
                {
                    case OnEventOptionsExtendedMappingKey.Types:
                        reader.Read();
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                    case OnEventOptionsExtendedMappingKey.Branches:
                        reader.Read();
                        hasBranches = true;
                        ParseScalarOrScalarSequence(ref reader, arena, diagnostics, $"on.{eventInfo.Name}.branches must be scalar or sequence of scalar");
                        continue;
                    case OnEventOptionsExtendedMappingKey.BranchesIgnore:
                        reader.Read();
                        hasBranchesIgnore = true;
                        ParseScalarOrScalarSequence(ref reader, arena, diagnostics, $"on.{eventInfo.Name}.branches-ignore must be scalar or sequence of scalar");
                        continue;
                    case OnEventOptionsExtendedMappingKey.Tags:
                        reader.Read();
                        hasTags = true;
                        ParseScalarOrScalarSequence(ref reader, arena, diagnostics, $"on.{eventInfo.Name}.tags must be scalar or sequence of scalar");
                        continue;
                    case OnEventOptionsExtendedMappingKey.TagsIgnore:
                        reader.Read();
                        hasTagsIgnore = true;
                        ParseScalarOrScalarSequence(ref reader, arena, diagnostics, $"on.{eventInfo.Name}.tags-ignore must be scalar or sequence of scalar");
                        continue;
                    case OnEventOptionsExtendedMappingKey.Paths:
                        reader.Read();
                        hasPaths = true;
                        ParseScalarOrScalarSequence(ref reader, arena, diagnostics, $"on.{eventInfo.Name}.paths must be scalar or sequence of scalar");
                        continue;
                    case OnEventOptionsExtendedMappingKey.PathsIgnore:
                        reader.Read();
                        hasPathsIgnore = true;
                        ParseScalarOrScalarSequence(ref reader, arena, diagnostics, $"on.{eventInfo.Name}.paths-ignore must be scalar or sequence of scalar");
                        continue;
                    case OnEventOptionsExtendedMappingKey.Workflows:
                        reader.Read();
                        ParseScalarOrScalarSequence(ref reader, arena, diagnostics, $"on.{eventInfo.Name}.workflows must be scalar or sequence of scalar");
                        continue;
                    case OnEventOptionsExtendedMappingKey.Inputs:
                    case OnEventOptionsExtendedMappingKey.Secrets:
                    case OnEventOptionsExtendedMappingKey.Outputs:
                        reader.Read();
                        var iosName = extKey == OnEventOptionsExtendedMappingKey.Inputs
                            ? "inputs"
                            : extKey == OnEventOptionsExtendedMappingKey.Secrets
                                ? "secrets"
                                : "outputs";
                        if (reader.CurrentKind != YamlEventKind.MappingStart)
                        {
                            AddError(diagnostics, $"on.{eventInfo.Name}.{iosName} must be mapping", reader.CurrentStart);
                        }

                        reader.SkipCurrentNode();
                        continue;
                    default:
                        reader.Read();
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                }
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

    private static void ParseOnTypes<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, in OnEventInfo eventInfo)
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
