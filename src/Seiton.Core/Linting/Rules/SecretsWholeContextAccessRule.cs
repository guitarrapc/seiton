using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>
/// Detects expressions that reference the entire secrets context as an object (e.g. ${{ toJson(secrets) }},
/// ${{ format('{0}', secrets) }}), rather than accessing a specific key (secrets.MY_KEY).
/// Exposing the whole secrets context leaks every secret at once and is a high-severity supply-chain risk.
/// </summary>
public sealed class SecretsWholeContextAccessRule : RuleBase
{
    const string DiagnosticMessage =
        "expression must not reference the entire ${{ secrets }} context object; " +
        "use ${{ secrets.SPECIFIC_KEY }} to access individual secrets, then map them to env variables";

    public override string Id => "secrets-whole-context-access";

    public override string Name => "Secrets Whole Context Access Rule";

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        // Check run: script
        if (step.Exec is ExecRun run)
        {
            CheckNode(run.Run, sinkName: "run", static (rule, location, s) =>
                rule.AddStepError(s, DiagnosticMessage, location), step);
        }

        // Check step-level env:
        CheckEnvForStep(step.Env, step);

        // Check step with: inputs (only for use:actions steps)
        if (step.Exec is ExecAction action && action.Inputs is not null && action.Inputs.Count > 0)
        {
            foreach (var pair in action.Inputs)
            {
                var inputName = Decode(pair.Key);
                CheckNode(pair.Value, sinkName: $"with.{inputName}", static (rule, location, s) =>
                    rule.AddStepError(s, DiagnosticMessage, location), step);
            }
        }
    }

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        // Check job-level env:
        CheckEnvForJob(job.Env, job);

        // Check job with: (reusable-workflow call inputs)
        var callInputs = job.WorkflowCall?.Inputs;
        if (callInputs is null || callInputs.Count == 0)
        {
            return;
        }

        foreach (var pair in callInputs)
        {
            var inputName = Decode(pair.Value.Name.Value);
            CheckNode(pair.Value.Value, sinkName: $"with.{inputName}", static (rule, location, j) =>
                rule.AddJobError(j, DiagnosticMessage, location), job);
        }
    }

    // Step-level env helper

    void CheckEnvForStep(Env? env, Step step)
    {
        if (env is null)
        {
            return;
        }

        CheckNode(env.Expression, sinkName: "env", static (rule, location, s) =>
            rule.AddStepError(s, DiagnosticMessage, location), step);

        var vars = env.Vars;
        if (vars is null || vars.Count == 0)
        {
            return;
        }

        foreach (var pair in vars)
        {
            var keyName = Decode(pair.Value.Name.Value);
            CheckNode(pair.Value.Value, sinkName: $"env.{keyName}", static (rule, location, s) =>
                rule.AddStepError(s, DiagnosticMessage, location), step);
        }
    }

    // Job-level env helper

    void CheckEnvForJob(Env? env, Job job)
    {
        if (env is null)
        {
            return;
        }

        CheckNode(env.Expression, sinkName: "env", static (rule, location, j) =>
            rule.AddJobError(j, DiagnosticMessage, location), job);

        var vars = env.Vars;
        if (vars is null || vars.Count == 0)
        {
            return;
        }

        foreach (var pair in vars)
        {
            var keyName = Decode(pair.Value.Name.Value);
            CheckNode(pair.Value.Value, sinkName: $"env.{keyName}", static (rule, location, j) =>
                rule.AddJobError(j, DiagnosticMessage, location), job);
        }
    }

    // Core expression scanning

    void CheckNode<TTarget>(
        StringNode? node,
        string sinkName,
        Action<SecretsWholeContextAccessRule, TextRange, TTarget> report,
        TTarget target)
    {
        if (node is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var value = node.Value.AsSpan(Config.Utf8Yaml);
        if (value.Length == 0)
        {
            return;
        }

        var searchStart = 0;
        while (TryFindExpression(value, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;

            var expression = TrimAsciiWhiteSpace(value.Slice(bodyStart, bodyLength));
            if (expression.Length == 0)
            {
                continue;
            }

            var parseResult = ExpressionParser.Parse(expression);
            if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
            {
                continue;
            }

            if (!ContainsSecretsWholeContextReference(
                    parseResult.RootNode,
                    parentId: -1,
                    parseResult.Nodes,
                    parseResult.Arguments,
                    expression))
            {
                continue;
            }

            report(this, node.Range, target);
            return;
        }
    }

    // AST traversal

    static bool ContainsSecretsWholeContextReference(
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

        // Flag: secrets identifier that is NOT the base of a specific key access (secrets.KEY, secrets['KEY'])
        if (node.Kind == ExpressionNodeKind.Identifier
            && EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), "secrets"u8)
            && IsWholeContextAccess(nodeId, parentId, nodes))
        {
            return true;
        }

        return node.Kind switch
        {
            ExpressionNodeKind.Unary =>
                ContainsSecretsWholeContextReference(node.Left, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.Binary =>
                ContainsSecretsWholeContextReference(node.Left, nodeId, nodes, arguments, expression)
                || ContainsSecretsWholeContextReference(node.Right, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.MemberAccess =>
                ContainsSecretsWholeContextReference(node.Left, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.WildcardAccess =>
                ContainsSecretsWholeContextReference(node.Left, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.IndexAccess =>
                ContainsSecretsWholeContextReference(node.Left, nodeId, nodes, arguments, expression)
                || ContainsSecretsWholeContextReference(node.Right, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.FunctionCall =>
                ContainsSecretsWholeContextReferenceInFunction(node, nodeId, nodes, arguments, expression),
            _ => false,
        };
    }

    static bool ContainsSecretsWholeContextReferenceInFunction(
        ExpressionNode functionCallNode,
        int functionCallNodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression)
    {
        // Check the function name expression (left child)
        if (ContainsSecretsWholeContextReference(functionCallNode.Left, functionCallNodeId, nodes, arguments, expression))
        {
            return true;
        }

        // Check each argument: secrets passed directly as an argument is a whole-context reference
        for (var i = 0; i < functionCallNode.ArgCount; i++)
        {
            var argIndex = functionCallNode.ArgStart + i;
            if (argIndex < 0 || argIndex >= arguments.Length)
            {
                continue;
            }

            if (ContainsSecretsWholeContextReference(arguments[argIndex], functionCallNodeId, nodes, arguments, expression))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when the secrets identifier is used as a whole context object.
    /// Returns false only when secrets is the left child of MemberAccess/IndexAccess/WildcardAccess,
    /// which means a specific key is being accessed (secrets.KEY or secrets['KEY']).
    /// </summary>
    static bool IsWholeContextAccess(int nodeId, int parentId, ExpressionNode[] nodes)
    {
        if (parentId >= 0 && parentId < nodes.Length)
        {
            var parent = nodes[parentId];
            if (parent.Left == nodeId
                && (parent.Kind == ExpressionNodeKind.MemberAccess
                    || parent.Kind == ExpressionNodeKind.IndexAccess
                    || parent.Kind == ExpressionNodeKind.WildcardAccess))
            {
                // secrets.KEY or secrets['KEY'] ? specific key access, not a whole-context reference
                return false;
            }
        }

        // All other contexts: standalone, function argument, binary operand, etc.
        return true;
    }

    // Shared utilities
}
