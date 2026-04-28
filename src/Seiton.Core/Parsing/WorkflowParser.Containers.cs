using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static Services? ParseServices<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        // spec §3.17: expression form is accepted as Services { Expression }
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var scalarMark = reader.CurrentStart;
            var expression = ParseStringAndValidateExpression(
                ref reader, arena, diagnostics,
                ExpressionValidationContext.JobServices,
                out var svcErr,
                out var svcMark,
                parseWholeValueIfNoEmbedded: false);
            if (svcErr)
            {
                AddError(diagnostics, "\"services\" section is scalar node but mapping node is expected", svcMark);
            }
            else if (expression.HasValue && !ExpressionScanHelpers.ContainsExpressionMarker(expression, arena))
            {
                // Plain scalar without expression → not a valid services value
                AddError(diagnostics, "\"services\" section is scalar node but mapping node is expected", scalarMark);
                return null;
            }
            return !expression.HasValue
                ? null
                : new Services { Expression = expression, Range = arena.GetStringRange(expression) };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' services must be object or expression", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
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
                    AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' services key must be string", reader.CurrentStart);
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

                var serviceNameNode = arena.AddString(serviceName, reader.IsScalarQuoted(), BuildScalarLocation(reader.CurrentStart, serviceNameUtf8.Length));
                reader.Read();
                if (reader.End)
                {
                    break;
                }

                var container = ParseContainerLike(ref reader, arena, diagnostics, source, jobId, serviceName, isService: true, requireImage: true, serviceMark);
                if (container is not null)
                {
                    map.Add(new SliceMap<Service>.Entry(serviceName, new Service
                    {
                        Name = serviceNameNode,
                        Container = container,
                        Range = arena.GetStringRange(serviceNameNode),
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

    private static Container? ParseContainerLike<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, Utf8Slice serviceName, bool isService, bool requireImage, TextPosition sectionKeyStart)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            // container: null / container: ~ (explicit null) → valid, means "no container".
            // container:                     (implicit empty)  → invalid, report error.
            if (reader.GetScalarTag() == ScalarTag.Null)
            {
                if (!reader.IsExplicitNull())
                {
                    var emptyContainerName = isService ? $"\"{DecodeUtf8(source, serviceName)}\" service" : "\"container\"";
                    AddError(diagnostics, $"{emptyContainerName} image should not be empty", reader.CurrentStart);
                }
                reader.Read();
                return default;
            }

            var scalarImage = ParseString(ref reader, arena, out var ctrErr, out var ctrMark);
            if (ctrErr)
            {
                if (scalarImage.HasValue)
                {
                    var emptyContainerName = isService ? $"\"{DecodeUtf8(source, serviceName)}\" service" : "\"container\"";
                    AddError(diagnostics, $"{emptyContainerName} image should not be empty", ctrMark);
                }
                else
                    AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} must be string or object", ctrMark);
            }
            if (!scalarImage.HasValue)
            {
                return default;
            }

            return new Container
            {
                Image = scalarImage,
                Range = arena.GetStringRange(scalarImage),
            };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} must be string or object", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);
        var hasImage = false;
        StringNodeId image = default;
        Credentials? credentials = null;
        Env? env = null;
        StringNodeId[]? ports = null;
        StringNodeId[]? volumes = null;
        StringNodeId options = default;
        StringNodeId entrypoint = default;
        StringNodeId command = default;
        ulong seen = 0;
        reader.Read(); // consume mapping

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} key must be string", reader.CurrentStart);
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

            if (Utf8MappingDispatch.TryMatchFirstOrdered<ContainerKeyTable>(keyUtf8, out var containerKeyOrdinal))
            {
                reader.Read();
                var ck = (ContainerMappingKey)containerKeyOrdinal;
                if (!TrySetBit(ref seen, containerKeyOrdinal))
                {
                    AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)} contains duplicate key: {ContainerDuplicateSubKey(ck)}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                if (reader.End)
                {
                    break;
                }

                switch (ck)
                {
                    case ContainerMappingKey.Image:
                        hasImage = true;
                        if (reader.CurrentKind != YamlEventKind.Scalar)
                        {
                            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.image must be string", reader.CurrentStart);
                        }

                        image = ParseString(ref reader, arena, out var imgErr, out var imgMark);
                        if (imgErr)
                        {
                            if (image.HasValue)
                            {
                                var emptyImgName = isService ? $"\"{DecodeUtf8(source, serviceName)}\" service" : "\"container\"";
                                AddError(diagnostics, $"{emptyImgName} image should not be empty", imgMark);
                            }
                            else
                                AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.image must be string", imgMark);
                        }
                        continue;
                    case ContainerMappingKey.Credentials:
                        credentials = ParseCredentials(ref reader, arena, diagnostics, source, jobId, serviceName, isService, keyMark);
                        continue;
                    case ContainerMappingKey.Env:
                        env = ParseEnvNode(
                            ref reader, arena, diagnostics,
                            source,
                            $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.env must be object or expression",
                            isService ? ExpressionValidationContext.JobServicesEnv : ExpressionValidationContext.JobContainerEnv,
                            $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.env");
                        continue;
                    case ContainerMappingKey.Ports:
                    case ContainerMappingKey.Volumes:
                    {
                        var pvKey = ck == ContainerMappingKey.Ports ? "ports" : "volumes";
                        if (reader.CurrentKind == YamlEventKind.Scalar)
                        {
                            // Ports/volumes require sequence, not scalar
                            var pvTag = reader.GetScalarTag();
                            var pvTagStr = pvTag == ScalarTag.Int ? "!!int" : "!!str";
                            AddError(diagnostics, $"\"{pvKey}\" section must be sequence node but got scalar node with \"{pvTagStr}\" tag", reader.CurrentStart);
                            reader.Read();
                        }
                        else
                        {
                            var pvValues = ParseStringOrStringSequence(ref reader, arena, diagnostics, out var pvErr, out var pvMark, allowElemEmpty: true, emptyElementMessage: $"\"container\" {pvKey} element should not be empty");
                            if (pvErr)
                            {
                                AddError(diagnostics, $"\"container\" {pvKey} element must be a string", pvMark);
                            }
                                if (ck == ContainerMappingKey.Ports)
                                {
                                    ports = pvValues;
                                }
                                else
                                {
                                    volumes = pvValues;
                                }
                        }
                        continue;
                    }
                    case ContainerMappingKey.Options:
                        if (reader.CurrentKind != YamlEventKind.Scalar)
                        {
                            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.options must be string", reader.CurrentStart);
                        }

                        options = ParseString(ref reader, arena, out var optErr, out var optMark);
                        if (optErr) AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.options must be string", optMark);
                        continue;
                    case ContainerMappingKey.Entrypoint:
                    case ContainerMappingKey.Command:
                        if (isService)
                        {
                            var svcFieldContext = ck == ContainerMappingKey.Entrypoint
                                ? ExpressionValidationContext.JobServicesEntrypoint
                                : ExpressionValidationContext.JobServicesCommand;
                            var svcField = ParseStringAndValidateExpression(
                                ref reader, arena, diagnostics,
                                svcFieldContext,
                                out var svcFieldErr, out var svcFieldMark,
                                parseWholeValueIfNoEmbedded: false);
                            if (svcFieldErr) AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.{ContainerDuplicateSubKey(ck)} must be string", svcFieldMark);
                            if (ck == ContainerMappingKey.Entrypoint)
                                entrypoint = svcField;
                            else
                                command = svcField;
                            continue;
                        }
                        // entrypoint/command are service-only keys — report as unexpected for container.
                        AddError(diagnostics, $"unexpected key \"{ContainerDuplicateSubKey(ck)}\" for \"container\" section. expected one of {Generated.ExpectedKeys.ContainerKeys}", keyMark);
                        if (!reader.End) reader.SkipCurrentNode();
                        continue;
                    default:
                        reader.SkipCurrentNode();
                        continue;
                }
            }

            var unknownKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            var containerSectionType = isService ? "services" : "container";
            var expectedKeys = isService
                ? Generated.ExpectedKeys.ServiceKeys
                : Generated.ExpectedKeys.ContainerKeys;
            AddError(diagnostics, $"unexpected key \"{unknownKey}\" for \"{containerSectionType}\" section. expected one of {expectedKeys}", keyMark);
            if (!reader.End) reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingStart, reader.CurrentEnd);
            reader.Read();
        }

        if (seen == 0)
        {
            var sectionName = isService ? $"\"{DecodeUtf8(source, serviceName)}\" service" : "\"container\"";
            AddError(diagnostics, $"{sectionName} section should not be empty. please remove this section if it's unnecessary", mappingStart);
        }

        // spec §3.16 / §12: container mapping form requires `image`
        if (requireImage && !hasImage)
        {
            var sectionType = isService ? "\"services\"" : "\"container\"";
            AddError(diagnostics, $"\"image\" is missing in {sectionType} section", sectionKeyStart);
        }

        return new Container
        {
            Image = image.HasValue ? image : arena.AddString(default, false, default),
            Credentials = credentials,
            Env = env,
            Ports = ports,
            Volumes = volumes,
            Options = options,
            Entrypoint = entrypoint,
            Command = command,
            Range = range,
        };
    }

    private static Credentials? ParseCredentials<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, Utf8Slice serviceName, bool isService, TextPosition credentialsKeyMark)
        where TReader : IYamlStreamReader, allows ref struct
    {
        // spec §3.18: expression form is accepted as Credentials { Expression }
        var credentialsContext = isService ? ExpressionValidationContext.JobServicesCredentials : ExpressionValidationContext.JobContainerCredentials;
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            if (reader.GetScalarUtf8().Length == 0)
            {
                AddError(diagnostics, "both \"username\" and \"password\" must be specified in \"credentials\" section", credentialsKeyMark);
                AddError(diagnostics, "\"credentials\" section should not be empty. please remove this section if it's unnecessary", reader.CurrentStart);
                reader.Read();
                return default;
            }

            // Non-expression scalars are not valid credentials (need mapping or ${{ }})
            if (!ContainsExpression(reader.GetScalarUtf8()))
            {
                var scalarCredMark = reader.CurrentStart;
                AddError(diagnostics, "both \"username\" and \"password\" must be specified in \"credentials\" section", credentialsKeyMark);
                AddError(diagnostics, "\"credentials\" section is scalar node but mapping node is expected", scalarCredMark);
                reader.Read();
                return default;
            }

            var expression = ParseStringAndValidateExpression(
                ref reader, arena, diagnostics,
                credentialsContext,
                out var crExprErr,
                out var crExprMark,
                parseWholeValueIfNoEmbedded: false);
            if (crExprErr) AddError(diagnostics, $"\"credentials\" section is scalar node but mapping node is expected", crExprMark);
            return !expression.HasValue
                ? null
                : new Credentials { Expression = expression, Range = arena.GetStringRange(expression) };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials must be object or expression", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);
        var hasUsername = false;
        var hasPassword = false;
        StringNodeId username = default;
        StringNodeId password = default;
        ulong seen = 0;
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials"))            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<CredentialsKeyTable>(keyUtf8, out var credKeyOrdinal))
            {
                reader.Read();
                var crk = (CredentialsMappingKey)credKeyOrdinal;
                if (!TrySetBit(ref seen, credKeyOrdinal))
                {
                    var dupName = crk == CredentialsMappingKey.Username ? "username" : "password";
                    AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (crk)
                {
                    case CredentialsMappingKey.Username:
                        hasUsername = true;
                        username = ParseStringAndValidateExpression(
                            ref reader, arena, diagnostics,
                            credentialsContext,
                            out var unErr,
                            out var unMark,
                            parseWholeValueIfNoEmbedded: false);
                        if (unErr) AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials.username must be string", unMark);
                        continue;
                    case CredentialsMappingKey.Password:
                        hasPassword = true;
                        password = ParseStringAndValidateExpression(
                            ref reader, arena, diagnostics,
                            credentialsContext,
                            out var pwErr,
                            out var pwMark,
                            parseWholeValueIfNoEmbedded: false);
                        if (pwErr) AddError(diagnostics, $"{FormatContainerSectionName(source, jobId, serviceName, isService)}.credentials.password must be string", pwMark);
                        continue;
                    default:
                        if (!reader.End)
                        {
                            reader.SkipCurrentNode();
                        }

                        continue;
                }
            }

            var unknownKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read();
            AddError(diagnostics, $"unexpected key \"{unknownKey}\" for \"credentials\" section. expected one of {Generated.ExpectedKeys.CredentialsKeys}", keyMark);
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
            AddError(diagnostics, "both \"username\" and \"password\" must be specified in \"credentials\" section", credentialsKeyMark);
        }

        return new Credentials
        {
            Username = username,
            Password = password,
            Range = range,
        };
    }

    private static void ParseStringMapping<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, string error, ExpressionValidationContext? expressionContext = null)
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
