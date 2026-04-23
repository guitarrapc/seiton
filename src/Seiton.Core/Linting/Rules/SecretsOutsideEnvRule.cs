using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags direct use of <c>secrets.*</c> outside <c>env:</c> blocks where they should be bound.</summary>
public sealed class SecretsOutsideEnvRule() : RuleBase(RuleId.SecretsOutsideEnv)
{
    public override string Name => "Secrets Outside Env Rule";

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        if (ContainsSecretsReference(job.If))
        {
            AddJobWarning(
                job,
                "job.if must not reference secrets context directly; map secrets to env variables and gate with non-secret signals",
                Arena.GetStringRange(job.If));
            return;
        }

        if (job.WorkflowCall?.Inputs is null || job.WorkflowCall.Inputs.Value.Count == 0)
        {
            return;
        }

        foreach (var pair in job.WorkflowCall.Inputs.Value)
        {
            var value = pair.Value.Value;
            if (!ContainsSecretsReference(value))
            {
                continue;
            }

            var inputName = Decode(Arena.GetStringSlice(pair.Value.Name));
            AddJobWarning(
                job,
                $"reusable workflow input '{inputName}' must not consume secrets context directly outside env handoff",
                Arena.GetStringRange(value));
            return;
        }
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        if (ContainsSecretsReference(step.If))
        {
            AddStepWarning(
                step,
                "step.if must not reference secrets context directly; map secrets to env variables and gate with non-secret signals",
                Arena.GetStringRange(step.If));
            return;
        }

        if (step.Exec is ExecAction action)
        {
            if (ContainsSecretsReference(action.Uses))
            {
                AddStepWarning(
                    step,
                    "action uses must not interpolate secrets context directly outside env handoff",
                    Arena.GetStringRange(action.Uses));
                return;
            }
        }
    }

    private bool ContainsSecretsReference(StringNodeId node)
    {
        if (Config.Utf8Yaml is null || !node.HasValue)
        {
            return false;
        }

        if (ContainsSecretsReferenceInValue(Arena.GetStringValue(node)))
        {
            return true;
        }

        if (!Arena.GetStringExpression(node).HasValue)
        {
            return false;
        }

        var expression = TrimAsciiWhiteSpace(Arena.GetStringValue(Arena.GetStringExpression(node)));
        return ContainsSecretsReferenceInExpression(expression);
    }

    private bool ContainsSecretsReferenceInValue(ReadOnlySpan<byte> value)
    {
        var searchStart = 0;
        while (TryFindExpression(value, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;
            var expression = TrimAsciiWhiteSpace(value.Slice(bodyStart, bodyLength));
            if (ContainsSecretsReferenceInExpression(expression))
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsSecretsReferenceInExpression(ReadOnlySpan<byte> expression)
    {
        if (expression.Length == 0)
        {
            return false;
        }

        var parseResult = Config.ParseExpression(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            return false;
        }

        return ContainsSecretsReference(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);
    }

    private static bool ContainsSecretsReference(int nodeId, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind == ExpressionNodeKind.Identifier
            && EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), "secrets"u8))
        {
            return true;
        }

        return node.Kind switch
        {
            ExpressionNodeKind.Unary => ContainsSecretsReference(node.Left, nodes, arguments, expression),
            ExpressionNodeKind.Binary => ContainsSecretsReference(node.Left, nodes, arguments, expression)
                || ContainsSecretsReference(node.Right, nodes, arguments, expression),
            ExpressionNodeKind.MemberAccess => ContainsSecretsReference(node.Left, nodes, arguments, expression)
                || ContainsSecretsReference(node.Right, nodes, arguments, expression),
            ExpressionNodeKind.WildcardAccess => ContainsSecretsReference(node.Left, nodes, arguments, expression),
            ExpressionNodeKind.IndexAccess => ContainsSecretsReference(node.Left, nodes, arguments, expression)
                || ContainsSecretsReference(node.Right, nodes, arguments, expression),
            ExpressionNodeKind.FunctionCall => ContainsSecretsReferenceInFunctionCall(node, nodes, arguments, expression),
            _ => false,
        };
    }

    private static bool ContainsSecretsReferenceInFunctionCall(ExpressionNode functionCallNode, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression)
    {
        if (ContainsSecretsReference(functionCallNode.Left, nodes, arguments, expression))
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

            if (ContainsSecretsReference(arguments[argIndex], nodes, arguments, expression))
            {
                return true;
            }
        }

        return false;
    }
}
