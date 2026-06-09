using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;
using static Seiton.Core.Linting.Rules.RunContextDirectUseAnalyzer;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags direct use of <c>secrets.*</c> context in <c>run:</c> scripts where environment variables should be used instead.</summary>
public sealed class RunSecretsContextDirectUseRule() : RuleBase(RuleId.RunSecretsContextDirectUse)
{
    private Workflow? _currentWorkflow;
    private Job? _currentJob;

    public override string Name => "Run Secrets Context Direct Use Rule";

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

            if (!ContainsContextRootReference(
                parseResult.RootNode,
                parentId: -1,
                parseResult.Nodes,
                parseResult.Arguments,
                expression,
                "secrets"u8))
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
                    "run script must not reference ${{ secrets.* }} directly; map secrets to env and use shell variables instead (e.g. ${TOKEN}, $TOKEN, or $env:TOKEN)",
                    location,
                    fix);
            }
            else if (!TryParseSimpleContextReferenceBounds(expression, "secrets"u8, out _, out _))
            {
                // Composite expression (e.g. "${{ secrets.TOKEN }}-suffix") — suggest env: block mapping
                AddStepError(
                    step,
                    "run script must not reference ${{ secrets.* }} directly; map secrets to env and use shell variables instead (e.g. ${TOKEN}, $TOKEN, or $env:TOKEN)",
                    location,
                    "consider moving the entire expression to an env: block and referencing the shell variable instead");
            }
            else
            {
                AddStepError(
                    step,
                    "run script must not reference ${{ secrets.* }} directly; map secrets to env and use shell variables instead (e.g. ${TOKEN}, $TOKEN, or $env:TOKEN)",
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

        if (!TryParseSimpleContextReferenceBounds(expression, "secrets"u8, out var nameStart, out var nameLength))
        {
            return false;
        }
        var secretName = DecodeExpressionName(expression, nameStart, nameLength);

        var absoluteOffset = Arena.GetStringSlice(runNode).Offset + expressionBodyStart - 3;

        if (IsInsideNoExpandHereDoc(Config.Utf8Yaml, absoluteOffset))
        {
            return false;
        }

        if (IsInsideShellSingleQuotes(Config.Utf8Yaml, absoluteOffset))
        {
            return false;
        }

        // Case 1: existing unique env mapping resolves the variable name
        if (TryResolveShellVariableName(Arena, step.Env, _currentJob?.Env, _currentWorkflow?.Env,
            Config.Utf8Yaml, secretName,
            static (ReadOnlySpan<byte> expr, out string name) => TryParseSimpleContextReference(expr, "secrets"u8, out name),
            out var variableName))
        {
            var isPowerShell = RunContextDirectUseAnalyzer.IsPowerShellWithDefaults(Arena, step, _currentJob, _currentWorkflow, Config.Utf8Yaml);
            if (isPowerShell is null)
            {
                return false;
            }

            if (!TryBuildShellVariableReplacement(variableName, isPowerShell.Value, wrapInDoubleQuotes: false, out var replacement))
            {
                return false;
            }

            fix = new DiagnosticFix(
                "replace direct secrets context expansion with mapped shell variable",
                [new TextEdit(absoluteOffset, expressionLength, replacement)]);
            return true;
        }

        // Case 2: no existing mapping — generate env var name and insert env block
        if (!Config.Fix.Enabled)
        {
            return false;
        }

        var expressionString = BuildSecretsExpressionString(secretName);
        var envVarName = DeduplicateEnvName(Arena, RunInputsContextDirectUseRule.InputNameToEnvVarName(secretName),
            step.Env, _currentJob?.Env, _currentWorkflow?.Env);
        if (envVarName is null)
        {
            return false;
        }

        var isPowerShell2 = RunContextDirectUseAnalyzer.IsPowerShellWithDefaults(Arena, step, _currentJob, _currentWorkflow, Config.Utf8Yaml);
        if (isPowerShell2 is null)
        {
            return false;
        }

        if (!TryBuildShellVariableReplacement(envVarName, isPowerShell2.Value, wrapInDoubleQuotes: false, out var shellReplacement))
        {
            return false;
        }

        if (!TryBuildStepEnvInsertionEdit(Arena, Config.Utf8Yaml, step, envVarName, expressionString, out var insertEdit))
        {
            return false;
        }

        fix = new DiagnosticFix(
            $"map secrets reference to env variable {envVarName}",
            [insertEdit, new TextEdit(absoluteOffset, expressionLength, shellReplacement)]);
        return true;
    }

    private static string BuildSecretsExpressionString(string secretName) => "secrets." + secretName;
}
