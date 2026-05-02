using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static void ParseScalarOrScalarSequence<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, string error, Utf8ScalarValidator? scalarValidator = null)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (scalarValidator is null)
        {
            _ = ParseStringOrStringSequence(ref reader, arena, ref diagnostics, error);
            return;
        }

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var validationError = scalarValidator(reader.GetScalarUtf8());
            if (validationError is not null)
            {
                AddError(ref diagnostics, validationError, reader.CurrentStart);
            }

            reader.Read();
            return;
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(ref diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            var validationError = scalarValidator(reader.GetScalarUtf8());
            if (validationError is not null)
            {
                AddError(ref diagnostics, validationError, reader.CurrentStart);
            }

            reader.Read();
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }
    }

    private static Strategy ParseStrategy<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        Matrix? matrix = null;
        BoolNodeId failFast = default;
        IntNodeId maxParallel = default;
        ulong seen = 0;
        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);

        reader.Read(); // consume MappingStart

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy key must be string", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, ref diagnostics, "strategy"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (Utf8MappingDispatch.TryMatchFirstOrdered<StrategyKeyTable>(keyUtf8, out var strategyKeyOrdinal))
            {
                reader.Read(); // consume key
                var sk = (StrategyMappingKey)strategyKeyOrdinal;
                if (!TrySetBit(ref seen, strategyKeyOrdinal))
                {
                    var dupName = sk == StrategyMappingKey.Matrix ? "matrix" : sk == StrategyMappingKey.FailFast ? "fail-fast" : "max-parallel";
                    AddError(ref diagnostics, $"strategy contains duplicate key: {dupName}", keyMark);
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                switch (sk)
                {
                    case StrategyMappingKey.Matrix:
                        if (reader.End)
                        {
                            goto strategy_mapping_done;
                        }

                        matrix = ParseMatrix(ref reader, arena, ref diagnostics, source, jobId);
                        continue;
                    case StrategyMappingKey.FailFast:
                        if (!reader.End)
                        {
                            failFast = ParseBoolOrExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobStrategy, out var ffErr, out var ffMark);
                            if (ffErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.fail-fast must be bool or expression", ffMark);
                        }

                        continue;
                    case StrategyMappingKey.MaxParallel:
                        if (!reader.End)
                        {
                            maxParallel = ParseIntOrExpression(ref reader, arena, ref diagnostics, ExpressionValidationContext.JobStrategy, out var mpErr, out var mpMark);
                            if (mpErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.max-parallel must be integer", mpMark);
                            if (maxParallel.HasValue && arena.GetIntExpression(maxParallel) == default && arena.GetIntValue(maxParallel) <= 0)
                            {
                                AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.max-parallel must be greater than 0", keyMark);
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

            var unknownKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read(); // consume key
            AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy unexpected key \"{unknownKey}\" for \"strategy\" section. expected one of {Generated.ExpectedKeys.StrategyKeys}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

    strategy_mapping_done:
        if (reader.CurrentKind == YamlEventKind.MappingEnd)
        {
            range = BuildCompositeLocation(mappingStart, reader.CurrentEnd);
            reader.Read();
        }

        return new Strategy
        {
            Matrix = matrix,
            FailFast = failFast,
            MaxParallel = maxParallel,
            Range = range,
        };
    }

    private static Matrix? ParseMatrix<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var expression = ParseStringAndValidateExpression(
                ref reader, arena, ref diagnostics,
                ExpressionValidationContext.JobStrategy,
                out var mxExprErr,
                out var mxExprMark,
                parseWholeValueIfNoEmbedded: false);
            if (mxExprErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix must be string or object", mxExprMark);
            return new Matrix { Expression = expression, Range = expression.HasValue ? arena.GetStringRange(expression) : default };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix must be string or object", reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);
        MatrixCombinations[]? include = null;
        MatrixCombinations[]? exclude = null;
        var rowBuffer = new PooledBuffer<SliceMap<MatrixRow>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;

            reader.Read(); // consume matrix mapping
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix key must be string", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                    {
                        reader.SkipCurrentNode();
                    }
                    continue;
                }

                var keyUtf8 = reader.GetScalarUtf8();
                var keySlice = reader.GetScalarSlice();
                var keyMark = reader.CurrentStart;
                if (!TryRegisterDynamicKey(
                    source,
                    keyUtf8,
                    keySlice.Offset,
                    keySlice.Length,
                    keyMark,
                    ref diagnostics,
                    keyStore,
                    ref keyCount,
                    caseSensitive: false,
                    "strategy.matrix"))
                {
                    reader.Read();
                    if (!reader.End)
                    {
                        reader.SkipCurrentNode();
                    }

                    continue;
                }

                var isInclude = false;
                var isExclude = false;
                if (Utf8MappingDispatch.TryMatchFirstOrdered<MatrixIncludeExcludeKeyTable>(keyUtf8, out var incExcOrdinal))
                {
                    isExclude = incExcOrdinal == 0;
                    isInclude = incExcOrdinal == 1;
                }

                reader.Read();
                if (reader.End)
                {
                    break;
                }

                if (isInclude || isExclude)
                {
                    var incExcKeyText = isInclude ? "include" : "exclude";
                    if (reader.CurrentKind is not YamlEventKind.SequenceStart and not YamlEventKind.Scalar)
                    {
                        AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix.{incExcKeyText} must be array or string", reader.CurrentStart);
                    }
                    var incExcSeqMark = reader.CurrentStart;
                    var combos = ParseMatrixCombinations(ref reader, arena, ref diagnostics, source, jobId, incExcKeyText);
                    if (combos.Length > 0 && combos[0].Entries is { Count: 0 } && combos[0].Expression == default)
                    {
                        AddError(ref diagnostics, $"\"{incExcKeyText}\" section should not be empty", incExcSeqMark);
                    }
                    if (isInclude)
                    {
                        include = combos;
                    }
                    else
                    {
                        exclude = combos;
                    }
                    continue;
                }

                if (reader.CurrentKind is not YamlEventKind.SequenceStart and not YamlEventKind.Scalar)
                {
                    var keyTextForDiagnostic = DecodeUtf8(source, keySlice);
                    AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix.{keyTextForDiagnostic} must be array or string", reader.CurrentStart);
                }

                var rowName = arena.AddString(keySlice, false, BuildScalarLocation(keyMark, keyUtf8.Length));
                StringNodeId rowExpr = default;
                IReadOnlyList<RawYamlValue>? rowValues = null;
                if (reader.CurrentKind == YamlEventKind.Scalar)
                {
                    var valueNode = ParseStringAndValidateExpression(
                        ref reader, arena, ref diagnostics,
                        ExpressionValidationContext.JobStrategy,
                        out var rowErr,
                        out var rowMark,
                        parseWholeValueIfNoEmbedded: false);
                    if (rowErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix.{DecodeUtf8(source, keySlice)} must be array or string", rowMark);
                    rowExpr = valueNode;
                    rowValues = !valueNode.HasValue ? [] : [new RawYamlString { Value = valueNode }];
                }
                else if (reader.CurrentKind == YamlEventKind.SequenceStart)
                {
                    var matrixRowSeqMark = reader.CurrentStart;
                    rowValues = ParseRawYamlArray(ref reader, arena, ref diagnostics, source, jobId, source.Slice(keySlice.Offset, keySlice.Length), ExpressionValidationContext.JobStrategy);
                    if (rowValues.Count == 0)
                    {
                        AddError(ref diagnostics, "\"matrix values\" section should not be empty", matrixRowSeqMark);
                    }
                }
                else
                {
                    reader.SkipCurrentNode();
                }

                rowBuffer.Add(new SliceMap<MatrixRow>.Entry(keySlice, new MatrixRow
                {
                    Name = rowName,
                    Expression = rowExpr,
                    Values = rowValues,
                }));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                range = BuildCompositeLocation(mappingStart, reader.CurrentEnd);
                reader.Read();
            }

            return new Matrix
            {
                Include = include,
                Exclude = exclude,
                Rows = rowBuffer.Count > 0 ? new SliceMap<MatrixRow>(rowBuffer.ToArray(), caseSensitive: false) : null,
                Range = range,
            };
        }
        finally { rowBuffer.Dispose(); }
    }

    private static MatrixCombinations[] ParseMatrixCombinations<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, string section)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var expr = ParseStringAndValidateExpression(
                ref reader, arena, ref diagnostics,
                ExpressionValidationContext.JobStrategy,
                out var mcErr,
                out var mcMark,
                parseWholeValueIfNoEmbedded: false);
            if (mcErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix.{section} must be array or string", mcMark);
            return
            [
                new MatrixCombinations
                {
                    Expression = expr,
                    Entries = null,
                }
            ];
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix.{section} must be array or string", reader.CurrentStart);
            reader.SkipCurrentNode();
            return [];
        }

        var entries = new PooledBuffer<SliceMap<RawYamlValue>>(4);
        try
        {
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
            {
                if (reader.CurrentKind != YamlEventKind.MappingStart)
                {
                    AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix.{section} item must be object", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    continue;
                }

                entries.Add(ParseRawYamlObject(ref reader, arena, ref diagnostics, source, jobId, ExpressionValidationContext.JobStrategy));
            }

            if (reader.CurrentKind == YamlEventKind.SequenceEnd)
            {
                reader.Read();
            }

            return
            [
                new MatrixCombinations
                {
                    Entries = entries.ToArray(),
                }
            ];
        }
        finally { entries.Dispose(); }
    }

    private static IReadOnlyList<RawYamlValue> ParseRawYamlArray<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, ReadOnlySpan<byte> rowNameUtf8, ExpressionValidationContext exprContext)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix.{Encoding.UTF8.GetString(rowNameUtf8)} must be array or string", reader.CurrentStart);
            reader.SkipCurrentNode();
            return [];
        }

        var values = new PooledBuffer<RawYamlValue>(4);
        try
        {
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
            {
                values.Add(ParseRawYamlValue(ref reader, arena, ref diagnostics, source, jobId, exprContext));
            }

            if (reader.CurrentKind == YamlEventKind.SequenceEnd)
            {
                reader.Read();
            }

            return values.ToArray();
        }
        finally { values.Dispose(); }
    }

    private static RawYamlValue ParseRawYamlValue<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, ExpressionValidationContext exprContext)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var node = ParseStringAndValidateExpression(ref reader, arena, ref diagnostics, exprContext, out var mvErr, out var mvMark, parseWholeValueIfNoEmbedded: false);
            if (mvErr) AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix value must be string, object, or array", mvMark);
            if (!node.HasValue) node = arena.AddString(default, false, default);
            return new RawYamlString { Value = node };
        }

        if (reader.CurrentKind == YamlEventKind.MappingStart)
        {
            var startMark = reader.CurrentStart;
            return new RawYamlObject
            {
                Properties = ParseRawYamlObject(ref reader, arena, ref diagnostics, source, jobId, exprContext),
                Range = BuildScalarLocation(startMark, 0),
            };
        }

        if (reader.CurrentKind == YamlEventKind.SequenceStart)
        {
            var startMark = reader.CurrentStart;
            return new RawYamlArray
            {
                Items = ParseRawYamlArray(ref reader, arena, ref diagnostics, source, jobId, "matrix"u8, exprContext),
                Range = BuildScalarLocation(startMark, 0),
            };
        }

        if (reader.CurrentKind == YamlEventKind.Alias)
        {
            AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix unexpected alias node in value", reader.CurrentStart);
            reader.SkipCurrentNode();
            return new RawYamlString { Value = arena.AddString(default, false, default) };
        }

        AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix value has unsupported shape", reader.CurrentStart);
        reader.SkipCurrentNode();
        return new RawYamlString { Value = arena.AddString(default, false, default) };
    }

    private static SliceMap<RawYamlValue> ParseRawYamlObject<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, ExpressionValidationContext exprContext)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var map = new PooledBuffer<SliceMap<RawYamlValue>.Entry>(8);
        try
        {
            Span<long> keyStore = stackalloc long[64];
            var keyCount = 0;
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
            {
                if (reader.CurrentKind != YamlEventKind.Scalar)
                {
                    AddError(ref diagnostics, $"jobs.'{DecodeUtf8(source, jobId)}'.strategy.matrix object key must be string", reader.CurrentStart);
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
                    ref diagnostics,
                    keyStore,
                    ref keyCount,
                    caseSensitive: false,
                    "matrix object"))
                {
                    reader.Read();
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

                map.Add(new SliceMap<RawYamlValue>.Entry(keySlice, ParseRawYamlValue(ref reader, arena, ref diagnostics, source, jobId, exprContext)));
            }

            if (reader.CurrentKind == YamlEventKind.MappingEnd)
            {
                reader.Read();
            }

            return new SliceMap<RawYamlValue>(map.ToArray(), caseSensitive: false);
        }
        finally { map.Dispose(); }
    }

}
