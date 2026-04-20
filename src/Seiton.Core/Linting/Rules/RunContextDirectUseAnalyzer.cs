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

    internal static TextRange BuildExpressionLocation(byte[] utf8Yaml, StringNode runNode, int bodyStart, int nextSearchStart)
    {
        var absoluteStart = runNode.Value.Offset + bodyStart - 3;
        var absoluteLength = nextSearchStart - (bodyStart - 3);
        if (absoluteStart < 0 || absoluteLength <= 0)
        {
            return runNode.Range;
        }

        var lineStarts = BuildLineStarts(utf8Yaml);
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

    internal static bool IsPowerShell(Step step, byte[] utf8Yaml)
    {
        if (step.Exec is not ExecRun run || run.Shell is null || run.Shell.Expression is not null)
        {
            return false;
        }

        return IsPowerShell(run.Shell, utf8Yaml);
    }

    internal static bool IsPowerShell(StringNode? shellNode, byte[] utf8Yaml)
    {
        if (shellNode is null || shellNode.Expression is not null)
        {
            return false;
        }

        var shell = Encoding.UTF8.GetString(shellNode.Value.AsSpan(utf8Yaml));
        return string.Equals(shell, "pwsh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase);
    }

    // Env Value Expression Extraction

    internal static bool TryExtractExpressionBody(StringNode node, byte[] utf8Yaml, out ReadOnlySpan<byte> expressionBody)
    {
        expressionBody = [];

        var value = TrimAsciiWhiteSpace(node.Value.AsSpan(utf8Yaml));
        if (value.Length == 0)
        {
            return false;
        }

        if (TryExtractEmbeddedExpressionBody(value, out expressionBody))
        {
            return true;
        }

        if (node.Expression is null)
        {
            return false;
        }

        var expression = TrimAsciiWhiteSpace(node.Expression.Value.AsSpan(utf8Yaml));
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

    static bool ContainsContextRootReferenceInFunction(
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

    internal static bool TryResolveShellVariableNameInEnv(Env? env, byte[] utf8Yaml, string targetName, SimpleReferenceParser parser, out string variableName)
    {
        variableName = string.Empty;
        if (env?.Vars is null || env.Vars.Count == 0)
        {
            return false;
        }

        var matches = 0;
        foreach (var pair in env.Vars)
        {
            var envVar = pair.Value;
            var envNameIndex = 0;
            if (!TryReadIdentifier(envVar.Name.Value.AsSpan(utf8Yaml), ref envNameIndex, out var candidateVariable)
                || envNameIndex != envVar.Name.Value.Length
                || !IsSimpleIdentifier(candidateVariable))
            {
                continue;
            }

            if (!TryExtractExpressionBody(envVar.Value, utf8Yaml, out var body)
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
        Env? stepEnv, Env? jobEnv, Env? workflowEnv,
        byte[] utf8Yaml, string targetName, SimpleReferenceParser parser,
        out string variableName)
    {
        variableName = string.Empty;
        var matchCount = 0;
        if (TryResolveShellVariableNameInEnv(stepEnv, utf8Yaml, targetName, parser, out var stepVariable))
        {
            variableName = stepVariable;
            matchCount++;
        }

        if (TryResolveShellVariableNameInEnv(jobEnv, utf8Yaml, targetName, parser, out var jobVariable))
        {
            variableName = jobVariable;
            matchCount++;
        }

        if (TryResolveShellVariableNameInEnv(workflowEnv, utf8Yaml, targetName, parser, out var workflowVariable))
        {
            variableName = workflowVariable;
            matchCount++;
        }

        return matchCount == 1;
    }
}
