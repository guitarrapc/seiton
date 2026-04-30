using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using System.Text;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>
/// Shared scanning, location-building, and fix-generation utilities for
/// RunEnvContextDirectUseRule, RunInputsContextDirectUseRule, and RunSecretsContextDirectUseRule.
/// </summary>
internal static class RunContextDirectUseAnalyzer
{
    internal delegate bool SimpleReferenceParser(ReadOnlySpan<byte> expression, out string name);

    // Expression Location

    internal static TextRange BuildExpressionLocation(AstArena arena, byte[] utf8Yaml, StringNodeId runNode, int bodyStart, int nextSearchStart, int[] lineStarts)
    {
        var absoluteStart = arena.GetStringSlice(runNode).Offset + bodyStart - 3;
        var absoluteLength = nextSearchStart - (bodyStart - 3);
        if (absoluteStart < 0 || absoluteLength <= 0)
        {
            return arena.GetStringRange(runNode);
        }

        var start = OffsetToLineColumn(lineStarts, absoluteStart);
        var end = OffsetToLineColumn(lineStarts, absoluteStart + absoluteLength - 1);
        return new TextRange(
            Start: absoluteStart,
            Length: absoluteLength,
            StartLine: start.Line,
            StartColumn: start.Column,
            EndLine: end.Line,
            EndColumn: end.Column);
    }

    // Shell Detection

    internal static bool IsPowerShell(AstArena arena, Step step, byte[] utf8Yaml)
    {
        if (step.Exec is not ExecRun run || !run.Shell.HasValue || arena.GetStringExpression(run.Shell).HasValue)
        {
            return false;
        }

        return IsPowerShell(arena, run.Shell, utf8Yaml);
    }

    internal static bool IsPowerShell(AstArena arena, StringNodeId shellNode, byte[] utf8Yaml)
    {
        if (!shellNode.HasValue || arena.GetStringExpression(shellNode).HasValue)
        {
            return false;
        }

        var shell = Encoding.UTF8.GetString(arena.GetStringValue(shellNode));
        return string.Equals(shell, "pwsh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase);
    }

    // Env Value Expression Extraction

    internal static bool TryExtractExpressionBody(AstArena arena, StringNodeId node, byte[] utf8Yaml, out ReadOnlySpan<byte> expressionBody)
    {
        expressionBody = [];

        var value = TrimAsciiWhiteSpace(arena.GetStringValue(node));
        if (value.Length == 0)
        {
            return false;
        }

        if (TryExtractEmbeddedExpressionBody(value, out expressionBody))
        {
            return true;
        }

        if (!arena.GetStringExpression(node).HasValue)
        {
            return false;
        }

        var expression = TrimAsciiWhiteSpace(arena.GetStringValue(arena.GetStringExpression(node)));
        if (TryExtractEmbeddedExpressionBody(expression, out expressionBody))
        {
            return true;
        }

        expressionBody = expression;
        return expressionBody.Length > 0;
    }

    internal static bool TryExtractEmbeddedExpressionBody(ReadOnlySpan<byte> value, out ReadOnlySpan<byte> expressionBody)
    {
        expressionBody = [];
        if (!value.StartsWith("${{"u8) || !value.EndsWith("}}"u8))
        {
            return false;
        }

        expressionBody = TrimAsciiWhiteSpace(value.Slice(3, value.Length - 5));
        return expressionBody.Length > 0;
    }

    // Simple Context Reference Parsing

    internal static bool TryConsumeMemberOrBracketName(ReadOnlySpan<byte> expression, ref int index, out string name)
    {
        name = string.Empty;
        if (index >= expression.Length)
        {
            return false;
        }

        if (expression[index] == (byte)'.')
        {
            index++;
            if (!TryReadIdentifier(expression, ref index, out name))
            {
                return false;
            }

            SkipWhiteSpace(expression, ref index);
            return index == expression.Length;
        }

        if (expression[index] != (byte)'[')
        {
            return false;
        }

        index++;
        SkipWhiteSpace(expression, ref index);
        if (index >= expression.Length)
        {
            return false;
        }

        var quote = expression[index];
        if (quote is not ((byte)'\'' or (byte)'"'))
        {
            return false;
        }

        index++;
        var start = index;
        while (index < expression.Length && expression[index] != quote)
        {
            index++;
        }

        if (index >= expression.Length)
        {
            return false;
        }

        var nameBytes = expression[start..index];
        index++;
        SkipWhiteSpace(expression, ref index);
        if (index >= expression.Length || expression[index] != (byte)']')
        {
            return false;
        }

        index++;
        SkipWhiteSpace(expression, ref index);
        if (index != expression.Length)
        {
            return false;
        }

        var parsedName = Encoding.UTF8.GetString(nameBytes);
        if (!IsSimpleIdentifier(parsedName))
        {
            return false;
        }

        name = parsedName;
        return true;
    }

    internal static bool TryParseSimpleContextReference(ReadOnlySpan<byte> expression, ReadOnlySpan<byte> rootToken, out string name)
    {
        name = string.Empty;
        var index = 0;
        if (!ConsumeWordIgnoreCase(expression, ref index, rootToken))
        {
            return false;
        }

        SkipWhiteSpace(expression, ref index);
        return TryConsumeMemberOrBracketName(expression, ref index, out name);
    }

    // AST Root Reference Detection

    internal static bool ContainsContextRootReference(
        int nodeId,
        int parentId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        ReadOnlySpan<byte> rootToken)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind == ExpressionNodeKind.Identifier
            && IsContextRootIdentifier(nodeId, parentId, nodes)
            && EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), rootToken))
        {
            return true;
        }

        return node.Kind switch
        {
            ExpressionNodeKind.Unary => ContainsContextRootReference(node.Left, nodeId, nodes, arguments, expression, rootToken),
            ExpressionNodeKind.Binary => ContainsContextRootReference(node.Left, nodeId, nodes, arguments, expression, rootToken)
                || ContainsContextRootReference(node.Right, nodeId, nodes, arguments, expression, rootToken),
            ExpressionNodeKind.MemberAccess => ContainsContextRootReference(node.Left, nodeId, nodes, arguments, expression, rootToken),
            ExpressionNodeKind.WildcardAccess => ContainsContextRootReference(node.Left, nodeId, nodes, arguments, expression, rootToken),
            ExpressionNodeKind.IndexAccess => ContainsContextRootReference(node.Left, nodeId, nodes, arguments, expression, rootToken)
                || ContainsContextRootReference(node.Right, nodeId, nodes, arguments, expression, rootToken),
            ExpressionNodeKind.FunctionCall => ContainsContextRootReferenceInFunction(node, nodeId, nodes, arguments, expression, rootToken),
            _ => false,
        };
    }

    private static bool ContainsContextRootReferenceInFunction(
        ExpressionNode functionCallNode,
        int functionCallNodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        ReadOnlySpan<byte> rootToken)
    {
        if (ContainsContextRootReference(functionCallNode.Left, functionCallNodeId, nodes, arguments, expression, rootToken))
        {
            return true;
        }

        for (var i = 0; i < functionCallNode.ArgCount; i++)
        {
            var argIndex = functionCallNode.ArgStart + i;
            if (argIndex < 0 || argIndex >= arguments.Length)
            {
                continue;
            }

            if (ContainsContextRootReference(arguments[argIndex], functionCallNodeId, nodes, arguments, expression, rootToken))
            {
                return true;
            }
        }

        return false;
    }

    // Env-Mapping Resolution

    internal static bool TryResolveShellVariableNameInEnv(AstArena arena, Env? env, byte[] utf8Yaml, string targetName, SimpleReferenceParser parser, out string variableName)
    {
        variableName = string.Empty;
        if (env?.Vars is null || env.Vars.Value.Count == 0)
        {
            return false;
        }

        var matches = 0;
        foreach (var pair in env.Vars.Value)
        {
            var envVar = pair.Value;
            var envNameIndex = 0;
            if (!TryReadIdentifier(arena.GetStringValue(envVar.Name), ref envNameIndex, out var candidateVariable)
                || envNameIndex != arena.GetStringSlice(envVar.Name).Length
                || !IsSimpleIdentifier(candidateVariable))
            {
                continue;
            }

            if (!TryExtractExpressionBody(arena, envVar.Value, utf8Yaml, out var body)
                || !parser(body, out var candidateName)
                || !string.Equals(candidateName, targetName, StringComparison.Ordinal))
            {
                continue;
            }

            variableName = candidateVariable;
            matches++;
            if (matches > 1)
            {
                return false;
            }
        }

        return matches == 1;
    }

    internal static bool TryResolveShellVariableName(
        AstArena arena,
        Env? stepEnv, Env? jobEnv, Env? workflowEnv,
        byte[] utf8Yaml, string targetName, SimpleReferenceParser parser,
        out string variableName)
    {
        variableName = string.Empty;
        var matchCount = 0;
        if (TryResolveShellVariableNameInEnv(arena, stepEnv, utf8Yaml, targetName, parser, out var stepVariable))
        {
            variableName = stepVariable;
            matchCount++;
        }

        if (TryResolveShellVariableNameInEnv(arena, jobEnv, utf8Yaml, targetName, parser, out var jobVariable))
        {
            variableName = jobVariable;
            matchCount++;
        }

        if (TryResolveShellVariableNameInEnv(arena, workflowEnv, utf8Yaml, targetName, parser, out var workflowVariable))
        {
            variableName = workflowVariable;
            matchCount++;
        }

        return matchCount == 1;
    }

    // HereDoc Detection

    internal static bool IsInsideNoExpandHereDoc(byte[] source, int targetOffset)
    {
        if (source.Length == 0 || (uint)targetOffset >= (uint)source.Length)
        {
            return false;
        }

        Span<HereDocState> hereDocs = stackalloc HereDocState[4];
        var hereDocCount = 0;
        var targetLine = 1;
        for (var i = 0; i < targetOffset; i++)
        {
            if (source[i] == (byte)'\n')
            {
                targetLine++;
            }
        }

        var currentLine = 1;
        var lineStart = 0;
        while (lineStart <= source.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < source.Length && source[lineEnd] != (byte)'\n')
            {
                lineEnd++;
            }

            var isTargetLine = currentLine == targetLine;
            var line = source.AsSpan(lineStart, lineEnd - lineStart);
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (hereDocCount > 0)
            {
                var top = hereDocs[hereDocCount - 1];
                var candidate = line;
                if (top.StripTabs)
                {
                    var trimIndex = 0;
                    while (trimIndex < candidate.Length && candidate[trimIndex] == (byte)'\t')
                    {
                        trimIndex++;
                    }

                    candidate = candidate[trimIndex..];
                }

                if (candidate.SequenceEqual(source.AsSpan(top.TerminatorOffset, top.TerminatorLength)))
                {
                    hereDocCount--;
                }
                else if (isTargetLine)
                {
                    return true;
                }
            }
            else
            {
                if (TryParseNoExpandHereDocStart(line, lineStart, out var state) && hereDocCount < hereDocs.Length)
                {
                    hereDocs[hereDocCount++] = state;
                }

                if (isTargetLine)
                {
                    return false;
                }
            }

            if (lineEnd >= source.Length)
            {
                break;
            }

            currentLine++;
            lineStart = lineEnd + 1;
        }

        return false;
    }

    internal static bool TryParseNoExpandHereDocStart(ReadOnlySpan<byte> line, int lineStartInSource, out HereDocState state)
    {
        state = default;
        var i = 0;
        while (i < line.Length - 1)
        {
            if (line[i] != (byte)'<' || line[i + 1] != (byte)'<')
            {
                i++;
                continue;
            }

            i += 2;
            var stripTabs = false;
            if (i < line.Length && line[i] == (byte)'-')
            {
                stripTabs = true;
                i++;
            }

            while (i < line.Length && (line[i] == (byte)' ' || line[i] == (byte)'\t'))
            {
                i++;
            }

            if (i >= line.Length)
            {
                return false;
            }

            var quote = line[i];
            if (quote is not ((byte)'\'' or (byte)'"'))
            {
                return false;
            }

            i++;
            var start = i;
            while (i < line.Length && line[i] != quote)
            {
                i++;
            }

            if (i <= start || i >= line.Length)
            {
                return false;
            }

            state = new HereDocState(lineStartInSource + start, i - start, stripTabs);
            return true;
        }

        return false;
    }

    internal readonly record struct HereDocState(int TerminatorOffset, int TerminatorLength, bool StripTabs);

    // Single-Quote Detection

    /// <summary>
    /// Returns true when <paramref name="targetOffset"/> falls inside a shell single-quoted
    /// string on the same line. Shell single quotes suppress all variable expansion, so
    /// replacing ${{ }} with ${VAR} would be ineffective.
    /// </summary>
    internal static bool IsInsideShellSingleQuotes(byte[] source, int targetOffset)
    {
        if (source.Length == 0 || (uint)targetOffset >= (uint)source.Length)
        {
            return false;
        }

        // Find start of the line containing targetOffset
        var lineStart = targetOffset;
        while (lineStart > 0 && source[lineStart - 1] != (byte)'\n')
        {
            lineStart--;
        }

        // Walk from lineStart to targetOffset, counting unescaped single quotes.
        // In shell, single quotes cannot be escaped inside single-quoted strings
        // (there's no backslash escape), so we just toggle state on each '.
        var insideSingleQuote = false;
        for (var i = lineStart; i < targetOffset; i++)
        {
            if (source[i] == (byte)'\'')
            {
                insideSingleQuote = !insideSingleQuote;
            }
        }

        return insideSingleQuote;
    }
}
