using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    private static void ParseScalarOrScalarSequence<TReader>(ref TReader reader, List<Diagnostic> diagnostics, string error, Utf8ScalarValidator? scalarValidator = null)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (scalarValidator is null)
        {
            _ = ParseStringOrStringSequence(ref reader, diagnostics, error);
            return;
        }

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var validationError = scalarValidator(reader.GetScalarUtf8());
            if (validationError is not null)
            {
                AddError(diagnostics, validationError, reader.CurrentStart);
            }

            reader.Read();
            return;
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, error, reader.CurrentStart);
            reader.SkipCurrentNode();
            return;
        }

        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, error, reader.CurrentStart);
                reader.SkipCurrentNode();
                continue;
            }

            var validationError = scalarValidator(reader.GetScalarUtf8());
            if (validationError is not null)
            {
                AddError(diagnostics, validationError, reader.CurrentStart);
            }

            reader.Read();
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }
    }

    private static Strategy ParseStrategy<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        Matrix? matrix = null;
        BoolNode? failFast = null;
        IntNode? maxParallel = null;
        ulong seen = 0;
        var mappingStart = reader.CurrentStart;
        var range = BuildScalarLocation(mappingStart, 1);

        reader.Read(); // consume MappingStart

        while (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
        {
            if (reader.CurrentKind != YamlEventKind.Scalar)
            {
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy key must be scalar", reader.CurrentStart);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentKind != YamlEventKind.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentStart;
            var keyUtf8 = reader.GetScalarUtf8();
            if (IsMergeKey(keyUtf8, keyMark, diagnostics, "strategy"))
            {
                reader.Read();
                if (!reader.End) reader.SkipCurrentNode();
                continue;
            }

            if (keyUtf8.SequenceEqual("matrix"u8))
            {
                reader.Read(); // consume key
                if (!TrySetBit(ref seen, 0)) { AddError(diagnostics, "strategy contains duplicate key: matrix", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (reader.End)
                {
                    break;
                }

                matrix = ParseMatrix(ref reader, diagnostics, source, jobId);
                continue;
            }

            if (keyUtf8.SequenceEqual("fail-fast"u8))
            {
                reader.Read(); // consume key
                if (!TrySetBit(ref seen, 1)) { AddError(diagnostics, "strategy contains duplicate key: fail-fast", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (!reader.End)
                {
                    failFast = ParseBoolOrExpression(ref reader, diagnostics, ExpressionValidationContext.Job, out var ffErr, out var ffMark);
                    if (ffErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.fail-fast must be bool or expression", ffMark);
                }
                continue;
            }

            if (keyUtf8.SequenceEqual("max-parallel"u8))
            {
                reader.Read(); // consume key
                if (!TrySetBit(ref seen, 2)) { AddError(diagnostics, "strategy contains duplicate key: max-parallel", keyMark); if (!reader.End) reader.SkipCurrentNode(); continue; }
                if (!reader.End)
                {
                    maxParallel = ParseInt(ref reader, out var mpErr, out var mpMark);
                    if (mpErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.max-parallel must be integer", mpMark);
                    if (maxParallel is not null && maxParallel.Value <= 0)
                    {
                        AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.max-parallel must be greater than 0", keyMark);
                    }
                }
                continue;
            }

            var unknownKey = Encoding.UTF8.GetString(keyUtf8);
            reader.Read(); // consume key
            AddError(diagnostics, $"unexpected strategy key '{unknownKey}' in job '{DecodeUtf8(source, jobId)}'", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

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

    private static Matrix? ParseMatrix<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var expression = ParseStringAndValidateExpression(
                ref reader,
                diagnostics,
                ExpressionValidationContext.Job,
                out var mxExprErr,
                out var mxExprMark,
                parseWholeValueIfNoEmbedded: false);
            if (mxExprErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix must be scalar or mapping", mxExprMark);
            return new Matrix { Expression = expression, Range = expression?.Range ?? default };
        }

        if (reader.CurrentKind != YamlEventKind.MappingStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix must be scalar or mapping", reader.CurrentStart);
            reader.SkipCurrentNode();
            return null;
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
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix key must be scalar", reader.CurrentStart);
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
                diagnostics,
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

            var isInclude = keyUtf8.SequenceEqual("include"u8);
            var isExclude = keyUtf8.SequenceEqual("exclude"u8);
            reader.Read();
            if (reader.End)
            {
                break;
            }

            if (isInclude || isExclude)
            {
                if (reader.CurrentKind is not YamlEventKind.SequenceStart and not YamlEventKind.Scalar)
                {
                    var keyTextForDiagnostic = isInclude ? "include" : "exclude";
                    AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{keyTextForDiagnostic} must be sequence or scalar", reader.CurrentStart);
                }
                var combos = ParseMatrixCombinations(ref reader, diagnostics, source, jobId, isInclude ? "include" : "exclude");
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
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{keyTextForDiagnostic} must be sequence or scalar", reader.CurrentStart);
            }

            var rowName = new StringNode
            {
                Value = keySlice,
                Quoted = false,
                Range = BuildScalarLocation(keyMark, keyUtf8.Length),
            };
            StringNode? rowExpr = null;
            IReadOnlyList<RawYamlValue>? rowValues = null;
            if (reader.CurrentKind == YamlEventKind.Scalar)
            {
                var valueNode = ParseStringAndValidateExpression(
                    ref reader,
                    diagnostics,
                    ExpressionValidationContext.Job,
                    out var rowErr,
                    out var rowMark,
                    parseWholeValueIfNoEmbedded: false);
                if (rowErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{DecodeUtf8(source, keySlice)} must be sequence or scalar", rowMark);
                rowExpr = valueNode;
                rowValues = valueNode is null ? [] : [new RawYamlString { Value = valueNode }];
            }
            else if (reader.CurrentKind == YamlEventKind.SequenceStart)
            {
                rowValues = ParseRawYamlArray(ref reader, diagnostics, source, jobId, source.Slice(keySlice.Offset, keySlice.Length));
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

    private static MatrixCombinations[] ParseMatrixCombinations<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, string section)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var expr = ParseStringAndValidateExpression(
                ref reader,
                diagnostics,
                ExpressionValidationContext.Job,
                out var mcErr,
                out var mcMark,
                parseWholeValueIfNoEmbedded: false);
            if (mcErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{section} must be sequence or scalar", mcMark);
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
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{section} must be sequence or scalar", reader.CurrentStart);
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
                    AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{section} item must be mapping", reader.CurrentStart);
                    reader.SkipCurrentNode();
                    continue;
                }

                entries.Add(ParseRawYamlObject(ref reader, diagnostics, source, jobId));
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

    private static IReadOnlyList<RawYamlValue> ParseRawYamlArray<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId, ReadOnlySpan<byte> rowNameUtf8)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' strategy.matrix.{Encoding.UTF8.GetString(rowNameUtf8)} must be sequence or scalar", reader.CurrentStart);
            reader.SkipCurrentNode();
            return [];
        }

        var values = new PooledBuffer<RawYamlValue>(4);
        try
        {
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
            {
                values.Add(ParseRawYamlValue(ref reader, diagnostics, source, jobId));
            }

            if (reader.CurrentKind == YamlEventKind.SequenceEnd)
            {
                reader.Read();
            }

            return values.ToArray();
        }
        finally { values.Dispose(); }
    }

    private static RawYamlValue ParseRawYamlValue<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var node = ParseString(ref reader, out var mvErr, out var mvMark, allowEmpty: true);
            if (mvErr) AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' matrix value must be scalar, mapping, or sequence", mvMark);
            node ??= new StringNode { Value = default, Quoted = false, Range = default };
            return new RawYamlString { Value = node };
        }

        if (reader.CurrentKind == YamlEventKind.MappingStart)
        {
            return new RawYamlObject
            {
                Properties = ParseRawYamlObject(ref reader, diagnostics, source, jobId),
            };
        }

        if (reader.CurrentKind == YamlEventKind.SequenceStart)
        {
            return new RawYamlArray
            {
                Items = ParseRawYamlArray(ref reader, diagnostics, source, jobId, "matrix"u8),
            };
        }

        AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' matrix value has unsupported shape", reader.CurrentStart);
        reader.SkipCurrentNode();
        return new RawYamlString { Value = new StringNode { Value = default, Quoted = false, Range = default } };
    }

    private static SliceMap<RawYamlValue> ParseRawYamlObject<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ReadOnlySpan<byte> source, Utf8Slice jobId)
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
                AddError(diagnostics, $"job '{DecodeUtf8(source, jobId)}' matrix object key must be scalar", reader.CurrentStart);
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

            map.Add(new SliceMap<RawYamlValue>.Entry(keySlice, ParseRawYamlValue(ref reader, diagnostics, source, jobId)));
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
