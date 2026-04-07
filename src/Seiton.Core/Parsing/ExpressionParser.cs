using System.Buffers;
using System.Text;

namespace Seiton.Core.Parsing;

public static class ExpressionParser
{
    public static ExpressionParseResult Parse(string expression)
    {
        var byteCount = Encoding.UTF8.GetByteCount(expression);
        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(expression.AsSpan(), rented.AsSpan(0, byteCount));
            return Parse(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static ExpressionParseResult Parse(ReadOnlySpan<byte> expressionUtf8)
    {
        var parser = new Parser(expressionUtf8);
        var root = parser.ParseExpression();
        parser.SkipWhiteSpace();
        if (!parser.End)
        {
            parser.AddError($"unexpected token at position {parser.Position}");
        }

        return new ExpressionParseResult(
            RootNode: root,
            Nodes: parser.Nodes.ToArray(),
            Arguments: parser.Arguments.ToArray(),
            Diagnostics: parser.Diagnostics.ToArray());
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<byte> _text;
        private int _pos;

        public Parser(ReadOnlySpan<byte> text)
        {
            _text = text;
            _pos = 0;
            Nodes = new List<ExpressionNode>(16);
            Arguments = new List<int>(16);
            Diagnostics = new List<Diagnostic>(4);
        }

        public int Position => _pos;

        public bool End => _pos >= _text.Length;

        public List<ExpressionNode> Nodes { get; }

        public List<int> Arguments { get; }

        public List<Diagnostic> Diagnostics { get; }

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
            var left = ParseAdditive();
            while (true)
            {
                if (Match("<="u8))
                {
                    var right = ParseAdditive();
                    left = AddBinary(left, right, ExpressionOperator.LessOrEqual);
                    continue;
                }

                if (Match(">="u8))
                {
                    var right = ParseAdditive();
                    left = AddBinary(left, right, ExpressionOperator.GreaterOrEqual);
                    continue;
                }

                if (Match("<"u8))
                {
                    var right = ParseAdditive();
                    left = AddBinary(left, right, ExpressionOperator.Less);
                    continue;
                }

                if (Match(">"u8))
                {
                    var right = ParseAdditive();
                    left = AddBinary(left, right, ExpressionOperator.Greater);
                    continue;
                }

                break;
            }

            return left;
        }

        private int ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (true)
            {
                if (Match("+"u8))
                {
                    var right = ParseMultiplicative();
                    left = AddBinary(left, right, ExpressionOperator.Add);
                    continue;
                }

                if (Match("-"u8))
                {
                    var right = ParseMultiplicative();
                    left = AddBinary(left, right, ExpressionOperator.Subtract);
                    continue;
                }

                break;
            }

            return left;
        }

        private int ParseMultiplicative()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Match("*"u8))
                {
                    var right = ParseUnary();
                    left = AddBinary(left, right, ExpressionOperator.Multiply);
                    continue;
                }

                if (Match("/"u8))
                {
                    var right = ParseUnary();
                    left = AddBinary(left, right, ExpressionOperator.Divide);
                    continue;
                }

                if (Match("%"u8))
                {
                    var right = ParseUnary();
                    left = AddBinary(left, right, ExpressionOperator.Modulo);
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

            if (Match("-"u8))
            {
                var operand = ParseUnary();
                if (operand < 0)
                {
                    AddError("unary '-' requires an operand");
                    return -1;
                }

                return AddNode(new ExpressionNode(ExpressionNodeKind.Unary, operand, -1, 0, 0, default, ExpressionOperator.Negate));
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
            if (Peek() == '\'' || Peek() == '"')
            {
                expr = ParseStringLiteral();
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
                    var argStart = Arguments.Count;
                    SkipWhiteSpace();
                    if (!Match(")"u8))
                    {
                        while (true)
                        {
                            var arg = ParseExpression();
                            if (arg >= 0)
                            {
                                Arguments.Add(arg);
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

                    expr = AddNode(new ExpressionNode(
                        ExpressionNodeKind.FunctionCall,
                        expr,
                        -1,
                        argStart,
                        Arguments.Count - argStart,
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

            if (Peek() == '\'' || Peek() == '"')
            {
                return ParseStringLiteral();
            }

            if (IsDigit(Peek()))
            {
                return ParseNumberLiteral();
            }

            if (TryParseIdentifier(out var identifierSlice))
            {
                return ParseKeywordOrIdentifier(identifierSlice);
            }

            AddError($"unsupported index token '{(char)Peek()}'");
            _pos++;
            return -1;
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

        private int AddNode(ExpressionNode node)
        {
            var id = Nodes.Count;
            Nodes.Add(node);
            return id;
        }

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
            Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, location));
        }

        private byte Peek() => _text[_pos];

        private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

        private static bool IsLetter(byte b) =>
            (b is >= (byte)'a' and <= (byte)'z') || (b is >= (byte)'A' and <= (byte)'Z');

        private static bool IsWhiteSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
    }
}
