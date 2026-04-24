using System.Collections.Frozen;
using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static readonly FrozenSet<string> ValidBrandingColors = FrozenSet.ToFrozenSet(
    [
        "white", "black", "yellow", "blue", "green", "orange", "red", "purple", "gray-dark",
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ValidBrandingIcons = FrozenSet.ToFrozenSet(
    [
        "activity", "airplay", "alert-circle", "alert-octagon", "alert-triangle",
        "align-center", "align-justify", "align-left", "align-right", "anchor",
        "aperture", "archive", "arrow-down-circle", "arrow-down-left", "arrow-down-right",
        "arrow-down", "arrow-left-circle", "arrow-left", "arrow-right-circle", "arrow-right",
        "arrow-up-circle", "arrow-up-left", "arrow-up-right", "arrow-up", "at-sign",
        "award", "bar-chart-2", "bar-chart", "battery-charging", "battery",
        "bell-off", "bell", "bluetooth", "bold", "book-open",
        "book", "bookmark", "box", "briefcase", "calendar",
        "camera-off", "camera", "cast", "check-circle", "check-square",
        "check", "chevron-down", "chevron-left", "chevron-right", "chevron-up",
        "chevrons-down", "chevrons-left", "chevrons-right", "chevrons-up", "circle",
        "clipboard", "clock", "cloud-drizzle", "cloud-lightning", "cloud-off",
        "cloud-rain", "cloud-snow", "cloud", "code", "command",
        "compass", "copy", "corner-down-left", "corner-down-right", "corner-left-down",
        "corner-left-up", "corner-right-down", "corner-right-up", "corner-up-left", "corner-up-right",
        "cpu", "credit-card", "crop", "crosshair", "database",
        "delete", "disc", "dollar-sign", "download-cloud", "download",
        "droplet", "edit-2", "edit-3", "edit", "external-link",
        "eye-off", "eye", "fast-forward", "feather", "file-minus",
        "file-plus", "file-text", "file", "film", "filter",
        "flag", "folder-minus", "folder-plus", "folder", "gift",
        "git-branch", "git-commit", "git-merge", "git-pull-request", "globe",
        "grid", "hard-drive", "hash", "headphones", "heart",
        "help-circle", "home", "image", "inbox", "info",
        "italic", "layers", "layout", "life-buoy", "link-2",
        "link", "list", "loader", "lock", "log-in",
        "log-out", "mail", "map-pin", "map", "maximize-2",
        "maximize", "menu", "message-circle", "message-square", "mic-off",
        "mic", "minimize-2", "minimize", "minus-circle", "minus-square",
        "minus", "monitor", "moon", "more-horizontal", "more-vertical",
        "move", "music", "navigation-2", "navigation", "octagon",
        "package", "paperclip", "pause-circle", "pause", "percent",
        "phone-call", "phone-forwarded", "phone-incoming", "phone-missed", "phone-off",
        "phone-outgoing", "phone", "pie-chart", "play-circle", "play",
        "plus-circle", "plus-square", "plus", "pocket", "power",
        "printer", "radio", "refresh-ccw", "refresh-cw", "repeat",
        "rewind", "rotate-ccw", "rotate-cw", "rss", "save",
        "scissors", "search", "send", "server", "settings",
        "share-2", "share", "shield-off", "shield", "shopping-bag",
        "shopping-cart", "shuffle", "sidebar", "skip-back", "skip-forward",
        "slash", "sliders", "smartphone", "speaker", "square",
        "star", "stop-circle", "sun", "sunrise", "sunset",
        "table", "tablet", "tag", "target", "terminal",
        "thermometer", "thumbs-down", "thumbs-up", "toggle-left", "toggle-right",
        "trash-2", "trash", "trending-down", "trending-up", "triangle",
        "truck", "tv", "type", "umbrella", "underline",
        "unlock", "upload-cloud", "upload", "user-check", "user-minus",
        "user-plus", "user-x", "user", "users", "video-off",
        "video", "voicemail", "volume-1", "volume-2", "volume-x",
        "volume", "watch", "wifi-off", "wifi", "wind",
        "x-circle", "x-square", "x", "zap-off", "zap",
        "zoom-in", "zoom-out",
    ], StringComparer.OrdinalIgnoreCase);

    private static SliceMap<ActionMetadataInput>? ParseActionMetadataInputs<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "action inputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var map = new PooledBuffer<SliceMap<ActionMetadataInput>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, "action inputs key must be scalar", reader.CurrentStart);
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
                    "action inputs"))
                {
                    reader.Read();
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var nameNode = arena.AddString(idSlice, reader.IsScalarQuoted(), BuildScalarLocation(idMark, idUtf8.Length));
                reader.Read();
                map.Add(new SliceMap<ActionMetadataInput>.Entry(idSlice, ParseActionMetadataInput(ref reader, arena, diagnostics, nameNode)));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            return new SliceMap<ActionMetadataInput>(map.ToArray(), caseSensitive: false);
        }
        finally { map.Dispose(); }
    }

    private static ActionMetadataInput ParseActionMetadataInput<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNodeId description = default;
        BoolNodeId required = default;
        StringNodeId defaultValue = default;
        StringNodeId deprecationMessage = default;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "action input must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new ActionMetadataInput
            {
                Name = nameNode,
                Description = description,
                Required = required,
                Default = defaultValue,
                DeprecationMessage = deprecationMessage,
                Range = arena.GetStringRange(nameNode),
            };
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "action input option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "action input"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<ActionMetadataInputOptionKeyTable>(keyUtf8, out var inputOptOrdinal))
            {
                reader.Read();
                var iok = (ActionMetadataInputOptionKey)inputOptOrdinal;
                if (!TrySetBit(ref seen, inputOptOrdinal))
                {
                    var dupName = iok switch
                    {
                        ActionMetadataInputOptionKey.Description => "description",
                        ActionMetadataInputOptionKey.Required => "required",
                        ActionMetadataInputOptionKey.Default => "default",
                        ActionMetadataInputOptionKey.DeprecationMessage => "deprecationMessage",
                        _ => "option",
                    };
                    AddError(diagnostics, $"action input contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (iok)
                {
                    case ActionMetadataInputOptionKey.Description:
                        description = ParseString(ref reader, arena, diagnostics, "action input description must be scalar");
                        continue;
                    case ActionMetadataInputOptionKey.Required:
                        required = ParseBoolNode(ref reader, arena, diagnostics, "action input required must be bool");
                        continue;
                    case ActionMetadataInputOptionKey.Default:
                        defaultValue = ParseString(ref reader, arena, diagnostics, "action input default must be scalar", allowEmpty: true);
                        continue;
                    case ActionMetadataInputOptionKey.DeprecationMessage:
                        deprecationMessage = ParseString(ref reader, arena, diagnostics, "action input deprecationMessage must be scalar");
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
            AddError(diagnostics, $"unexpected action input option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new ActionMetadataInput
        {
            Name = nameNode,
            Description = description,
            Required = required,
            Default = defaultValue,
            DeprecationMessage = deprecationMessage,
            Range = arena.GetStringRange(nameNode),
        };
    }

    private static SliceMap<ActionMetadataOutput>? ParseActionMetadataOutputs<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "action outputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var map = new PooledBuffer<SliceMap<ActionMetadataOutput>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, "action outputs key must be scalar", reader.CurrentStart);
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
                    "action outputs"))
                {
                    reader.Read();
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var nameNode = arena.AddString(idSlice, reader.IsScalarQuoted(), BuildScalarLocation(idMark, idUtf8.Length));
                reader.Read();
                map.Add(new SliceMap<ActionMetadataOutput>.Entry(idSlice, ParseActionMetadataOutput(ref reader, arena, diagnostics, nameNode)));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            return new SliceMap<ActionMetadataOutput>(map.ToArray(), caseSensitive: false);
        }
        finally { map.Dispose(); }
    }

    private static ActionMetadataOutput ParseActionMetadataOutput<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, StringNodeId nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNodeId description = default;
        StringNodeId value = default;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "action output must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new ActionMetadataOutput { Name = nameNode, Description = description, Value = value, Range = arena.GetStringRange(nameNode) };
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "action output option key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "action output"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<ActionMetadataOutputOptionKeyTable>(keyUtf8, out var outputOptOrdinal))
            {
                reader.Read();
                var ook = (ActionMetadataOutputOptionKey)outputOptOrdinal;
                if (!TrySetBit(ref seen, outputOptOrdinal))
                {
                    var dupName = ook == ActionMetadataOutputOptionKey.Description ? "description" : "value";
                    AddError(diagnostics, $"action output contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (ook)
                {
                    case ActionMetadataOutputOptionKey.Description:
                        description = ParseString(ref reader, arena, diagnostics, "action output description must be scalar");
                        continue;
                    case ActionMetadataOutputOptionKey.Value:
                        value = ParseStringAndValidateExpression(
                            ref reader, arena, diagnostics,
                            ExpressionValidationContext.Step,
                            "action output value must be scalar",
                            parseWholeValueIfNoEmbedded: false);
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
            AddError(diagnostics, $"unexpected action output option: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return new ActionMetadataOutput
        {
            Name = nameNode,
            Description = description,
            Value = value,
            Range = arena.GetStringRange(nameNode),
        };
    }

    private static ActionMetadataBranding? ParseActionMetadataBranding<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "action branding must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var mappingStart = reader.CurrentStart;
        StringNodeId icon = default;
        StringNodeId color = default;
        ulong seen = 0;
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "action branding key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "action branding"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<ActionMetadataBrandingKeyTable>(keyUtf8, out var brandingOrdinal))
            {
                reader.Read();
                var bk = (ActionMetadataBrandingKey)brandingOrdinal;
                if (!TrySetBit(ref seen, brandingOrdinal))
                {
                    var dupName = bk == ActionMetadataBrandingKey.Icon ? "icon" : "color";
                    AddError(diagnostics, $"action branding contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (bk)
                {
                    case ActionMetadataBrandingKey.Icon:
                        icon = ParseString(ref reader, arena, diagnostics, "action branding icon must be scalar");
                        continue;
                    case ActionMetadataBrandingKey.Color:
                        color = ParseString(ref reader, arena, diagnostics, "action branding color must be scalar");
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
            AddError(diagnostics, $"unexpected action branding key: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        TextRange range = default;
        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingStart, reader.CurrentEnd);
            reader.Read();
        }

        // Validate branding color
        if (color.HasValue)
        {
            var colorValue = Encoding.UTF8.GetString(arena.GetStringValue(color));
            if (!ValidBrandingColors.Contains(colorValue))
            {
                var colorRange = arena.GetStringRange(color);
                AddError(diagnostics, $"invalid branding color \"{colorValue}\"; expected one of: white, black, yellow, blue, green, orange, red, purple, gray-dark", new TextPosition(colorRange.Start, colorRange.StartLine, colorRange.StartColumn));
            }
        }

        // Validate branding icon
        if (icon.HasValue)
        {
            var iconValue = Encoding.UTF8.GetString(arena.GetStringValue(icon));
            if (!ValidBrandingIcons.Contains(iconValue))
            {
                var iconRange = arena.GetStringRange(icon);
                AddError(diagnostics, $"invalid branding icon \"{iconValue}\"; see https://feathericons.com for valid icon names", new TextPosition(iconRange.Start, iconRange.StartLine, iconRange.StartColumn));
            }
        }

        return new ActionMetadataBranding
        {
            Icon = icon,
            Color = color,
            Range = range,
        };
    }

    private static ActionMetadataRuns? ParseActionMetadataRuns<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "action runs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var mappingStart = reader.CurrentStart;
        StringNodeId usingNode = default;
        StringNodeId main = default;
        StringNodeId pre = default;
        StringNodeId post = default;
        StringNodeId preIf = default;
        StringNodeId postIf = default;
        StringNodeId image = default;
        StringNodeId entrypoint = default;
        StringNodeId[]? args = null;
        Env? env = null;
        IReadOnlyList<Step>? steps = null;
        ulong seen = 0;
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, "action runs key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "action runs"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<ActionMetadataRunsKeyTable>(keyUtf8, out var runsKeyOrdinal))
            {
                reader.Read();
                var rk = (ActionMetadataRunsMappingKey)runsKeyOrdinal;
                if (!TrySetBit(ref seen, runsKeyOrdinal))
                {
                    AddError(diagnostics, $"action runs contains duplicate key: {ActionMetadataRunsDuplicateKeyName(rk)}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (rk)
                {
                    case ActionMetadataRunsMappingKey.Using:
                        usingNode = ParseString(ref reader, arena, diagnostics, "action runs using must be scalar");
                        continue;
                    case ActionMetadataRunsMappingKey.Main:
                        main = ParseString(ref reader, arena, diagnostics, "action runs main must be scalar");
                        continue;
                    case ActionMetadataRunsMappingKey.Pre:
                        pre = ParseString(ref reader, arena, diagnostics, "action runs pre must be scalar");
                        continue;
                    case ActionMetadataRunsMappingKey.Post:
                        post = ParseString(ref reader, arena, diagnostics, "action runs post must be scalar");
                        continue;
                    case ActionMetadataRunsMappingKey.PreIf:
                        preIf = ParseStringAndValidateExpression(
                            ref reader, arena, diagnostics,
                            ExpressionValidationContext.Step,
                            "action runs pre-if must be scalar",
                            parseWholeValueIfNoEmbedded: false);
                        continue;
                    case ActionMetadataRunsMappingKey.PostIf:
                        postIf = ParseStringAndValidateExpression(
                            ref reader, arena, diagnostics,
                            ExpressionValidationContext.Step,
                            "action runs post-if must be scalar",
                            parseWholeValueIfNoEmbedded: false);
                        continue;
                    case ActionMetadataRunsMappingKey.Image:
                        image = ParseString(ref reader, arena, diagnostics, "action runs image must be scalar");
                        continue;
                    case ActionMetadataRunsMappingKey.Entrypoint:
                        entrypoint = ParseString(ref reader, arena, diagnostics, "action runs entrypoint must be scalar");
                        continue;
                    case ActionMetadataRunsMappingKey.Args:
                        if (!reader.End)
                        {
                            args = ParseActionRunsArgs(ref reader, arena, diagnostics);
                        }

                        continue;
                    case ActionMetadataRunsMappingKey.Env:
                        if (!reader.End)
                        {
                            env = ParseEnvNode(
                                ref reader, arena, diagnostics,
                                source,
                                "action runs env must be mapping",
                                ExpressionValidationContext.Step);
                        }

                        continue;
                    case ActionMetadataRunsMappingKey.Steps:
                        if (!reader.End)
                        {
                            if (reader.CurrentKind != YamlEventKind.SequenceStart)
                            {
                                AddError(diagnostics, "action runs steps must be sequence", reader.CurrentStart);
                                reader.SkipCurrentNode();
                            }
                            else
                            {
                                Utf8Slice emptyJobId = default;
                                steps = ParseSteps(ref reader, arena, diagnostics, source, emptyJobId);
                            }
                        }

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
            AddError(diagnostics, $"unexpected action runs key: {unknown}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        TextRange range = default;
        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingStart, reader.CurrentEnd);
            reader.Read();
        }

        return new ActionMetadataRuns
        {
            Using = usingNode,
            Main = main,
            Pre = pre,
            Post = post,
            PreIf = preIf,
            PostIf = postIf,
            Image = image,
            Entrypoint = entrypoint,
            Args = args,
            Env = env,
            Steps = steps,
            Range = range,
        };
    }

    private static StringNodeId[]? ParseActionRunsArgs<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.SequenceStart)
        {
            var list = new PooledBuffer<StringNodeId>(4);
            try
            {
                reader.Read();
                while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
                {
                    var s = ParseString(ref reader, arena, diagnostics, "action runs args entry must be scalar");
                    if (s.HasValue)
                    {
                        list.Add(s);
                    }
                }

                if (reader.CurrentKind == YamlEventKind.SequenceEnd)
                {
                    reader.Read();
                }

                return list.ToArray();
            }
            finally { list.Dispose(); }
        }

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var single = ParseString(ref reader, arena, diagnostics, "action runs args must be scalar or sequence");
            return !single.HasValue ? null : [single];
        }

        AddError(diagnostics, "action runs args must be scalar or sequence", reader.CurrentStart);
        reader.SkipCurrentNode();
        return default;
    }
}
