using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;
using static Seiton.Core.Linting.Rules.RunContextDirectUseAnalyzer;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags direct use of <c>env.*</c> context in <c>run:</c> scripts where shell environment variables should be used instead.</summary>
public sealed class RunEnvContextDirectUseRule() : RuleBase(RuleId.RunEnvContextDirectUse)
{
    private Workflow? _currentWorkflow;
    private Job? _currentJob;

    public override string Name => "Run Env Context Direct Use Rule";

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

        CheckRunNode(step, run, run.Run);
    }

    private void CheckRunNode(Step step, ExecRun run, StringNodeId runNode)
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

            if (!ContainsContextRootReference(
                parseResult.RootNode,
                parentId: -1,
                parseResult.Nodes,
                parseResult.Arguments,
                expression,
                "env"u8))
            {
                continue;
            }

            // Skip detection inside no-expand heredoc (<<'EOF') where shell variables don't expand
            var absoluteOffset = Arena.GetStringSlice(runNode).Offset + bodyStart - 3;
            if (IsInsideNoExpandHereDoc(Config.Utf8Yaml, absoluteOffset))
            {
                continue;
            }

            if (TryBuildFix(step, runNode, expression, bodyStart, nextSearchStart - (bodyStart - 3), out var fix))
            {
                AddStepError(
                    step,
                    "run script must not reference ${{ env.* }} directly; use shell variables instead (e.g. $NAME or $env:NAME)",
                    location,
                    fix);
            }
            else if (!TryParseSimpleContextReference(expression, "env"u8, out _))
            {
                // Composite expression (e.g. "${{ env.FOO }}-suffix") — suggest env: block mapping
                AddStepError(
                    step,
                    "run script must not reference ${{ env.* }} directly; use shell variables instead (e.g. $NAME or $env:NAME)",
                    location,
                    "consider moving the entire expression to an env: block and referencing the shell variable instead");
            }
            else
            {
                AddStepError(
                    step,
                    "run script must not reference ${{ env.* }} directly; use shell variables instead (e.g. $NAME or $env:NAME)",
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

        var absoluteOffset = Arena.GetStringSlice(runNode).Offset + expressionBodyStart - 3;
        if (IsInsideNoExpandHereDoc(Config.Utf8Yaml, absoluteOffset))
        {
            return false;
        }

        if (!TryParseSimpleContextReference(expression, "env"u8, out var variableName))
        {
            return false;
        }

        var isPowerShell = RunContextDirectUseAnalyzer.IsPowerShellWithDefaults(Arena, step, _currentJob, _currentWorkflow, Config.Utf8Yaml);
        if (isPowerShell is null)
        {
            return false;
        }

        var replacement = isPowerShell.Value
            ? "$env:" + variableName
            : "${" + variableName + "}";

        if (IsInsideShellSingleQuotes(Config.Utf8Yaml, absoluteOffset))
        {
            if (!TryBuildSingleQuotedSimpleEdit(Config.Utf8Yaml, absoluteOffset, expressionLength, replacement, out var singleQuotedEdit))
            {
                return false;
            }

            fix = new DiagnosticFix(
                "replace direct env context expansion with shell variable",
                [singleQuotedEdit]);
            return true;
        }

        fix = new DiagnosticFix(
            "replace direct env context expansion with shell variable",
            [new TextEdit(absoluteOffset, expressionLength, replacement)]);
        return true;
    }

    private static bool TryBuildSingleQuotedSimpleEdit(byte[] source, int absoluteOffset, int expressionLength, string replacement, out TextEdit edit)
    {
        edit = default;
        if ((uint)absoluteOffset >= (uint)source.Length || expressionLength <= 0)
        {
            return false;
        }

        var singleQuoteStart = absoluteOffset - 1;
        var singleQuoteEnd = absoluteOffset + expressionLength;
        if ((uint)singleQuoteStart >= (uint)source.Length || (uint)singleQuoteEnd >= (uint)source.Length)
        {
            return false;
        }

        if (source[singleQuoteStart] != (byte)'\'' || source[singleQuoteEnd] != (byte)'\'')
        {
            return false;
        }

        edit = new TextEdit(singleQuoteStart, expressionLength + 2, "\"" + replacement + "\"");
        return true;
    }

    private static bool IsInsideNoExpandHereDoc(byte[] source, int targetOffset)
        => RunContextDirectUseAnalyzer.IsInsideNoExpandHereDoc(source, targetOffset);
}
