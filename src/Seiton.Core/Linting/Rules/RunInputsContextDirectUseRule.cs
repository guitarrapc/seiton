using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;
using static Seiton.Core.Linting.Rules.RunContextDirectUseAnalyzer;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags direct use of <c>inputs.*</c> context in <c>run:</c> scripts where environment variables should be used instead.</summary>
public sealed class RunInputsContextDirectUseRule() : RuleBase(RuleId.RunInputsContextDirectUse)
{
    private WorkflowRef _currentWorkflow;
    private JobRef _currentJob;
    private bool _strict;

    public override string Name => "Run Inputs Context Direct Use Rule";

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        _currentWorkflow = workflow;
        _currentJob = default;
    }

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        _strict = config.GetRuleConfig(Id)?.Strict == true;
    }

    public override void VisitWorkflowPost(WorkflowRef workflow)
    {
        _currentWorkflow = default;
        _currentJob = default;
    }

    public override void VisitJobPre(JobRef job)
    {
        _currentJob = job;
    }

    public override void VisitJobPost(JobRef job)
    {
        _currentJob = default;
    }

    public override void VisitStep(StepRef step)
    {
        if (Config.Utf8Yaml is null || step.Exec.Kind != StepExecKind.Run)
        {
            return;
        }

        CheckRunNode(step, step.Exec.AsRun().Run);
    }

    private void CheckRunNode(StepRef step, StringRef runNode)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        var runText = runNode.Value;
        var searchStart = 0;
        while (TryFindExpression(runText, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;
            var location = BuildExpressionLocation(Config.Utf8Yaml, runNode, bodyStart, nextSearchStart, Config.GetLineStarts());

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

            // Skip detection inside no-expand heredoc (<<'EOF') where shell variables don't expand
            var absoluteOffset = runNode.Slice.Offset + bodyStart - 3;
            if (ShouldSuppressNoExpandDirectUseDiagnostic(Config.Utf8Yaml, absoluteOffset, _strict))
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
            else if (!TryParseSimpleInputsReferenceBounds(expression, out _, out _, out _))
            {
                // Composite expression — suggest env: block mapping
                AddStepError(
                    step,
                    "run script must not reference ${{ inputs.* }} or ${{ github.event.inputs.* }} directly; map inputs to env and use shell variables instead (e.g. ${NAME}, $NAME, or $env:NAME)",
                    location,
                    "consider moving the entire expression to an env: block and referencing the shell variable instead");
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

    private bool TryBuildFix(StepRef step, StringRef runNode, ReadOnlySpan<byte> expression, int expressionBodyStart, int expressionLength, out DiagnosticFix fix)
    {
        fix = default;
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        if (!TryParseSimpleInputsReference(expression, out var inputName, out var isGithubEventInputs))
        {
            return TryBuildCompoundExpressionFix(step, runNode, expression, expressionBodyStart, expressionLength, out fix);
        }

        var absoluteOffset = runNode.Slice.Offset + expressionBodyStart - 3;

        if (IsInsideNoExpandHereDoc(Config.Utf8Yaml, absoluteOffset))
        {
            return false;
        }

        if (IsInsideShellSingleQuotes(Config.Utf8Yaml, absoluteOffset))
        {
            return false;
        }

        // Case 1: existing unique env mapping resolves the variable name
        if (TryResolveShellVariableName(step.Env, _currentJob.Env, _currentWorkflow.Env,
            Config.Utf8Yaml, inputName, TryParseSimpleInputsReference, out var variableName))
        {
            var isPowerShell = RunContextDirectUseAnalyzer.IsPowerShellWithDefaults(step, _currentJob, _currentWorkflow, Config.Utf8Yaml);
            if (isPowerShell is null)
            {
                return false;
            }

            if (!TryBuildShellVariableReplacement(variableName, isPowerShell.Value, wrapInDoubleQuotes: false, out var replacement))
            {
                return false;
            }

            fix = new DiagnosticFix(
                "replace direct inputs context expansion with mapped shell variable",
                [new TextEdit(absoluteOffset, expressionLength, replacement)]);
            return true;
        }

        // Case 2: no existing mapping — generate env var name and insert env block
        if (!Config.Fix.Enabled)
        {
            return false;
        }

        var expressionString = BuildInputsExpressionString(inputName, isGithubEventInputs);
        var envVarName = DeduplicateEnvName(InputNameToEnvVarName(inputName),
            step.Env, _currentJob.Env, _currentWorkflow.Env);
        if (envVarName is null)
        {
            return false;
        }

        var isPowerShell2 = RunContextDirectUseAnalyzer.IsPowerShellWithDefaults(step, _currentJob, _currentWorkflow, Config.Utf8Yaml);
        if (isPowerShell2 is null)
        {
            return false;
        }

        if (!TryBuildShellVariableReplacement(envVarName, isPowerShell2.Value, wrapInDoubleQuotes: false, out var shellReplacement))
        {
            return false;
        }

        if (!TryBuildStepEnvInsertionEdit(Config.Utf8Yaml, step, envVarName, expressionString, out var insertEdit))
        {
            return false;
        }

        fix = new DiagnosticFix(
            $"map inputs reference to env variable {envVarName}",
            [insertEdit, new TextEdit(absoluteOffset, expressionLength, shellReplacement)]);
        return true;
    }

    private bool TryBuildCompoundExpressionFix(StepRef step, StringRef runNode, ReadOnlySpan<byte> expression, int expressionBodyStart, int expressionLength, out DiagnosticFix fix)
    {
        fix = default;
        if (Config.Utf8Yaml is null || !Config.Fix.Enabled)
        {
            return false;
        }

        var absoluteOffset = runNode.Slice.Offset + expressionBodyStart - 3;

        if (IsInsideNoExpandHereDoc(Config.Utf8Yaml, absoluteOffset)
            || IsInsideShellSingleQuotes(Config.Utf8Yaml, absoluteOffset))
        {
            return false;
        }

        var expressionString = Encoding.UTF8.GetString(expression);
        var envBaseName = TryExtractFirstInputsName(expression, out var firstInputName)
            ? InputNameToEnvVarName(firstInputName)
            : "INPUT_VALUE";
        var envVarName = DeduplicateEnvName(envBaseName,
            step.Env, _currentJob.Env, _currentWorkflow.Env);
        if (envVarName is null)
        {
            return false;
        }

        var isPowerShell = RunContextDirectUseAnalyzer.IsPowerShellWithDefaults(step, _currentJob, _currentWorkflow, Config.Utf8Yaml);
        if (isPowerShell is null)
        {
            return false;
        }

        if (!TryBuildShellVariableReplacement(envVarName, isPowerShell.Value, wrapInDoubleQuotes: false, out var shellReplacement))
        {
            return false;
        }

        if (!TryBuildStepEnvInsertionEdit(Config.Utf8Yaml, step, envVarName, expressionString, out var insertEdit))
        {
            return false;
        }

        fix = new DiagnosticFix(
            $"map compound inputs expression to env variable {envVarName}",
            [insertEdit, new TextEdit(absoluteOffset, expressionLength, shellReplacement)]);
        return true;
    }

    private bool TryExtractFirstInputsName(ReadOnlySpan<byte> expression, out string inputName)
    {
        inputName = string.Empty;
        var parseResult = Config.ParseExpression(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            return false;
        }

        return TryExtractFirstInputsNameFromAst(
            parseResult.RootNode,
            parentId: -1,
            parseResult.Nodes,
            parseResult.Arguments,
            expression,
            out inputName);
    }

    private static bool TryExtractFirstInputsNameFromAst(
        int nodeId,
        int parentId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        out string inputName)
    {
        inputName = string.Empty;
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind is ExpressionNodeKind.MemberAccess or ExpressionNodeKind.IndexAccess or ExpressionNodeKind.WildcardAccess)
        {
            if (IsSimpleInputsMember(node, nodes, expression, out inputName)
                || IsGithubEventInputsMember(node, nodes, expression, out inputName))
            {
                return true;
            }
        }

        switch (node.Kind)
        {
            case ExpressionNodeKind.Unary:
                return TryExtractFirstInputsNameFromAst(node.Left, nodeId, nodes, arguments, expression, out inputName);
            case ExpressionNodeKind.Binary:
                return TryExtractFirstInputsNameFromAst(node.Left, nodeId, nodes, arguments, expression, out inputName)
                    || TryExtractFirstInputsNameFromAst(node.Right, nodeId, nodes, arguments, expression, out inputName);
            case ExpressionNodeKind.MemberAccess:
            case ExpressionNodeKind.WildcardAccess:
            case ExpressionNodeKind.IndexAccess:
                return TryExtractFirstInputsNameFromAst(node.Left, nodeId, nodes, arguments, expression, out inputName);
            case ExpressionNodeKind.FunctionCall:
                if (TryExtractFirstInputsNameFromAst(node.Left, nodeId, nodes, arguments, expression, out inputName))
                {
                    return true;
                }

                for (var i = 0; i < node.ArgCount; i++)
                {
                    var argIndex = node.ArgStart + i;
                    if (argIndex < 0 || argIndex >= arguments.Length)
                    {
                        continue;
                    }

                    if (TryExtractFirstInputsNameFromAst(arguments[argIndex], nodeId, nodes, arguments, expression, out inputName))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private static bool IsSimpleInputsMember(
        ExpressionNode node,
        ExpressionNode[] nodes,
        ReadOnlySpan<byte> expression,
        out string inputName)
    {
        inputName = string.Empty;
        if (node.Kind != ExpressionNodeKind.MemberAccess)
        {
            return false;
        }

        if (!IsIdentifierNode(node.Left, nodes, expression, "inputs"u8))
        {
            return false;
        }

        return TryGetMemberName(node, expression, out inputName);
    }

    private static bool IsGithubEventInputsMember(
        ExpressionNode node,
        ExpressionNode[] nodes,
        ReadOnlySpan<byte> expression,
        out string inputName)
    {
        inputName = string.Empty;
        if (node.Kind != ExpressionNodeKind.MemberAccess)
        {
            return false;
        }

        if (!IsGithubEventInputsChain(node.Left, nodes, expression))
        {
            return false;
        }

        return TryGetMemberName(node, expression, out inputName);
    }

    private static bool TryGetMemberName(ExpressionNode node, ReadOnlySpan<byte> expression, out string inputName)
    {
        inputName = string.Empty;
        if (node.Kind != ExpressionNodeKind.MemberAccess)
        {
            return false;
        }

        var nameBytes = node.Token.AsSpan(expression);
        if (nameBytes.Length == 0)
        {
            return false;
        }

        inputName = Encoding.UTF8.GetString(nameBytes);
        return inputName.Length > 0;
    }

    /// <summary>Builds the expression string for the env value (e.g. "inputs.target" or "github.event.inputs.target").</summary>
    private static string BuildInputsExpressionString(string inputName, bool isGithubEventInputs)
    {
        if (isGithubEventInputs)
        {
            return "github.event.inputs." + inputName;
        }

        return "inputs." + inputName;
    }

    /// <summary>Converts an input name (e.g. "benchmark-config-path") to an env var name (e.g. "BENCHMARK_CONFIG_PATH").</summary>
    internal static string InputNameToEnvVarName(string inputName)
    {
        return string.Create(inputName.Length, inputName, static (span, name) =>
        {
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                span[i] = c switch
                {
                    '-' or '.' => '_',
                    >= 'a' and <= 'z' => (char)(c - 32),
                    _ => c,
                };
            }
        });
    }

    // Inputs-specific reference parsing

    private static bool TryParseSimpleInputsReference(ReadOnlySpan<byte> expression, out string inputName)
    {
        return TryParseSimpleInputsReference(expression, out inputName, out _);
    }

    private static bool TryParseSimpleInputsReference(ReadOnlySpan<byte> expression, out string inputName, out bool isGithubEventInputs)
    {
        inputName = string.Empty;
        if (!TryParseSimpleInputsReferenceBounds(expression, out isGithubEventInputs, out var nameStart, out var nameLength))
        {
            return false;
        }

        inputName = DecodeExpressionName(expression, nameStart, nameLength);
        return inputName.Length > 0;
    }

}
