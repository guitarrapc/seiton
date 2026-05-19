using Seiton.Core.Linting.Fixing;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;
using static Seiton.Core.Linting.Rules.RunContextDirectUseAnalyzer;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags direct use of <c>inputs.*</c> context in <c>run:</c> scripts where environment variables should be used instead.</summary>
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

        var absoluteOffset = Arena.GetStringSlice(runNode).Offset + expressionBodyStart - 3;

        // Skip fix when expression is inside a no-expand heredoc body
        if (IsInsideNoExpandHereDoc(Config.Utf8Yaml, absoluteOffset))
        {
            return false;
        }

        // Skip fix when expression is inside shell single quotes
        if (IsInsideShellSingleQuotes(Config.Utf8Yaml, absoluteOffset))
        {
            return false;
        }

        // Case 1: existing unique env mapping resolves the variable name
        if (TryResolveShellVariableName(Arena, step.Env, _currentJob?.Env, _currentWorkflow?.Env,
            Config.Utf8Yaml, inputName, TryParseSimpleInputsReference, out var variableName))
        {
            var replacement = RunContextDirectUseAnalyzer.IsPowerShell(Arena, step, Config.Utf8Yaml)
                ? "$env:" + variableName
                : "${" + variableName + "}";

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

        var expressionString = BuildInputsExpressionString(inputName, expression);
        var envVarName = DeduplicateEnvName(InputNameToEnvVarName(inputName), step);
        if (envVarName is null)
        {
            return false;
        }

        var shellReplacement = RunContextDirectUseAnalyzer.IsPowerShell(Arena, step, Config.Utf8Yaml)
            ? "$env:" + envVarName
            : "${" + envVarName + "}";

        if (!TryBuildEnvInsertionEdit(step, envVarName, expressionString, out var insertEdit))
        {
            return false;
        }

        fix = new DiagnosticFix(
            $"map inputs reference to env variable {envVarName}",
            [insertEdit, new TextEdit(absoluteOffset, expressionLength, shellReplacement)]);
        return true;
    }

    /// <summary>Builds the expression string for the env value (e.g. "inputs.target" or "github.event.inputs.target").</summary>
    private static string BuildInputsExpressionString(string inputName, ReadOnlySpan<byte> expression)
    {
        // Check if the expression uses github.event.inputs form
        var index = 0;
        if (TryConsumeGithubEventInputsRoot(expression, ref index))
        {
            return "github.event.inputs." + inputName;
        }

        return "inputs." + inputName;
    }

    /// <summary>Converts an input name (e.g. "benchmark-config-path") to an env var name (e.g. "BENCHMARK_CONFIG_PATH").</summary>
    internal static string InputNameToEnvVarName(string inputName)
    {
        var sb = new System.Text.StringBuilder(inputName.Length);
        for (var i = 0; i < inputName.Length; i++)
        {
            var c = inputName[i];
            if (c is '-' or '.')
            {
                sb.Append('_');
            }
            else if (c is >= 'a' and <= 'z')
            {
                sb.Append((char)(c - 32));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private string? DeduplicateEnvName(string baseName, Step step)
    {
        var existing = CollectExistingEnvNames(step);
        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; i <= 99; i++)
        {
            var candidate = baseName + "_" + i;
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private HashSet<string> CollectExistingEnvNames(Step step)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectEnvNames(step.Env, names);
        CollectEnvNames(_currentJob?.Env, names);
        CollectEnvNames(_currentWorkflow?.Env, names);
        return names;
    }

    private void CollectEnvNames(Env? env, HashSet<string> names)
    {
        if (env?.Vars is null || Config.Utf8Yaml is null)
        {
            return;
        }

        foreach (var pair in env.Vars.Value)
        {
            var nameBytes = Arena.GetStringValue(pair.Value.Name);
            var nameIndex = 0;
            if (TryReadIdentifier(nameBytes, ref nameIndex, out var name) && nameIndex == nameBytes.Length)
            {
                names.Add(name);
            }
        }
    }

    private bool TryBuildEnvInsertionEdit(Step step, string envVarName, string expressionString, out TextEdit edit)
    {
        edit = default;
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        var utf8Yaml = Config.Utf8Yaml;
        var lineEnding = FixFormatting.DetectDominantLineEnding(utf8Yaml);

        var runKeyOffset = FindRunKeyOffset(utf8Yaml, step.Exec.Range.Start);
        if (runKeyOffset < 0)
        {
            return false;
        }

        var runLine = FindLineNumberFromOffset(utf8Yaml, runKeyOffset);
        if (runLine < 1)
        {
            return false;
        }

        var stepKeyIndent = GetStepKeyIndentation(utf8Yaml, runLine);

        if (step.Env?.Vars is not null && step.Env.Vars.Value.Count > 0)
        {
            if (IsFlowStyleEnv(utf8Yaml, step.Env))
            {
                return false;
            }

            var lastEnvLine = FindLastEnvEntryLine(step.Env);
            if (lastEnvLine < 1)
            {
                return false;
            }

            var envKeyLine = FindEnvKeyLine(step.Env);
            var childIndent = envKeyLine >= 0
                ? FixFormatting.GetLineIndentation(utf8Yaml, envKeyLine)
                : FixFormatting.GetLineIndentation(utf8Yaml, lastEnvLine);
            var insertOffset = FindLineEndOffsetIncludingNewLine(utf8Yaml, lastEnvLine);
            var needsLeadingNewline = insertOffset == utf8Yaml.Length && utf8Yaml.Length > 0 && utf8Yaml[^1] != (byte)'\n';
            var insertText = (needsLeadingNewline ? lineEnding : "")
                + childIndent + envVarName + ": ${{ " + expressionString + " }}" + lineEnding;
            edit = new TextEdit(insertOffset, 0, insertText);
            return true;
        }

        if (step.Env is not null)
        {
            return false;
        }

        var childIndentUnit = FixFormatting.InferIndentationUnit(utf8Yaml);
        var envChildIndent = stepKeyIndent + childIndentUnit;
        var runEndLine = FindRunEndLine(utf8Yaml, runLine, stepKeyIndent);
        var insertAfterRun = FindLineEndOffsetIncludingNewLine(utf8Yaml, runEndLine);
        var needsLeadingNewlineForEnvBlock = insertAfterRun == utf8Yaml.Length && utf8Yaml.Length > 0 && utf8Yaml[^1] != (byte)'\n';
        var envBlock = (needsLeadingNewlineForEnvBlock ? lineEnding : "")
            + stepKeyIndent + "env:" + lineEnding
            + envChildIndent + envVarName + ": ${{ " + expressionString + " }}" + lineEnding;
        edit = new TextEdit(insertAfterRun, 0, envBlock);
        return true;
    }

    private int FindLastEnvEntryLine(Env env)
    {
        if (env.Vars is null || Config.Utf8Yaml is null)
        {
            return -1;
        }

        var maxEndOffset = 0;
        foreach (var pair in env.Vars.Value)
        {
            var valueRange = Arena.GetStringRange(pair.Value.Value);
            var endOffset = valueRange.Start + valueRange.Length;
            if (endOffset > maxEndOffset)
            {
                maxEndOffset = endOffset;
            }
        }

        if (maxEndOffset <= 0)
        {
            return -1;
        }

        return FindLineNumberFromOffset(Config.Utf8Yaml, maxEndOffset - 1);
    }

    private int FindEnvKeyLine(Env env)
    {
        if (env.Vars is null || Config.Utf8Yaml is null)
        {
            return -1;
        }

        foreach (var pair in env.Vars.Value)
        {
            var nameRange = Arena.GetStringRange(pair.Value.Name);
            if (nameRange.Start >= 0)
            {
                return FindLineNumberFromOffset(Config.Utf8Yaml, nameRange.Start);
            }
        }

        return -1;
    }

    private static bool IsFlowStyleEnv(byte[] utf8Yaml, Env env)
    {
        if (env.Range.Start < 0 || env.Range.Start >= utf8Yaml.Length)
        {
            return false;
        }

        var pos = env.Range.Start;
        while (pos < utf8Yaml.Length && utf8Yaml[pos] != (byte)'\n')
        {
            if (utf8Yaml[pos] == (byte)'{')
            {
                return true;
            }

            pos++;
        }

        return false;
    }

    private static int FindRunKeyOffset(byte[] utf8Yaml, int valueStart)
    {
        var pos = Math.Min(valueStart, utf8Yaml.Length);
        while (pos > 0)
        {
            var lineStart = pos - 1;
            while (lineStart > 0 && utf8Yaml[lineStart - 1] != (byte)'\n')
            {
                lineStart--;
            }

            var i = lineStart;
            while (i < pos && utf8Yaml[i] == (byte)' ')
            {
                i++;
            }

            if (i + 1 < pos && utf8Yaml[i] == (byte)'-' && utf8Yaml[i + 1] == (byte)' ')
            {
                i += 2;
            }

            if (i + 3 < utf8Yaml.Length
                && i < valueStart
                && utf8Yaml[i] == (byte)'r'
                && utf8Yaml[i + 1] == (byte)'u'
                && utf8Yaml[i + 2] == (byte)'n'
                && utf8Yaml[i + 3] == (byte)':')
            {
                return i;
            }

            pos = lineStart;
        }

        return -1;
    }

    private static int FindLineNumberFromOffset(byte[] utf8Yaml, int offset)
    {
        if (offset <= 0)
        {
            return 1;
        }

        if (offset > utf8Yaml.Length)
        {
            offset = utf8Yaml.Length;
        }

        var line = 1;
        for (var i = 0; i < offset; i++)
        {
            if (utf8Yaml[i] == (byte)'\n')
            {
                line++;
            }
        }

        return line;
    }

    private static int FindLineStartOffset(byte[] utf8Yaml, int lineNumber)
    {
        if (lineNumber <= 1)
        {
            return 0;
        }

        var currentLine = 1;
        for (var i = 0; i < utf8Yaml.Length; i++)
        {
            if (utf8Yaml[i] != (byte)'\n')
            {
                continue;
            }

            currentLine++;
            if (currentLine == lineNumber)
            {
                return i + 1;
            }
        }

        return utf8Yaml.Length;
    }

    private static int FindLineEndOffsetIncludingNewLine(byte[] utf8Yaml, int lineNumber)
    {
        var start = FindLineStartOffset(utf8Yaml, lineNumber);
        for (var i = start; i < utf8Yaml.Length; i++)
        {
            if (utf8Yaml[i] == (byte)'\n')
            {
                return i + 1;
            }
        }

        return utf8Yaml.Length;
    }

    private static string GetStepKeyIndentation(byte[] utf8Yaml, int lineNumber)
    {
        var baseIndent = FixFormatting.GetLineIndentation(utf8Yaml, lineNumber);
        var lineStart = FindLineStartOffset(utf8Yaml, lineNumber);
        var offset = lineStart + baseIndent.Length;
        return offset + 1 < utf8Yaml.Length && utf8Yaml[offset] == (byte)'-' && utf8Yaml[offset + 1] == (byte)' '
            ? baseIndent + "  "
            : baseIndent;
    }

    private static int FindRunEndLine(byte[] utf8Yaml, int runKeyLine, string stepKeyIndent)
    {
        var lastContentLine = runKeyLine;
        var stepKeyIndentLen = stepKeyIndent.Length;
        var currentLine = runKeyLine;
        var pos = FindLineStartOffset(utf8Yaml, runKeyLine);

        while (pos < utf8Yaml.Length && utf8Yaml[pos] != (byte)'\n')
        {
            pos++;
        }

        if (pos < utf8Yaml.Length)
        {
            pos++;
        }

        currentLine++;

        while (pos < utf8Yaml.Length)
        {
            var lineStart = pos;
            while (pos < utf8Yaml.Length && utf8Yaml[pos] != (byte)'\n')
            {
                pos++;
            }

            var lineLen = pos - lineStart;
            if (pos < utf8Yaml.Length)
            {
                pos++;
            }

            var indent = 0;
            while (indent < lineLen && utf8Yaml[lineStart + indent] == (byte)' ')
            {
                indent++;
            }

            if (indent >= lineLen || (lineLen > 0 && lineStart + indent < utf8Yaml.Length && utf8Yaml[lineStart + indent] == (byte)'\r' && indent + 1 >= lineLen))
            {
                lastContentLine = currentLine;
                currentLine++;
                continue;
            }

            if (indent > stepKeyIndentLen)
            {
                lastContentLine = currentLine;
                currentLine++;
                continue;
            }

            break;
        }

        return lastContentLine;
    }

    // Inputs-specific reference parsing

    private static bool TryParseSimpleInputsReference(ReadOnlySpan<byte> expression, out string inputName)
    {
        inputName = string.Empty;

        var index = 0;
        if (TryConsumeSimpleInputsRoot(expression, ref index))
        {
            return TryConsumeInputMemberOrBracketName(expression, ref index, out inputName);
        }

        index = 0;
        if (!TryConsumeGithubEventInputsRoot(expression, ref index))
        {
            return false;
        }

        return TryConsumeInputMemberOrBracketName(expression, ref index, out inputName);
    }

    /// <summary>
    /// Like <see cref="RunContextDirectUseAnalyzer.TryConsumeMemberOrBracketName"/> but allows hyphens
    /// in dot-access identifiers, matching the GitHub Actions expression parser behavior for input names.
    /// </summary>
    private static bool TryConsumeInputMemberOrBracketName(ReadOnlySpan<byte> expression, ref int index, out string name)
    {
        name = string.Empty;
        if (index >= expression.Length)
        {
            return false;
        }

        if (expression[index] == (byte)'.')
        {
            index++;
            SkipWhiteSpace(expression, ref index);
            if (!TryReadGitHubIdentifier(expression, ref index, out name))
            {
                return false;
            }

            SkipWhiteSpace(expression, ref index);
            return index == expression.Length;
        }

        // Bracket access: delegate to shared helper (quotes handle any chars)
        return TryConsumeMemberOrBracketName(expression, ref index, out name);
    }

    /// <summary>
    /// Reads a GitHub Actions identifier that allows hyphens (e.g. "benchmark-config-path").
    /// Matches the expression parser's TryParseIdentifier behavior.
    /// </summary>
    private static bool TryReadGitHubIdentifier(ReadOnlySpan<byte> expression, ref int index, out string identifier)
    {
        identifier = string.Empty;
        if (index >= expression.Length)
        {
            return false;
        }

        var b = expression[index];
        if (!((b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z') || b == (byte)'_'))
        {
            return false;
        }

        var start = index;
        index++;
        while (index < expression.Length)
        {
            b = expression[index];
            if (!((b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z')
                || (b >= (byte)'0' && b <= (byte)'9') || b == (byte)'_' || b == (byte)'-'))
            {
                break;
            }

            index++;
        }

        identifier = System.Text.Encoding.UTF8.GetString(expression[start..index]);
        return true;
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
