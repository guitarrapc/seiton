using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>
/// Flags <c>if:</c> conditions using block scalars (<c>|</c> or <c>&gt;</c>) with fenced expressions
/// that evaluate to truthy due to trailing whitespace (e.g. newline from clip chomping).
/// The fix is to use strip chomping (<c>|-</c> or <c>&gt;-</c>).
/// </summary>
public sealed class UnsoundConditionRule() : RuleBase(RuleId.UnsoundCondition)
{
    private const string FixDescriptionLiteral = "replace '|' with '|-' to strip trailing newline";
    private const string FixDescriptionFolded = "replace '>' with '>-' to strip trailing newline";

    public override string Name => "Unsound Condition Rule";

    public override void VisitJobPre(JobRef job)
    {
        ValidateCondition(job.If, job, default, job.IfKeyRange);

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

        // Only flag block scalars: raw value has trailing newline from clip chomping
        if (raw[raw.Length - 1] != (byte)'\n')
        {
            return;
        }

        // Must be a fenced expression: the value (ignoring trailing whitespace) is wrapped in ${{ }}
        if (!ExpressionScanHelpers.TryExtractExpressionBody(raw, out _))
        {
            return;
        }

        // The raw string length exceeds what ${{ body }} alone would produce,
        // indicating trailing content (newline) makes the condition always truthy.
        // Since TryExtractExpressionBody already handles trailing whitespace internally,
        // and we confirmed trailing \n exists, this is an unsound condition.

        // Find the block indicator position for diagnostic location and fix
        var diagRange = condition.Range;
        var indicatorInfo = FindBlockIndicator(diagRange, ifKeyRange);
        if (indicatorInfo is not null)
        {
            diagRange = indicatorInfo.Value.Range;
        }

        var message = "if: condition uses block scalar with fenced expression; trailing newline makes it always truthy; use strip chomping (|- or >-)";

        DiagnosticFix? fix = null;
        if (indicatorInfo is not null)
        {
            fix = BuildFix(indicatorInfo.Value);
        }

        if (job.HasValue)
        {
            if (fix is { } f)
            {
                AddJobWarning(job, message, diagRange, f);
            }
            else
            {
                AddJobWarning(job, message, diagRange);
            }
        }

        if (step.HasValue)
        {
            if (fix is { } f)
            {
                AddStepWarning(step, message, diagRange, f);
            }
            else
            {
                AddStepWarning(step, message, diagRange);
            }
        }
    }

    private readonly record struct IndicatorInfo(TextRange Range, bool IsLiteral, int ByteOffset);

    private IndicatorInfo? FindBlockIndicator(TextRange contentRange, TextRange? ifKeyRange)
    {
        if (ifKeyRange is not { } kr || Config.Utf8Yaml is null)
        {
            return null;
        }

        var yaml = Config.Utf8Yaml;
        var contentStart = contentRange.Start;

        // Scan backward from content start past whitespace/newlines/chomping chars to find | or >
        for (var i = contentStart - 1; i >= kr.Start; i--)
        {
            var b = yaml[i];
            if (b == (byte)'|' || b == (byte)'>')
            {
                // Compute location
                var lineStart = i;
                while (lineStart > 0 && yaml[lineStart - 1] != (byte)'\n')
                {
                    lineStart--;
                }

                var col = i - lineStart + 1;
                var range = new TextRange(i, 1, kr.StartLine, col, kr.StartLine, col + 1);
                return new IndicatorInfo(range, b == (byte)'|', i);
            }

            if (b != (byte)' ' && b != (byte)'\n' && b != (byte)'\r' && b != (byte)'-' && b != (byte)'+' && !(b >= (byte)'0' && b <= (byte)'9'))
            {
                break;
            }
        }

        return null;
    }

    private DiagnosticFix BuildFix(IndicatorInfo info)
    {
        var yaml = Config.Utf8Yaml!;
        // Check if there's already a chomping indicator after | or >
        var afterIndicator = info.ByteOffset + 1;
        if (afterIndicator < yaml.Length && yaml[afterIndicator] == (byte)'-')
        {
            // Already has strip chomping, no fix needed (shouldn't reach here but be safe)
            return new DiagnosticFix(
                info.IsLiteral ? FixDescriptionLiteral : FixDescriptionFolded,
                [new TextEdit(info.ByteOffset, 2, info.IsLiteral ? "|-" : ">-")]);
        }

        // Insert '-' after the indicator
        var description = info.IsLiteral ? FixDescriptionLiteral : FixDescriptionFolded;
        var edit = new TextEdit(info.ByteOffset, 1, info.IsLiteral ? "|-" : ">-");
        return new DiagnosticFix(description, [edit]);
    }
}
