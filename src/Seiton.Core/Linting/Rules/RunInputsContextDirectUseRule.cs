using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;
using static Seiton.Core.Linting.Rules.RunContextDirectUseAnalyzer;

namespace Seiton.Core.Linting.Rules;

public sealed class RunInputsContextDirectUseRule() : RuleBase(RuleId.RunInputsContextDirectUse)
{
    private Workflow? _currentWorkflow;
    private Job? _currentJob;

    public override string Name => "Run Inputs Context Direct Use Rule";

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
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

    private void CheckRunNode(Step step, StringNodeId runNode)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        var runText = Arena.GetStringValue(runNode);
        var searchStart = 0;
        while (TryFindExpression(runText, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;
            var location = BuildExpressionLocation(Arena, Config.Utf8Yaml, runNode, bodyStart, nextSearchStart, Config.GetLineStarts());

            var expression = TrimAsciiWhiteSpace(runText.Slice(bodyStart, bodyLength));
            if (expression.Length == 0)
            {
                continue;
            }

            var parseResult = Config.ParseExpression(expression);
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

    private bool TryBuildFix(Step step, StringNodeId runNode, ReadOnlySpan<byte> expression, int expressionBodyStart, int expressionLength, out DiagnosticFix fix)
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

        if (!TryResolveShellVariableName(Arena, step.Env, _currentJob?.Env, _currentWorkflow?.Env,
            Config.Utf8Yaml, inputName, TryParseSimpleInputsReference, out var variableName))
        {
            return false;
        }

        var replacement = RunContextDirectUseAnalyzer.IsPowerShell(Arena, step, Config.Utf8Yaml)
            ? "$env:" + variableName
            : "${" + variableName + "}";

        var absoluteOffset = Arena.GetStringSlice(runNode).Offset + expressionBodyStart - 3;
        fix = new DiagnosticFix(
            "replace direct inputs context expansion with mapped shell variable",
            [new TextEdit(absoluteOffset, expressionLength, replacement)]);
        return true;
    }

    // Inputs-specific reference parsing

    private static bool TryParseSimpleInputsReference(ReadOnlySpan<byte> expression, out string inputName)
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

    private static bool TryConsumeSimpleInputsRoot(ReadOnlySpan<byte> expression, ref int index)
    {
        if (!ConsumeWordIgnoreCase(expression, ref index, "inputs"u8))
        {
            return false;
        }

        SkipWhiteSpace(expression, ref index);
        return true;
    }

    private static bool TryConsumeGithubEventInputsRoot(ReadOnlySpan<byte> expression, ref int index)
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

    // Inputs-specific AST detection

    private static bool ContainsInputsReference(
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

        // Case 1: root `inputs` identifier — covers ${{ inputs.* }} and ${{ inputs['*'] }}
        if (node.Kind == ExpressionNodeKind.Identifier
            && IsContextRootIdentifier(nodeId, parentId, nodes)
            && EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), "inputs"u8))
        {
            return true;
        }

        // Case 2: accessing a property or index of github.event.inputs — covers ${{ github.event.inputs.* }}
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

    private static bool ContainsInputsReferenceInFunction(
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

    private static bool IsGithubEventInputsChain(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression)
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

    private static bool IsGithubEventChain(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression)
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

    private static bool IsIdentifierNode(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression, ReadOnlySpan<byte> expected)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        return node.Kind == ExpressionNodeKind.Identifier
            && EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), expected);
    }
}
