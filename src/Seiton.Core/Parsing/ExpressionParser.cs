using System.Runtime.CompilerServices;
using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Parsing;

/// <summary>Parses GitHub Actions expression strings (content between <c>${{</c> and <c>}}</c>) into a flat AST.</summary>
public static class ExpressionParser
{
    /// <summary>Parses a GitHub Actions expression (the content between <c>${{</c> and <c>}}</c>) into an AST.</summary>
    public static ExpressionParseResult Parse(ReadOnlySpan<byte> expressionUtf8)
    {
        using var parser = new Parser(expressionUtf8);
        var root = parser.ParseExpression();
        parser.SkipWhiteSpace();
        if (!parser.End)
        {
            parser.AddError($"unexpected token at position {parser.Position}");
        }

        return new ExpressionParseResult(
            RootNode: root,
            Nodes: parser.NodesToArray(),
            Arguments: parser.ArgumentsToArray(),
            Diagnostics: parser.DiagnosticsToArray());
    }

    /// <summary>
    /// Internal fast path: parse expression and validate semantics without allocating
    /// ExpressionNode[] / int[] arrays. Parser uses ArrayPool-backed buffers internally
    /// and passes spans directly to the semantic analyzer.
    /// </summary>
    internal static void ParseAndValidateInline(
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        ExpressionValidationContext context,
        List<Diagnostic> diagnostics,
        bool allowStatusCheckFunctions = false)
    {
        using var parser = new Parser(expressionUtf8);
        var root = parser.ParseExpression();
        parser.SkipWhiteSpace();
        if (!parser.End)
        {
            parser.AddError($"unexpected token at position {parser.Position}");
        }

        // Transfer parse diagnostics with location shifting
        var parseDiags = parser.DiagnosticsAsSpan();
        for (var i = 0; i < parseDiags.Length; i++)
        {
            var d = parseDiags[i];
            diagnostics.Add(new Diagnostic(
                d.Severity,
                $"expression parse error: {d.Message}",
                ShiftExpressionLocation(expressionLocation, d.Location.Start, d.Location.Length)));
        }

        // Validate with spans — no array allocation
        if (root >= 0)
        {
            ExpressionSemanticAnalyzer.ValidateInline(
                root,
                parser.NodesAsSpan(),
                parser.ArgsAsSpan(),
                expressionUtf8,
                expressionLocation,
                context,
                diagnostics,
                allowStatusCheckFunctions);
        }
    }

    /// <summary>
    /// PooledBuffer overload: same logic as above but writes to a ref PooledBuffer
    /// for use from the parser hot path (avoids List allocation at the boundary).
    /// </summary>
    internal static void ParseAndValidateInline(
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        ExpressionValidationContext context,
        ref PooledBuffer<Diagnostic> diagnostics,
        bool allowStatusCheckFunctions = false)
    {
        using var parser = new Parser(expressionUtf8);
        var root = parser.ParseExpression();
        parser.SkipWhiteSpace();
        if (!parser.End)
        {
            parser.AddError($"unexpected token at position {parser.Position}");
        }

        // Transfer parse diagnostics with location shifting
        var parseDiags = parser.DiagnosticsAsSpan();
        for (var i = 0; i < parseDiags.Length; i++)
        {
            var d = parseDiags[i];
            diagnostics.Add(new Diagnostic(
                d.Severity,
                $"expression parse error: {d.Message}",
                ShiftExpressionLocation(expressionLocation, d.Location.Start, d.Location.Length)));
        }

        // Validate with spans — no array allocation (uses List-based ValidateInline internally)
        if (root >= 0)
        {
            var tempList = threadstaticValidateDiagnostics ??= new List<Diagnostic>();
            tempList.Clear();
            ExpressionSemanticAnalyzer.ValidateInline(
                root,
                parser.NodesAsSpan(),
                parser.ArgsAsSpan(),
                expressionUtf8,
                expressionLocation,
                context,
                tempList,
                allowStatusCheckFunctions);
            for (var i = 0; i < tempList.Count; i++)
            {
                diagnostics.Add(tempList[i]);
            }

            // Cap retained capacity to prevent unbounded growth from pathological expressions
            if (tempList.Capacity > 64)
            {
                threadstaticValidateDiagnostics = new List<Diagnostic>(16);
            }
        }
    }

    [ThreadStatic] private static List<Diagnostic>? threadstaticValidateDiagnostics;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TextRange ShiftExpressionLocation(TextRange baseLocation, int relativeOffset, int length)
    {
        var safeLength = length <= 0 ? 1 : length;
        return new TextRange(
            Start: baseLocation.Start + relativeOffset,
            Length: safeLength,
            StartLine: baseLocation.StartLine,
            StartColumn: baseLocation.StartColumn + relativeOffset,
            EndLine: baseLocation.EndLine,
            EndColumn: baseLocation.StartColumn + relativeOffset + safeLength - 1);
    }

    // PooledBuffer<T> is now shared — see PooledBuffer.cs

    private ref struct Parser
    {
        private readonly ReadOnlySpan<byte> _text;
        private int _pos;
        private PooledBuffer<ExpressionNode> _nodes;
        private PooledBuffer<int> _args;
        private PooledBuffer<Diagnostic> _diagnostics;

        public Parser(ReadOnlySpan<byte> text)
        {
            _text = text;
            _pos = 0;
            _nodes = new PooledBuffer<ExpressionNode>(16);
            _args = new PooledBuffer<int>(16);
            _diagnostics = new PooledBuffer<Diagnostic>(4);
        }

        public int Position => _pos;

        public bool End => _pos >= _text.Length;

        public int ParseExpression() => ParseOr();

        private int ParseOr()
        {
            var left = ParseAnd();
            while (Match("||"u8))
            {
                var right = ParseAnd();
                left = AddBinary(left, right, ExpressionOperator.Or);
            }

            return left;
        }

        private int ParseAnd()
        {
            var left = ParseEquality();
            while (Match("&&"u8))
            {
                var right = ParseEquality();
                left = AddBinary(left, right, ExpressionOperator.And);
            }

            return left;
        }

        private int ParseEquality()
        {
            var left = ParseRelational();
            while (true)
            {
                if (Match("=="u8))
                {
                    var right = ParseRelational();
                    left = AddBinary(left, right, ExpressionOperator.Equal);
                    continue;
                }

                if (Match("!="u8))
                {
                    var right = ParseRelational();
                    left = AddBinary(left, right, ExpressionOperator.NotEqual);
                    continue;
                }

                break;
            }

            return left;
        }

        private int ParseRelational()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Match("<="u8))
                {
                    var right = ParseUnary();
                    left = AddBinary(left, right, ExpressionOperator.LessOrEqual);
                    continue;
                }

                if (Match(">="u8))
                {
                    var right = ParseUnary();
                    left = AddBinary(left, right, ExpressionOperator.GreaterOrEqual);
                    continue;
                }

                if (Match("<"u8))
                {
                    var right = ParseUnary();
                    left = AddBinary(left, right, ExpressionOperator.Less);
                    continue;
                }

                if (Match(">"u8))
                {
                    var right = ParseUnary();
                    left = AddBinary(left, right, ExpressionOperator.Greater);
                    continue;
                }

                break;
            }

            return left;
        }

        private int ParseUnary()
        {
            if (Match("!"u8))
            {
                var operand = ParseUnary();
                if (operand < 0)
                {
                    AddError("operator '!' requires an operand");
                    return -1;
                }

                return AddNode(new ExpressionNode(ExpressionNodeKind.Unary, operand, -1, 0, 0, default, ExpressionOperator.Not));
            }

            return ParsePrimary();
        }

        private int ParsePrimary()
        {
            SkipWhiteSpace();
            if (End)
            {
                AddError("unexpected end of expression");
                return -1;
            }

            if (Match("("u8))
            {
                var inner = ParseExpression();
                if (!Match(")"u8))
                {
                    AddError("missing closing ')' ");
                }

                return inner;
            }

            int expr;
            if (Peek() == '\'')
            {
                expr = ParseStringLiteral();
            }
            else if (Peek() == '"')
            {
                AddError("got unexpected character '\"'; only single quotes are available for string delimiter in expressions");
                // Skip the double-quoted string so we can continue parsing the rest
                _pos++;
                while (!End && Peek() != '"') _pos++;
                if (!End) _pos++;
                return -1;
            }
            else if (IsDigit(Peek()))
            {
                expr = ParseNumberLiteral();
            }
            else if (TryParseIdentifier(out var identifierSlice))
            {
                expr = ParseKeywordOrIdentifier(identifierSlice);
            }
            else
            {
                AddError($"unexpected token '{(char)Peek()}' at position {_pos}");
                _pos++;
                return -1;
            }

            while (true)
            {
                SkipWhiteSpace();
                if (Match("."u8))
                {
                    if (Match("*"u8))
                    {
                        expr = AddNode(new ExpressionNode(ExpressionNodeKind.WildcardAccess, expr, -1, 0, 0, default, ExpressionOperator.None));
                        continue;
                    }

                    if (!TryParseIdentifier(out var memberSlice))
                    {
                        AddError("member name is missing after '.'");
                        return expr;
                    }

                    expr = AddNode(new ExpressionNode(ExpressionNodeKind.MemberAccess, expr, -1, 0, 0, memberSlice, ExpressionOperator.None));
                    continue;
                }

                if (Match("["u8))
                {
                    SkipWhiteSpace();
                    if (Match("*"u8))
                    {
                        if (!Match("]"u8))
                        {
                            AddError("missing closing ']' after wildcard index");
                        }

                        expr = AddNode(new ExpressionNode(ExpressionNodeKind.WildcardAccess, expr, -1, 0, 0, default, ExpressionOperator.None));
                        continue;
                    }

                    var index = ParseIndexExpression();
                    if (!Match("]"u8))
                    {
                        AddError("missing closing ']' in index access");
                    }

                    if (index >= 0)
                    {
                        expr = AddNode(new ExpressionNode(ExpressionNodeKind.IndexAccess, expr, index, 0, 0, default, ExpressionOperator.None));
                    }

                    continue;
                }

                if (Match("("u8))
                {
                    // Collect this function's direct arguments locally before adding to the
                    // shared Arguments buffer. ParseExpression() for each argument may
                    // recursively parse inner function calls that add their own args.
                    // By deferring the add, ArgStart/ArgCount reflect only this call's args.
                    Span<int> directArgs = stackalloc int[16];
                    var directArgCount = 0;
                    SkipWhiteSpace();
                    if (!Match(")"u8))
                    {
                        while (true)
                        {
                            var arg = ParseExpression();
                            if (arg >= 0 && directArgCount < directArgs.Length)
                            {
                                directArgs[directArgCount++] = arg;
                            }

                            SkipWhiteSpace();
                            if (Match(")"u8))
                            {
                                break;
                            }

                            if (!Match(","u8))
                            {
                                AddError("expected ',' or ')' in function call");
                                break;
                            }
                        }
                    }

                    // Add this function's args after inner calls have already added theirs.
                    var argStart = _args.Count;
                    for (var i = 0; i < directArgCount; i++)
                    {
                        AddArgument(directArgs[i]);
                    }

                    expr = AddNode(new ExpressionNode(
                        ExpressionNodeKind.FunctionCall,
                        expr,
                        -1,
                        argStart,
                        directArgCount,
                        default,
                        ExpressionOperator.None));
                    continue;
                }

                break;
            }

            return expr;
        }

        private int ParseIndexExpression()
        {
            SkipWhiteSpace();
            if (End)
            {
                AddError("index expression is missing");
                return -1;
            }

            // Allow full expressions as index keys (e.g. secrets[matrix.secret], env[vars.key]).
            // The original limited implementation only supported string/number literals and bare
            // identifiers. Any valid GitHub Actions expression is a valid index operand.
            return ParseExpression();
        }

        private int ParseKeywordOrIdentifier(Utf8Slice identifierSlice)
        {
            var ident = identifierSlice.AsSpan(_text);
            if (ident.SequenceEqual("true"u8))
            {
                return AddNode(new ExpressionNode(ExpressionNodeKind.BooleanLiteral, -1, -1, 0, 0, identifierSlice, ExpressionOperator.None));
            }

            if (ident.SequenceEqual("false"u8))
            {
                return AddNode(new ExpressionNode(ExpressionNodeKind.BooleanLiteral, -1, -1, 0, 0, identifierSlice, ExpressionOperator.None));
            }

            if (ident.SequenceEqual("null"u8))
            {
                return AddNode(new ExpressionNode(ExpressionNodeKind.NullLiteral, -1, -1, 0, 0, identifierSlice, ExpressionOperator.None));
            }

            return AddNode(new ExpressionNode(ExpressionNodeKind.Identifier, -1, -1, 0, 0, identifierSlice, ExpressionOperator.None));
        }

        private int ParseStringLiteral()
        {
            var quote = Peek();
            _pos++;
            var start = _pos;
            while (!End && Peek() != quote)
            {
                if (Peek() == '\\' && _pos + 1 < _text.Length)
                {
                    _pos += 2;
                    continue;
                }

                _pos++;
            }

            var token = new Utf8Slice(start, _pos - start);
            if (!End)
            {
                _pos++;
            }
            else
            {
                AddError("unterminated string literal");
            }

            return AddNode(new ExpressionNode(ExpressionNodeKind.StringLiteral, -1, -1, 0, 0, token, ExpressionOperator.None));
        }

        private int ParseNumberLiteral()
        {
            var start = _pos;
            while (!End && (IsDigit(Peek()) || Peek() == '.'))
            {
                _pos++;
            }

            var token = new Utf8Slice(start, _pos - start);
            return AddNode(new ExpressionNode(ExpressionNodeKind.NumberLiteral, -1, -1, 0, 0, token, ExpressionOperator.None));
        }

        private bool TryParseIdentifier(out Utf8Slice identifierSlice)
        {
            SkipWhiteSpace();
            identifierSlice = default;
            if (End)
            {
                return false;
            }

            var ch = Peek();
            if (!(IsLetter(ch) || ch == '_'))
            {
                return false;
            }

            var start = _pos;
            _pos++;
            while (!End)
            {
                ch = Peek();
                if (!(IsLetter(ch) || IsDigit(ch) || ch == '_' || ch == '-'))
                {
                    break;
                }

                _pos++;
            }

            identifierSlice = new Utf8Slice(start, _pos - start);
            return true;
        }

        private int AddBinary(int left, int right, ExpressionOperator op)
        {
            if (left < 0 || right < 0)
            {
                AddError($"operator '{op}' requires both operands");
                return left >= 0 ? left : right;
            }

            return AddNode(new ExpressionNode(ExpressionNodeKind.Binary, left, right, 0, 0, default, op));
        }

        private int AddNode(ExpressionNode node) => _nodes.Add(node);

        private void AddArgument(int arg) => _args.Add(arg);

        private bool Match(ReadOnlySpan<byte> token)
        {
            SkipWhiteSpace();
            if (_pos + token.Length > _text.Length)
            {
                return false;
            }

            if (!_text.Slice(_pos, token.Length).SequenceEqual(token))
            {
                return false;
            }

            _pos += token.Length;
            return true;
        }

        public void SkipWhiteSpace()
        {
            while (!End && IsWhiteSpace(Peek()))
            {
                _pos++;
            }
        }

        public void AddError(string message)
        {
            var location = new TextRange(
                Start: _pos,
                Length: 0,
                StartLine: 1,
                StartColumn: _pos + 1,
                EndLine: 1,
                EndColumn: _pos + 1);
            _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, location));
        }

        public ExpressionNode[] NodesToArray() => _nodes.ToArray();

        public int[] ArgumentsToArray() => _args.ToArray();

        public Diagnostic[] DiagnosticsToArray() => _diagnostics.ToArray();

        public ReadOnlySpan<ExpressionNode> NodesAsSpan() => _nodes.AsSpan();

        public ReadOnlySpan<int> ArgsAsSpan() => _args.AsSpan();

        public ReadOnlySpan<Diagnostic> DiagnosticsAsSpan() => _diagnostics.AsSpan();

        public void Dispose()
        {
            _nodes.Dispose();
            _args.Dispose();
            _diagnostics.Dispose();
        }

        private byte Peek() => _text[_pos];

        private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

        private static bool IsLetter(byte b) =>
            (b is >= (byte)'a' and <= (byte)'z') || (b is >= (byte)'A' and <= (byte)'Z');
    }
}
