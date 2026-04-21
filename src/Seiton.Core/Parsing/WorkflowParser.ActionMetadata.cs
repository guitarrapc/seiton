using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static Dictionary<Utf8String, ActionMetadataInput>? ParseActionMetadataInputs<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "action inputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new Dictionary<Utf8String, ActionMetadataInput>();
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

            var id = Utf8String.FromLowerAscii(idUtf8);
            var nameNode = new StringNode
            {
                Value = idSlice,
                Quoted = reader.IsScalarQuoted(),
                Range = BuildScalarLocation(idMark, idUtf8.Length),
            };
            reader.Read();
            map[id] = ParseActionMetadataInput(ref reader, diagnostics, nameNode);
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return map;
    }

    private static ActionMetadataInput ParseActionMetadataInput<TReader>(ref TReader reader, List<Diagnostic> diagnostics, StringNode nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNode? description = null;
        BoolNode? required = null;
        StringNode? defaultValue = null;
        StringNode? deprecationMessage = null;
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
                Range = nameNode.Range,
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

            if (keyUtf8.SequenceEqual("description"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "action input contains duplicate key: description", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                description = ParseString(ref reader, diagnostics, "action input description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("required"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "action input contains duplicate key: required", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                required = ParseBoolNode(ref reader, diagnostics, "action input required must be bool");
                continue;
            }

            if (keyUtf8.SequenceEqual("default"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, "action input contains duplicate key: default", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                defaultValue = ParseString(ref reader, diagnostics, "action input default must be scalar", allowEmpty: true);
                continue;
            }

            if (keyUtf8.SequenceEqual("deprecationMessage"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 3)) { AddError(diagnostics, "action input contains duplicate key: deprecationMessage", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                deprecationMessage = ParseString(ref reader, diagnostics, "action input deprecationMessage must be scalar");
                continue;
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
            Range = nameNode.Range,
        };
    }

    private static Dictionary<Utf8String, ActionMetadataOutput>? ParseActionMetadataOutputs<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "action outputs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var map = new Dictionary<Utf8String, ActionMetadataOutput>();
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

            var id = Utf8String.FromLowerAscii(idUtf8);
            var nameNode = new StringNode
            {
                Value = idSlice,
                Quoted = reader.IsScalarQuoted(),
                Range = BuildScalarLocation(idMark, idUtf8.Length),
            };
            reader.Read();
            map[id] = ParseActionMetadataOutput(ref reader, diagnostics, nameNode);
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }

        return map;
    }

    private static ActionMetadataOutput ParseActionMetadataOutput<TReader>(ref TReader reader, List<Diagnostic> diagnostics, StringNode nameNode)
        where TReader : IYamlStreamReader, allows ref struct
    {
        StringNode? description = null;
        StringNode? value = null;
        ulong seen = 0;

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "action output must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new ActionMetadataOutput { Name = nameNode, Description = description, Value = value, Range = nameNode.Range };
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

            if (keyUtf8.SequenceEqual("description"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "action output contains duplicate key: description", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                description = ParseString(ref reader, diagnostics, "action output description must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("value"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "action output contains duplicate key: value", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                value = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Step,
                    "action output value must be scalar",
                    parseWholeValueIfNoEmbedded: false);
                continue;
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
            Range = nameNode.Range,
        };
    }

    private static ActionMetadataBranding? ParseActionMetadataBranding<TReader>(ref TReader reader, List<Diagnostic> diagnostics)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "action branding must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mappingStart = reader.CurrentStart;
        StringNode? icon = null;
        StringNode? color = null;
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

            if (keyUtf8.SequenceEqual("icon"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "action branding contains duplicate key: icon", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                icon = ParseString(ref reader, diagnostics, "action branding icon must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("color"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "action branding contains duplicate key: color", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                color = ParseString(ref reader, diagnostics, "action branding color must be scalar");
                continue;
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

        return new ActionMetadataBranding
        {
            Icon = icon,
            Color = color,
            Range = range,
        };
    }

    private static ActionMetadataRuns? ParseActionMetadataRuns<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, "action runs must be mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mappingStart = reader.CurrentStart;
        StringNode? usingNode = null;
        StringNode? main = null;
        StringNode? pre = null;
        StringNode? post = null;
        StringNode? preIf = null;
        StringNode? postIf = null;
        StringNode? image = null;
        StringNode? entrypoint = null;
        IReadOnlyList<StringNode>? args = null;
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

            if (keyUtf8.SequenceEqual("using"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "action runs contains duplicate key: using", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                usingNode = ParseString(ref reader, diagnostics, "action runs using must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("main"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "action runs contains duplicate key: main", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                main = ParseString(ref reader, diagnostics, "action runs main must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("pre"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, "action runs contains duplicate key: pre", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                pre = ParseString(ref reader, diagnostics, "action runs pre must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("post"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 3)) { AddError(diagnostics, "action runs contains duplicate key: post", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                post = ParseString(ref reader, diagnostics, "action runs post must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("pre-if"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 4)) { AddError(diagnostics, "action runs contains duplicate key: pre-if", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                preIf = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Step,
                    "action runs pre-if must be scalar",
                    parseWholeValueIfNoEmbedded: false);
                continue;
            }

            if (keyUtf8.SequenceEqual("post-if"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 5)) { AddError(diagnostics, "action runs contains duplicate key: post-if", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                postIf = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Step,
                    "action runs post-if must be scalar",
                    parseWholeValueIfNoEmbedded: false);
                continue;
            }

            if (keyUtf8.SequenceEqual("image"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 6)) { AddError(diagnostics, "action runs contains duplicate key: image", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                image = ParseString(ref reader, diagnostics, "action runs image must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("entrypoint"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 7)) { AddError(diagnostics, "action runs contains duplicate key: entrypoint", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                entrypoint = ParseString(ref reader, diagnostics, "action runs entrypoint must be scalar");
                continue;
            }

            if (keyUtf8.SequenceEqual("args"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 8)) { AddError(diagnostics, "action runs contains duplicate key: args", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (!reader.End)
                {
                    args = ParseActionRunsArgs(ref reader, diagnostics);
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 9)) { AddError(diagnostics, "action runs contains duplicate key: env", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (!reader.End)
                {
                    env = ParseEnvNode(
                        ref reader,
                        diagnostics,
                        source,
                        "action runs env must be mapping",
                        ExpressionValidationContext.Step);
                }

                continue;
            }

            if (keyUtf8.SequenceEqual("steps"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 10)) { AddError(diagnostics, "action runs contains duplicate key: steps", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
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
                        steps = ParseSteps(ref reader, diagnostics, source, emptyJobId);
                    }
                }

                continue;
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

    private static IReadOnlyList<StringNode>? ParseActionRunsArgs<TReader>(ref TReader reader, List<Diagnostic> diagnostics)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.SequenceStart)
        {
            var list = new List<StringNode>(4);
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
            {
                var s = ParseString(ref reader, diagnostics, "action runs args entry must be scalar");
                if (s is not null)
                {
                    list.Add(s);
                }
            }

            if (reader.CurrentKind == YamlEventKind.SequenceEnd)
            {
                reader.Read();
            }

            return list;
        }

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var single = ParseString(ref reader, diagnostics, "action runs args must be scalar or sequence");
            return single is null ? null : [single];
        }

        AddError(diagnostics, "action runs args must be scalar or sequence", reader.CurrentStart);
        reader.SkipCurrentNode();
        return null;
    }
}
