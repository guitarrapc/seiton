namespace Seiton.Core.Parsing;

public static class ExpressionParser
{
    public static ExpressionParseResult Parse(string expression)
    {
        var parser = new Parser(expression);
        var root = parser.ParseExpression();
        parser.SkipWhiteSpace();
        if (!parser.End)
        {
            parser.AddError($"unexpected token at position {parser.Position}");
        }

        return new ExpressionParseResult(root, parser.Diagnostics.ToArray());
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _pos;

        public Parser(string text)
        {
            _text = text;
        }

        public bool End => _pos >= _text.Length;

        public int Position => _pos;

        public List<Diagnostic> Diagnostics { get; } = new();

        public ExpressionSyntax? ParseExpression() => ParseOr();

        private ExpressionSyntax? ParseOr()
        {
            var left = ParseAnd();
            while (Match("||"))
            {
                var right = ParseAnd();
                if (left is null || right is null)
                {
                    AddError("operator '||' requires both operands");
                    return left ?? right;
                }

                left = new BinarySyntax(left, "||", right);
            }

            return left;
        }

        private ExpressionSyntax? ParseAnd()
        {
            var left = ParseEquality();
            while (Match("&&"))
            {
                var right = ParseEquality();
                if (left is null || right is null)
                {
                    AddError("operator '&&' requires both operands");
                    return left ?? right;
                }

                left = new BinarySyntax(left, "&&", right);
            }

            return left;
        }

        private ExpressionSyntax? ParseEquality()
        {
            var left = ParseRelational();
            while (true)
            {
                if (Match("=="))
                {
                    var right = ParseRelational();
                    if (left is null || right is null)
                    {
                        AddError("operator '==' requires both operands");
                        return left ?? right;
                    }

                    left = new BinarySyntax(left, "==", right);
                    continue;
                }

                if (Match("!="))
                {
                    var right = ParseRelational();
                    if (left is null || right is null)
                    {
                        AddError("operator '!=' requires both operands");
                        return left ?? right;
                    }

                    left = new BinarySyntax(left, "!=", right);
                    continue;
                }

                break;
            }

            return left;
        }

        private ExpressionSyntax? ParseRelational()
        {
            var left = ParseAdditive();
            while (true)
            {
                if (Match("<="))
                {
                    var right = ParseAdditive();
                    left = CombineBinary(left, "<=", right);
                    continue;
                }

                if (Match(">="))
                {
                    var right = ParseAdditive();
                    left = CombineBinary(left, ">=", right);
                    continue;
                }

                if (Match("<"))
                {
                    var right = ParseAdditive();
                    left = CombineBinary(left, "<", right);
                    continue;
                }

                if (Match(">"))
                {
                    var right = ParseAdditive();
                    left = CombineBinary(left, ">", right);
                    continue;
                }

                break;
            }

            return left;
        }

        private ExpressionSyntax? ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (true)
            {
                if (Match("+"))
                {
                    var right = ParseMultiplicative();
                    left = CombineBinary(left, "+", right);
                    continue;
                }

                if (Match("-"))
                {
                    var right = ParseMultiplicative();
                    left = CombineBinary(left, "-", right);
                    continue;
                }

                break;
            }

            return left;
        }

        private ExpressionSyntax? ParseMultiplicative()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Match("*"))
                {
                    var right = ParseUnary();
                    left = CombineBinary(left, "*", right);
                    continue;
                }

                if (Match("/"))
                {
                    var right = ParseUnary();
                    left = CombineBinary(left, "/", right);
                    continue;
                }

                if (Match("%"))
                {
                    var right = ParseUnary();
                    left = CombineBinary(left, "%", right);
                    continue;
                }

                break;
            }

            return left;
        }

        private ExpressionSyntax? ParseUnary()
        {
            if (Match("!"))
            {
                var operand = ParseUnary();
                if (operand is null)
                {
                    AddError("operator '!' requires an operand");
                    return null;
                }

                return new UnarySyntax("!", operand);
            }

            if (Match("-"))
            {
                var operand = ParseUnary();
                if (operand is null)
                {
                    AddError("unary '-' requires an operand");
                    return null;
                }

                return new UnarySyntax("-", operand);
            }

            return ParsePrimary();
        }

        private ExpressionSyntax? ParsePrimary()
        {
            SkipWhiteSpace();
            if (End)
            {
                AddError("unexpected end of expression");
                return null;
            }

            if (Match("("))
            {
                var inner = ParseExpression();
                if (!Match(")"))
                {
                    AddError("missing closing ')' ");
                }
                return inner;
            }

            if (Peek() == '\'' || Peek() == '"')
            {
                return ParseStringLiteral();
            }

            if (char.IsDigit(Peek()))
            {
                return ParseNumberLiteral();
            }

            if (!TryParseIdentifier(out var ident))
            {
                AddError($"unexpected token '{Peek()}' at position {_pos}");
                _pos++;
                return null;
            }

            ExpressionSyntax expr = ParseKeywordOrIdentifier(ident);

            while (true)
            {
                SkipWhiteSpace();
                if (Match("."))
                {
                    if (Match("*"))
                    {
                        expr = new WildcardAccessSyntax(expr);
                        continue;
                    }

                    if (!TryParseIdentifier(out var member))
                    {
                        AddError("member name is missing after '.'");
                        return expr;
                    }

                    expr = new MemberAccessSyntax(expr, member);
                    continue;
                }

                if (Match("("))
                {
                    var args = new List<ExpressionSyntax>();
                    SkipWhiteSpace();
                    if (!Match(")"))
                    {
                        while (true)
                        {
                            var arg = ParseExpression();
                            if (arg is not null)
                            {
                                args.Add(arg);
                            }

                            SkipWhiteSpace();
                            if (Match(")"))
                            {
                                break;
                            }

                            if (!Match(","))
                            {
                                AddError("expected ',' or ')' in function call");
                                break;
                            }
                        }
                    }

                    expr = new FunctionCallSyntax(expr, args);
                    continue;
                }

                if (Match("["))
                {
                    SkipWhiteSpace();
                    if (Match("*"))
                    {
                        if (!Match("]"))
                        {
                            AddError("missing closing ']' after wildcard index");
                        }

                        expr = new WildcardAccessSyntax(expr);
                        continue;
                    }

                    var index = ParseIndexExpression();
                    if (!Match("]"))
                    {
                        AddError("missing closing ']' in index access");
                    }

                    if (index is not null)
                    {
                        expr = new IndexAccessSyntax(expr, index);
                    }

                    continue;
                }

                break;
            }

            return expr;
        }

        private ExpressionSyntax? ParseIndexExpression()
        {
            SkipWhiteSpace();
            if (End)
            {
                AddError("index expression is missing");
                return null;
            }

            if (Peek() == '\'' || Peek() == '"')
            {
                return ParseStringLiteral();
            }

            if (char.IsDigit(Peek()))
            {
                return ParseNumberLiteral();
            }

            if (TryParseIdentifier(out var ident))
            {
                return ParseKeywordOrIdentifier(ident);
            }

            AddError($"unsupported index token '{Peek()}'");
            _pos++;
            return null;
        }

        private ExpressionSyntax ParseKeywordOrIdentifier(string ident)
        {
            if (string.Equals(ident, "true", StringComparison.Ordinal))
            {
                return new BooleanLiteralSyntax(true);
            }

            if (string.Equals(ident, "false", StringComparison.Ordinal))
            {
                return new BooleanLiteralSyntax(false);
            }

            if (string.Equals(ident, "null", StringComparison.Ordinal))
            {
                return new NullLiteralSyntax();
            }

            return new IdentifierSyntax(ident);
        }

        private ExpressionSyntax ParseStringLiteral()
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

            var value = _text.Substring(start, _pos - start);
            if (!End)
            {
                _pos++;
            }
            else
            {
                AddError("unterminated string literal");
            }

            return new StringLiteralSyntax(value);
        }

        private ExpressionSyntax ParseNumberLiteral()
        {
            var start = _pos;
            while (!End && (char.IsDigit(Peek()) || Peek() == '.'))
            {
                _pos++;
            }

            return new NumberLiteralSyntax(_text.Substring(start, _pos - start));
        }

        private bool TryParseIdentifier(out string identifier)
        {
            SkipWhiteSpace();
            identifier = string.Empty;
            if (End)
            {
                return false;
            }

            var ch = Peek();
            if (!(char.IsLetter(ch) || ch == '_'))
            {
                return false;
            }

            var start = _pos;
            _pos++;
            while (!End)
            {
                ch = Peek();
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-'))
                {
                    break;
                }

                _pos++;
            }

            identifier = _text.Substring(start, _pos - start);
            return true;
        }

        private ExpressionSyntax? CombineBinary(ExpressionSyntax? left, string op, ExpressionSyntax? right)
        {
            if (left is null || right is null)
            {
                AddError($"operator '{op}' requires both operands");
                return left ?? right;
            }

            return new BinarySyntax(left, op, right);
        }

        private bool Match(string token)
        {
            SkipWhiteSpace();
            if (_pos + token.Length > _text.Length)
            {
                return false;
            }

            if (string.Compare(_text, _pos, token, 0, token.Length, StringComparison.Ordinal) != 0)
            {
                return false;
            }

            _pos += token.Length;
            return true;
        }

        public void SkipWhiteSpace()
        {
            while (!End && char.IsWhiteSpace(Peek()))
            {
                _pos++;
            }
        }

        private char Peek() => _text[_pos];

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
    }
}
