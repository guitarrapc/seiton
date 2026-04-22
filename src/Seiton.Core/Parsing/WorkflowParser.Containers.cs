using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static Services? ParseServices<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        // spec §3.17: expression form is accepted as Services { Expression }
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var expression = ParseStringAndValidateExpression(
                ref reader,
                diagnostics,
                ExpressionValidationContext.Job,
                out var svcErr,
                out var svcMark,
                parseWholeValueIfNoEmbedded: false);
            if (svcErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' services must be mapping or expression", svcMark);
            return expression is null
                ? null
                : new Services { Expression = expression, Range = expression.Range };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' services must be mapping or expression", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);
        var map = new PooledBuffer<SliceMap<Service>.Entry>(8);
        try
        {
        Span<long> keyStore = stackalloc long[64];
        var keyCount = 0;

        reader.Read(); // consume services mapping
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' services key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var serviceName = reader.GetScalarSlice();
            var serviceNameUtf8 = reader.GetScalarUtf8();
            var serviceMark = reader.CurrentStart;
            if (!TryRegisterDynamicKey(
                source,
                serviceNameUtf8,
                serviceName.Offset,
                serviceName.Length,
                serviceMark,
                diagnostics,
                keyStore,
                ref keyCount,
                caseSensitive: false,
                "services"))
            {
                reader.Read();
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            var serviceNameNode = new StringNode
            {
                Value = serviceName,
                Quoted = reader.IsScalarQuoted(),
                Range = BuildScalarLocation(reader.CurrentStart, serviceNameUtf8.Length),
            };
            reader.Read();
            if (reader.End)
            {
                break;
            }

            var container = ParseContainerLike(ref reader, diagnostics, source, jobId, serviceName, isService: true, requireImage: true);
            if (container is not null)
            {
                map.Add(new SliceMap<Service>.Entry(serviceName, new Service
                {
                    Name = serviceNameNode,
                    Container = container,
                    Range = serviceNameNode.Range,
                }));
            }
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingStart, reader.CurrentEnd);
            reader.Read();
        }

        return new Services
        {
            ServiceMap = new SliceMap<Service>(map.ToArray(), caseSensitive: false),
            Range = range,
        };
        }
        finally { map.Dispose(); }
    }

    private static Container? ParseContainerLike<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, Utf8Slice serviceName, bool isService, bool requireImage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var scalarImage = ParseString(ref reader, out var ctrErr, out var ctrMark);
            if (ctrErr) AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} must be scalar or mapping", ctrMark);
            if (scalarImage is null)
            {
                return null;
            }

            return new Container
            {
                Image = scalarImage,
                Range = scalarImage.Range,
            };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} must be scalar or mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);
        var hasImage = false;
        StringNode? image = null;
        Credentials? credentials = null;
        Env? env = null;
        StringNode[]? ports = null;
        StringNode[]? volumes = null;
        StringNode? options = null;
        ulong seen = 0;
        reader.Read(); // consume mapping

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, FormatContainerSectionName(source, jobId, serviceName, isService)))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("image"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} contains duplicate key: image", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (reader.End)
                {
                    break;
                }

                hasImage = true;
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.image must be scalar", reader.CurrentStart);
                }
                image = ParseString(ref reader, out var imgErr, out var imgMark);
                if (imgErr) AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.image must be scalar", imgMark);
                continue;
            }

            if (keyUtf8.SequenceEqual("credentials"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} contains duplicate key: credentials", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (reader.End)
                {
                    break;
                }

                credentials = ParseCredentials(ref reader, diagnostics, source, jobId, serviceName, isService);
                continue;
            }

            if (keyUtf8.SequenceEqual("env"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} contains duplicate key: env", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (reader.End)
                {
                    break;
                }

                // spec §2.8/§14: env accepts expression form (${{ }}) or mapping
                env = ParseEnvNode(
                    ref reader,
                    diagnostics,
                    source,
                    $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.env must be mapping or expression",
                    ExpressionValidationContext.Job);
                continue;
            }

            if (keyUtf8.SequenceEqual("ports"u8) || keyUtf8.SequenceEqual("volumes"u8))
            {
                var optionKey = keyUtf8.SequenceEqual("ports"u8) ? "ports" : "volumes";
                var bit = optionKey == "ports" ? 3 : 4;
                reader.Read();
                if (!TrySetBit(ref seen, bit)) { AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} contains duplicate key: {optionKey}", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (reader.End)
                {
                    break;
                }

                var values = ParseStringOrStringSequence(ref reader, diagnostics, out var pvErr, out var pvMark);
                if (pvErr) AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.{optionKey} must be scalar or sequence of scalar", pvMark);
                if (optionKey == "ports")
                {
                    ports = values;
                }
                else
                {
                    volumes = values;
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("options"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 5)) { AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} contains duplicate key: options", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (reader.End)
                {
                    break;
                }

                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.options must be scalar", reader.CurrentStart);
                }
                options = ParseString(ref reader, out var optErr, out var optMark);
                if (optErr) AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.options must be scalar", optMark);
                continue;
            }

            var unknownKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected {FormatContainerSectionName(source, jobId, serviceName, isService)} key: {unknownKey}", keyMark);
            if (!reader.End) reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingStart, reader.CurrentEnd);
            reader.Read();
        }

        // spec §3.16 / §12: container mapping form requires `image`
        if (requireImage && !hasImage)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.image is required", new TextPosition(0, 1, 1));
        }

        return new Container
        {
            Image = image ?? new StringNode { Value = default, Quoted = false, Range = default },
            Credentials = credentials,
            Env = env,
            Ports = ports,
            Volumes = volumes,
            Options = options,
            Range = range,
        };
    }

    private static Credentials? ParseCredentials<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, Utf8Slice serviceName, bool isService)
        where TReader : IYamlStreamReader, allows ref struct
    {
        // spec §3.18: expression form is accepted as Credentials { Expression }
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var expression = ParseStringAndValidateExpression(
                ref reader,
                diagnostics,
                ExpressionValidationContext.Job,
                out var crExprErr,
                out var crExprMark,
                parseWholeValueIfNoEmbedded: false);
            if (crExprErr) AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials must be mapping or expression", crExprMark);
            return expression is null
                ? null
                : new Credentials { Expression = expression, Range = expression.Range };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials must be mapping or expression", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
        }

        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);
        var hasUsername = false;
        var hasPassword = false;
        StringNode? username = null;
        StringNode? password = null;
        ulong seen = 0;
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("username"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials contains duplicate key: username", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                hasUsername = true;
                username = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Job,
                    out var unErr,
                    out var unMark,
                    parseWholeValueIfNoEmbedded: false);
                if (unErr) AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials.username must be scalar", unMark);
                continue;
            }

            if (keyUtf8.SequenceEqual("password"u8))
            {
                reader.Read();
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials contains duplicate key: password", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                hasPassword = true;
                password = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Job,
                    out var pwErr,
                    out var pwMark,
                    parseWholeValueIfNoEmbedded: false);
                if (pwErr) AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials.password must be scalar", pwMark);
                continue;
            }

            var unknownKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected {FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials key: {unknownKey}", keyMark);
            if (!reader.End) reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingStart, reader.CurrentEnd);
            reader.Read();
        }

        // spec §3.18 / §12: credentials mapping form requires both `username` and `password`
        if (!hasUsername || !hasPassword)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials requires both username and password", new TextPosition(0, 1, 1));
        }

        return new Credentials
        {
            Username = username,
            Password = password,
            Range = range,
        };
    }

    private static void ParseStringMapping<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, string error, ExpressionValidationContext? expressionContext = null)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        Span<long> keyStore = stackalloc long[64];
        var keyCount = 0;
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
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
            if (!TryRegisterDynamicKey(
                source,
                keyUtf8,
                keySlice.Offset,
                keySlice.Length,
                keyMark,
                diagnostics,
                keyStore,
                ref keyCount,
                caseSensitive: true,
                error))
            {
                reader.Read(); // consume key
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }

                continue;
            }

            reader.Read();
            if (reader.End)
            {
                break;
            }

            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            if (expressionContext.HasValue)
            {
                var valueMark = reader.CurrentStart;
                var valueUtf8 = reader.GetScalarUtf8();
                ValidateExpressionText(
                    valueUtf8,
                    BuildScalarLocation(valueMark, valueUtf8.Length),
                    expressionContext.Value,
                    diagnostics,
                    parseWholeValueIfNoEmbedded: false);
                reader.Read();
                continue;
            }

            reader.Read();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            reader.Read();
        }
    }

}
