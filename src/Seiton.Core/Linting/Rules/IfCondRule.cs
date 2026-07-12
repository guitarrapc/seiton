using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Checks <c>if:</c> conditions for common mistakes (e.g. missing expression delimiters, always-true patterns).</summary>
public sealed class IfCondRule() : RuleBase(RuleId.IfCond)
{
    public override string Name => "If Condition Rule";

    public override void VisitJobPre(JobRef job)
    {
        ValidateCondition(job.If, job, default, job.IfKeyRange);

        // snapshot.if
        var snapshot = job.Snapshot;
        if (snapshot.HasValue)
        {
            ValidateCondition(snapshot.If, job, default, snapshot.IfKeyRange);
        }
    }

    public override void VisitStep(StepRef step)
    {
        ValidateCondition(step.If, default, step, step.IfKeyRange);
    }

    private void ValidateCondition(StringRef condition, JobRef job, StepRef step, TextRange? ifKeyRange)
    {
        if (!condition.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var raw = condition.Value;
        if (raw.Length == 0)
        {
            return;
        }

        // For block scalars (trailing \n), report at the block indicator position (same line as if: key)
        // instead of the content position (next line). Scan backward from content start to find | or >.
        var diagRange = condition.Range;
        if (raw[raw.Length - 1] == (byte)'\n' && ifKeyRange is { } kr)
        {
            var yaml = Config.Utf8Yaml;
            var contentStart = diagRange.Start;
            var indicatorPos = -1;

            // Scan backward from content start past whitespace/newlines to find | or >
            for (var i = contentStart - 1; i >= kr.Start; i--)
            {
                var b = yaml[i];
                if (b == (byte)'|' || b == (byte)'>')
                {
                    indicatorPos = i;
                    break;
                }

                if (b != (byte)' ' && b != (byte)'\n' && b != (byte)'\r' && b != (byte)'-' && b != (byte)'+' && !(b >= (byte)'0' && b <= (byte)'9'))
                {
                    break; // Not part of indicator syntax, bail out
                }
            }

            if (indicatorPos >= 0)
            {
                // Compute column by finding line start
                var lineStart = indicatorPos;
                while (lineStart > 0 && yaml[lineStart - 1] != (byte)'\n')
                {
                    lineStart--;
                }

                var indicatorCol = indicatorPos - lineStart + 1; // 1-based
                diagRange = new TextRange(indicatorPos, 1, kr.StartLine, indicatorCol, kr.StartLine, indicatorCol + 1);
            }
        }

        // Detect "always true" pattern: value contains ${{ }} but has extra characters around it.
        // GitHub Actions evaluates the entire string as a template, producing a non-empty string → always truthy.
        // Examples: "${{ expr }}\n" (block scalar), "${{ expr }} " (trailing space), "${{ e1 }} && ${{ e2 }}"
        if (IsAlwaysTrueTemplate(raw))
        {
            var conditionText = FormatConditionText(raw);
            var message = $"if: condition \"{conditionText}\" is always evaluated to true because extra characters are around ${{{{ }}}}";
            if (job.HasValue)
            {
                AddJobWarning(job, message, diagRange);
            }

            if (step.HasValue)
            {
                AddStepWarning(step, message, diagRange);
            }

            return;
        }

        var expression = ExpressionScanHelpers.TryExtractExpressionBody(raw, out var body) ? body : raw;

        var parseResult = Config.ParseExpression(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            if (job.HasValue)
            {
                AddJobWarning(job, "job if condition contains syntax errors", diagRange);
            }

            if (step.HasValue)
            {
                AddStepWarning(step, "step if condition contains syntax errors", diagRange);
            }

            return;
        }

        if (ExpressionConstantEvaluator.TryEvaluateConstantBool(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression, out _))
        {
            var expressionText = Encoding.UTF8.GetString(expression).Trim();
            var message = $"constant expression \"{expressionText}\" in condition. remove the if: section";
            if (job.HasValue)
            {
                AddJobWarning(job, message, diagRange);
            }

            if (step.HasValue)
            {
                AddStepWarning(step, message, diagRange);
            }
        }
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

    /// <summary>Converts raw UTF-8 condition bytes to a displayable string, escaping newlines.</summary>
    private static string FormatConditionText(ReadOnlySpan<byte> raw)
    {
        var text = Encoding.UTF8.GetString(raw);
        // Trim trailing newline that block scalars produce, but show it as \n in the message
        if (text.EndsWith('\n'))
        {
            text = text.TrimEnd('\n') + "\\n";
        }

        return text;
    }
}
