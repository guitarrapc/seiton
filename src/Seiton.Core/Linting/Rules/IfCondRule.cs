using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Checks <c>if:</c> conditions for common mistakes (e.g. missing expression delimiters, always-true patterns).</summary>
public sealed class IfCondRule() : RuleBase(RuleId.IfCond)
{
    public override string Name => "If Condition Rule";

    public override void VisitJobPre(Job job)
    {
        ValidateCondition(job.If, job, null);
    }

    public override void VisitStep(Step step)
    {
        ValidateCondition(step.If, null, step);
    }

    private void ValidateCondition(StringNodeId condition, Job? job, Step? step)
    {
        if (!condition.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var raw = Arena.GetStringValue(condition);
        if (raw.Length == 0)
        {
            return;
        }

        // Detect "always true" pattern: value contains ${{ }} but has extra characters around it.
        // GitHub Actions evaluates the entire string as a template, producing a non-empty string → always truthy.
        // Examples: "${{ expr }}\n" (block scalar), "${{ expr }} " (trailing space), "${{ e1 }} && ${{ e2 }}"
        if (IsAlwaysTrueTemplate(raw))
        {
            if (job is not null)
            {
                AddJobWarning(job, "job if condition is always evaluated to true because extra characters are around ${{ }}", Arena.GetStringRange(condition));
            }

            if (step is not null)
            {
                AddStepWarning(step, "step if condition is always evaluated to true because extra characters are around ${{ }}", Arena.GetStringRange(condition));
            }

            return;
        }

        var expression = ExpressionScanHelpers.TryExtractExpressionBody(raw, out var body) ? body : raw;

        var parseResult = Config.ParseExpression(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            if (job is not null)
            {
                AddJobWarning(job, "job if condition contains syntax errors", Arena.GetStringRange(condition));
            }

            if (step is not null)
            {
                AddStepWarning(step, "step if condition contains syntax errors", Arena.GetStringRange(condition));
            }

            return;
        }

        if (IsConstantBool(parseResult.RootNode, parseResult.Nodes, expression, out var value))
        {
            var boolText = value ? "true" : "false";
            if (job is not null)
            {
                AddJobWarning(job, $"job if condition is always {boolText}", Arena.GetStringRange(condition));
            }

            if (step is not null)
            {
                AddStepWarning(step, $"step if condition is always {boolText}", Arena.GetStringRange(condition));
            }
        }
    }

    private static bool IsConstantBool(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression, out bool value)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            value = false;
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind == ExpressionNodeKind.BooleanLiteral)
        {
            var token = node.Token.AsSpan(expression);
            if (token.SequenceEqual("true"u8))
            {
                value = true;
                return true;
            }

            if (token.SequenceEqual("false"u8))
            {
                value = false;
                return true;
            }
        }

        if (node.Kind == ExpressionNodeKind.Unary && node.Operator == ExpressionOperator.Not)
        {
            if (IsConstantBool(node.Left, nodes, expression, out var child))
            {
                value = !child;
                return true;
            }
        }

        if (node.Kind == ExpressionNodeKind.Binary)
        {
            if (node.Operator == ExpressionOperator.And
                && IsConstantBool(node.Left, nodes, expression, out var leftAnd)
                && IsConstantBool(node.Right, nodes, expression, out var rightAnd))
            {
                value = leftAnd && rightAnd;
                return true;
            }

            if (node.Operator == ExpressionOperator.Or
                && IsConstantBool(node.Left, nodes, expression, out var leftOr)
                && IsConstantBool(node.Right, nodes, expression, out var rightOr))
            {
                value = leftOr || rightOr;
                return true;
            }
        }

        value = false;
        return false;
    }

    /// <summary>
    /// Detects "always evaluated to true" patterns where <c>${{ }}</c> is present but extra characters
    /// are around it (leading text, trailing newline/space, or multiple expression blocks).
    /// GitHub Actions treats such values as string templates that produce non-empty strings → always truthy.
    /// </summary>
    private static bool IsAlwaysTrueTemplate(ReadOnlySpan<byte> value)
    {
        var firstOpen = value.IndexOf("${{"u8);
        if (firstOpen < 0)
        {
            return false; // No expression delimiter at all
        }

        // Leading text before first ${{ → always true
        if (firstOpen > 0)
        {
            return true;
        }

        // firstOpen == 0: starts with ${{
        // Find the first matching }}
        var firstClose = value.Slice(3).IndexOf("}}"u8);
        if (firstClose < 0)
        {
            return false; // Malformed, let syntax error path handle it
        }

        firstClose += 3; // Adjust to absolute position

        var tail = firstClose + 2;

        // Check for another ${{ after the first }} → multiple expression blocks → always true
        if (tail < value.Length && ExpressionScanHelpers.ContainsExpressionMarker(value.Slice(tail)))
        {
            return true;
        }

        // Check trailing characters after }}: any characters at all mean "extra characters around ${{ }}".
        // A clean expression wrapper has nothing after }}.
        if (tail < value.Length)
        {
            return true;
        }

        return false;
    }
}
