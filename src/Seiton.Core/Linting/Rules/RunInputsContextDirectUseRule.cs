using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using System.Text;

namespace Seiton.Core.Linting.Rules;

public sealed class RunInputsContextDirectUseRule : RuleBase
{
    Workflow? _currentWorkflow;
    Job? _currentJob;

    public override string Id => "run-inputs-context-direct-use";

    public override string Name => "Run Inputs Context Direct Use Rule";

    public override void VisitWorkflowPre(Workflow workflow)
    {
        _currentWorkflow = workflow;
        _currentJob = null;
    }

    public override void VisitWorkflowPost(Workflow workflow)
    {
        _currentWorkflow = null;
        _currentJob = null;
    }

    public override void VisitJobPre(Job job)
    {
        _currentJob = job;
    }

    public override void VisitJobPost(Job job)
    {
        _currentJob = null;
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecRun run)
        {
            return;
        }

        CheckRunNode(step, run.Run);
    }

    void CheckRunNode(Step step, StringNode runNode)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        var runText = runNode.Value.AsSpan(Config.Utf8Yaml);
        var searchStart = 0;
        while (TryFindExpression(runText, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;
            var location = BuildExpressionLocation(runNode, bodyStart, nextSearchStart);

            var expression = TrimAsciiWhiteSpace(runText.Slice(bodyStart, bodyLength));
            if (expression.Length == 0)
            {
                continue;
            }

            var parseResult = ExpressionParser.Parse(expression);
            if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
            {
                continue;
            }

            if (!ContainsInputsReference(
                parseResult.RootNode,
                parentId: -1,
                parseResult.Nodes,
                parseResult.Arguments,
                expression))
            {
                continue;
            }

            if (TryBuildFix(step, runNode, expression, bodyStart, nextSearchStart - (bodyStart - 3), out var fix))
            {
                AddStepError(
                    step,
                    "run script must not reference ${{ inputs.* }} or ${{ github.event.inputs.* }} directly; map inputs to env and use shell variables instead (e.g. ${NAME}, $NAME, or $env:NAME)",
                    location,
                    fix);
            }
            else
            {
                AddStepError(
                    step,
                    "run script must not reference ${{ inputs.* }} or ${{ github.event.inputs.* }} directly; map inputs to env and use shell variables instead (e.g. ${NAME}, $NAME, or $env:NAME)",
                    location);
            }

            return;
        }
    }

    TextRange BuildExpressionLocation(StringNode runNode, int bodyStart, int nextSearchStart)
    {
        var absoluteStart = runNode.Value.Offset + bodyStart - 3;
        var absoluteLength = nextSearchStart - (bodyStart - 3);
        if (Config.Utf8Yaml is null || absoluteStart < 0 || absoluteLength <= 0)
        {
            return runNode.Range;
        }

        var lineStarts = BuildLineStarts(Config.Utf8Yaml);
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

    bool TryBuildFix(Step step, StringNode runNode, ReadOnlySpan<byte> expression, int expressionBodyStart, int expressionLength, out DiagnosticFix fix)
    {
        fix = default;
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        if (!TryParseSimpleInputsReference(expression, out var inputName))
        {
            return false;
        }

        if (!TryResolveShellVariableNameForInput(step, inputName, out var variableName))
        {
            return false;
        }

        var replacement = IsPowerShell(step, Config.Utf8Yaml)
            ? "$env:" + variableName
            : "${" + variableName + "}";

        var absoluteOffset = runNode.Value.Offset + expressionBodyStart - 3;
        fix = new DiagnosticFix(
            "replace direct inputs context expansion with mapped shell variable",
            [new TextEdit(absoluteOffset, expressionLength, replacement)]);
        return true;
    }

    bool TryResolveShellVariableNameForInput(Step step, string inputName, out string variableName)
    {
        variableName = string.Empty;
        var matchCount = 0;
        if (TryResolveShellVariableNameInEnv(step.Env, inputName, out var stepVariable))
        {
            variableName = stepVariable;
            matchCount++;
        }

        if (TryResolveShellVariableNameInEnv(_currentJob?.Env, inputName, out var jobVariable))
        {
            variableName = jobVariable;
            matchCount++;
        }

        if (TryResolveShellVariableNameInEnv(_currentWorkflow?.Env, inputName, out var workflowVariable))
        {
            variableName = workflowVariable;
            matchCount++;
        }

        return matchCount == 1;
    }

    bool TryResolveShellVariableNameInEnv(Env? env, string inputName, out string variableName)
    {
        variableName = string.Empty;
        if (Config.Utf8Yaml is null || env?.Vars is null || env.Vars.Count == 0)
        {
            return false;
        }

        var matches = 0;
        foreach (var pair in env.Vars)
        {
            var envVar = pair.Value;
            var envNameIndex = 0;
            if (!TryReadIdentifier(envVar.Name.Value.AsSpan(Config.Utf8Yaml), ref envNameIndex, out var candidateVariable)
                || envNameIndex != envVar.Name.Value.Length
                || !IsSimpleIdentifier(candidateVariable))
            {
                continue;
            }

            if (!TryExtractExpressionBody(envVar.Value, Config.Utf8Yaml, out var body)
                || !TryParseSimpleInputsReference(body, out var candidateInput)
                || !string.Equals(candidateInput, inputName, StringComparison.Ordinal))
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

    static bool IsPowerShell(Step step, byte[] utf8Yaml)
    {
        if (step.Exec is not ExecRun run || run.Shell is null || run.Shell.Expression is not null)
        {
            return false;
        }

        var shell = Encoding.UTF8.GetString(run.Shell.Value.AsSpan(utf8Yaml));
        return string.Equals(shell, "pwsh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase);
    }

    static bool TryExtractExpressionBody(StringNode node, byte[] utf8Yaml, out ReadOnlySpan<byte> expressionBody)
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

    static bool TryExtractEmbeddedExpressionBody(ReadOnlySpan<byte> value, out ReadOnlySpan<byte> expressionBody)
    {
        expressionBody = [];
        if (!value.StartsWith("${{"u8) || !value.EndsWith("}}"u8))
        {
            return false;
        }

        expressionBody = TrimAsciiWhiteSpace(value.Slice(3, value.Length - 5));
        return expressionBody.Length > 0;
    }

    static bool TryParseSimpleInputsReference(ReadOnlySpan<byte> expression, out string inputName)
    {
        inputName = string.Empty;

        var index = 0;
        if (TryConsumeSimpleInputsRoot(expression, ref index))
        {
            return TryConsumeMemberOrBracketName(expression, ref index, out inputName);
        }

        index = 0;
        if (!TryConsumeGithubEventInputsRoot(expression, ref index))
        {
            return false;
        }

        return TryConsumeMemberOrBracketName(expression, ref index, out inputName);
    }

    static bool TryConsumeSimpleInputsRoot(ReadOnlySpan<byte> expression, ref int index)
    {
        if (!ConsumeWordIgnoreCase(expression, ref index, "inputs"u8))
        {
            return false;
        }

        SkipWhiteSpace(expression, ref index);
        return true;
    }

    static bool TryConsumeGithubEventInputsRoot(ReadOnlySpan<byte> expression, ref int index)
    {
        if (!ConsumeWordIgnoreCase(expression, ref index, "github"u8))
        {
            return false;
        }

        SkipWhiteSpace(expression, ref index);
        if (index >= expression.Length || expression[index] != (byte)'.')
        {
            return false;
        }

        index++;
        SkipWhiteSpace(expression, ref index);
        if (!ConsumeWordIgnoreCase(expression, ref index, "event"u8))
        {
            return false;
        }

        SkipWhiteSpace(expression, ref index);
        if (index >= expression.Length || expression[index] != (byte)'.')
        {
            return false;
        }

        index++;
        SkipWhiteSpace(expression, ref index);
        if (!ConsumeWordIgnoreCase(expression, ref index, "inputs"u8))
        {
            return false;
        }

        SkipWhiteSpace(expression, ref index);
        return true;
    }

    static bool TryConsumeMemberOrBracketName(ReadOnlySpan<byte> expression, ref int index, out string name)
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

    static bool ConsumeWordIgnoreCase(ReadOnlySpan<byte> value, ref int index, ReadOnlySpan<byte> word)
    {
        if (index + word.Length > value.Length)
        {
            return false;
        }

        for (var i = 0; i < word.Length; i++)
        {
            var l = value[index + i];
            var r = word[i];
            if (l is >= (byte)'A' and <= (byte)'Z')
            {
                l = (byte)(l + 32);
            }

            if (l != r)
            {
                return false;
            }
        }

        index += word.Length;
        return true;
    }

    static void SkipWhiteSpace(ReadOnlySpan<byte> value, ref int index)
    {
        while (index < value.Length && IsWhiteSpace(value[index]))
        {
            index++;
        }
    }

    static bool TryReadIdentifier(ReadOnlySpan<byte> value, ref int index, out string identifier)
    {
        identifier = string.Empty;
        if (index >= value.Length || !IsIdentifierStart(value[index]))
        {
            return false;
        }

        var start = index;
        index++;
        while (index < value.Length && IsIdentifierPart(value[index]))
        {
            index++;
        }

        identifier = Encoding.UTF8.GetString(value[start..index]);
        return true;
    }

    static bool IsSimpleIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (!IsIdentifierStart((byte)value[0]))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (!IsIdentifierPart((byte)value[i]))
            {
                return false;
            }
        }

        return true;
    }

    static bool IsIdentifierStart(byte b)
    {
        return (b >= (byte)'A' && b <= (byte)'Z')
            || (b >= (byte)'a' && b <= (byte)'z')
            || b == (byte)'_';
    }

    static bool IsIdentifierPart(byte b)
    {
        return IsIdentifierStart(b) || (b >= (byte)'0' && b <= (byte)'9');
    }

    static bool ContainsInputsReference(
        int nodeId,
        int parentId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];

        // Case 1: root `inputs` identifier ? covers ${{ inputs.* }} and ${{ inputs['*'] }}
        if (node.Kind == ExpressionNodeKind.Identifier
            && IsContextRootIdentifier(nodeId, parentId, nodes)
            && EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), "inputs"u8))
        {
            return true;
        }

        // Case 2: accessing a property or index of github.event.inputs ? covers ${{ github.event.inputs.* }}
        if (node.Kind is ExpressionNodeKind.MemberAccess
            or ExpressionNodeKind.IndexAccess
            or ExpressionNodeKind.WildcardAccess)
        {
            if (IsGithubEventInputsChain(node.Left, nodes, expression))
            {
                return true;
            }
        }

        return node.Kind switch
        {
            ExpressionNodeKind.Unary => ContainsInputsReference(node.Left, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.Binary => ContainsInputsReference(node.Left, nodeId, nodes, arguments, expression)
                || ContainsInputsReference(node.Right, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.MemberAccess => ContainsInputsReference(node.Left, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.WildcardAccess => ContainsInputsReference(node.Left, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.IndexAccess => ContainsInputsReference(node.Left, nodeId, nodes, arguments, expression)
                || ContainsInputsReference(node.Right, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.FunctionCall => ContainsInputsReferenceInFunction(node, nodeId, nodes, arguments, expression),
            _ => false,
        };
    }

    static bool ContainsInputsReferenceInFunction(
        ExpressionNode functionCallNode,
        int functionCallNodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression)
    {
        if (ContainsInputsReference(functionCallNode.Left, functionCallNodeId, nodes, arguments, expression))
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

            if (ContainsInputsReference(arguments[argIndex], functionCallNodeId, nodes, arguments, expression))
            {
                return true;
            }
        }

        return false;
    }

    // Returns true when nodeId represents the `github.event.inputs` member-access chain.
    // That is: MemberAccess(token="inputs", left=MemberAccess(token="event", left=Identifier("github")))
    static bool IsGithubEventInputsChain(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind != ExpressionNodeKind.MemberAccess)
        {
            return false;
        }

        if (!EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), "inputs"u8))
        {
            return false;
        }

        return IsGithubEventChain(node.Left, nodes, expression);
    }

    // Returns true when nodeId represents `github.event`: MemberAccess(token="event", left=Identifier("github"))
    static bool IsGithubEventChain(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind != ExpressionNodeKind.MemberAccess)
        {
            return false;
        }

        if (!EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), "event"u8))
        {
            return false;
        }

        return IsIdentifierNode(node.Left, nodes, expression, "github"u8);
    }

    static bool IsIdentifierNode(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression, ReadOnlySpan<byte> expected)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        return node.Kind == ExpressionNodeKind.Identifier
            && EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), expected);
    }

    static bool IsContextRootIdentifier(int nodeId, int parentId, ExpressionNode[] nodes)
    {
        if (parentId < 0)
        {
            return true;
        }

        if (parentId >= nodes.Length)
        {
            return false;
        }

        var parent = nodes[parentId];
        return parent.Left == nodeId
            && (parent.Kind == ExpressionNodeKind.MemberAccess
                || parent.Kind == ExpressionNodeKind.IndexAccess
                || parent.Kind == ExpressionNodeKind.WildcardAccess);
    }

    static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i];
            var r = right[i];
            if (l is >= (byte)'A' and <= (byte)'Z')
            {
                l = (byte)(l + 32);
            }

            if (r is >= (byte)'A' and <= (byte)'Z')
            {
                r = (byte)(r + 32);
            }

            if (l != r)
            {
                return false;
            }
        }

        return true;
    }

    static bool TryFindExpression(
        ReadOnlySpan<byte> value,
        int searchStart,
        out int bodyStart,
        out int bodyLength,
        out int nextSearchStart)
    {
        bodyStart = 0;
        bodyLength = 0;
        nextSearchStart = 0;

        if ((uint)searchStart >= (uint)value.Length)
        {
            return false;
        }

        var start = value[searchStart..].IndexOf("${{"u8);
        if (start < 0)
        {
            return false;
        }

        bodyStart = searchStart + start + 3;
        var close = value[bodyStart..].IndexOf("}}"u8);
        if (close < 0)
        {
            return false;
        }

        bodyLength = close;
        nextSearchStart = bodyStart + close + 2;
        return true;
    }

    static ReadOnlySpan<byte> TrimAsciiWhiteSpace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length - 1;
        while (start <= end && IsWhiteSpace(value[start]))
        {
            start++;
        }

        while (end >= start && IsWhiteSpace(value[end]))
        {
            end--;
        }

        return end < start ? [] : value.Slice(start, end - start + 1);
    }

    static int[] BuildLineStarts(byte[] source)
    {
        var starts = new List<int>(64) { 0 };
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == (byte)'\n')
            {
                var next = i + 1;
                if (next < source.Length)
                {
                    starts.Add(next);
                }
            }
        }

        return starts.ToArray();
    }

    static (int Line, int Column) OffsetToLineColumn(int[] lineStarts, int offset)
    {
        var idx = Array.BinarySearch(lineStarts, offset);
        if (idx >= 0)
        {
            return (idx + 1, 1);
        }

        idx = ~idx - 1;
        if (idx < 0)
        {
            return (1, offset + 1);
        }

        return (idx + 1, offset - lineStarts[idx] + 1);
    }

    static bool IsWhiteSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
