using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;
using static Seiton.Core.Linting.RuleConfigHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags workflow patterns that may leak secrets through unredacted output commands.</summary>
public sealed class UnredactedSecretsRule() : RuleBase(RuleId.UnredactedSecrets)
{
    private Workflow? currentWorkflow;
    private Job? currentJob;
    private HashSet<string> additionalOutputCommands = [];
    private readonly List<string> _workflowVarNames = [];
    private readonly List<string> _jobVarNames = [];
    private readonly List<string> _stepVarNames = [];

    public override string Name => "Unredacted Secrets Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalOutputCommands = config.GetRuleConfig(Id)?.OutputCommands is { Count: > 0 } commands
            ? BuildNormalizedSet(commands)
            : [];
    }

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        currentWorkflow = workflow;
        currentJob = null;
        _workflowVarNames.Clear();
        AddSecretMappedVars(workflow.Env, _workflowVarNames);
    }

    public override void VisitWorkflowPost(Workflow workflow)
    {
        currentWorkflow = null;
        currentJob = null;
        _workflowVarNames.Clear();
    }

    public override void VisitJobPre(Job job)
    {
        currentJob = job;
        _jobVarNames.Clear();
        AddSecretMappedVars(job.Env, _jobVarNames);
    }

    public override void VisitJobPost(Job job)
    {
        currentJob = null;
        _jobVarNames.Clear();
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecRun run)
        {
            return;
        }

        _stepVarNames.Clear();
        AddSecretMappedVars(step.Env, _stepVarNames);

        if (_workflowVarNames.Count == 0 && _jobVarNames.Count == 0 && _stepVarNames.Count == 0)
        {
            return;
        }

        var runText = Arena.GetStringValue(run.Run);
        if (FindAndReportSecretVar(runText, _stepVarNames, run, step)) return;
        if (FindAndReportSecretVar(runText, _jobVarNames, run, step)) return;
        FindAndReportSecretVar(runText, _workflowVarNames, run, step);
    }

    private bool FindAndReportSecretVar(ReadOnlySpan<byte> runText, List<string> varNames, ExecRun run, Step step)
    {
        for (var i = 0; i < varNames.Count; i++)
        {
            var name = varNames[i];
            if (!TryFindOutputOfVariableLocation(
                runText,
                name.AsSpan(),
                additionalOutputCommands,
                out var relativeOffset,
                out var tokenLength))
            {
                continue;
            }

            var location = BuildRunTextLocation(run.Run, relativeOffset, tokenLength);

            AddStepWarning(
                step,
                $"run script may print secret-derived variable '{name}' without masking; avoid echo/printf/Write-Host of secret values",
                location);
            return true;
        }

        return false;
    }

    private TextRange BuildRunTextLocation(StringNodeId runNode, int relativeOffset, int tokenLength)
    {
        var absoluteStart = Arena.GetStringSlice(runNode).Offset + relativeOffset;
        var absoluteLength = tokenLength;
        if (Config.Utf8Yaml is null || absoluteStart < 0 || absoluteLength <= 0)
        {
            return Arena.GetStringRange(runNode);
        }

        var lineStarts = Config.GetLineStarts();
        var start = OffsetToLineColumn(lineStarts, absoluteStart);
        var end = OffsetToLineColumn(lineStarts, absoluteStart + absoluteLength - 1);
        return new TextRange(
            Start: absoluteStart,
            Length: absoluteLength,
            StartLine: start.Line,
            StartColumn: start.Column,
            EndLine: end.Line,
            EndColumn: end.Column);
    }

    private void AddSecretMappedVars(Env? env, List<string> names)
    {
        if (env?.Vars is null || env.Vars.Value.Count == 0 || Config.Utf8Yaml is null)
        {
            return;
        }

        foreach (var pair in env.Vars.Value)
        {
            var envVar = pair.Value;
            if (!ContainsSecretsReference(envVar.Value))
            {
                continue;
            }

            var name = Decode(Arena.GetStringSlice(envVar.Name));
            if (IsSimpleIdentifier(name))
            {
                names.Add(name);
            }
        }
    }

    private bool ContainsSecretsReference(StringNodeId node)
    {
        if (Config.Utf8Yaml is null)
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

    private static bool TryFindOutputOfVariableLocation(
        ReadOnlySpan<byte> runText,
        ReadOnlySpan<char> variableName,
        HashSet<string> additionalOutputCommands,
        out int relativeOffset,
        out int tokenLength)
    {
        relativeOffset = 0;
        tokenLength = 0;

        if (runText.Length == 0 || variableName.Length == 0)
        {
            return false;
        }

        var lineStart = 0;
        while (lineStart < runText.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < runText.Length && runText[lineEnd] != (byte)'\n')
            {
                lineEnd++;
            }

            var line = runText[lineStart..lineEnd];
            if (!ContainsOutputCommand(line, additionalOutputCommands))
            {
                lineStart = lineEnd + 1;
                continue;
            }

            if (TryFindPosixVariableReference(line, variableName, out var localOffset, out var localTokenLength)
                || TryFindPowerShellVariableReference(line, variableName, out localOffset, out localTokenLength))
            {
                relativeOffset = lineStart + localOffset;
                tokenLength = localTokenLength;
                return true;
            }

            lineStart = lineEnd + 1;
        }

        return false;
    }

    private static bool ContainsOutputCommand(ReadOnlySpan<byte> line, HashSet<string> additionalOutputCommands)
    {
        if (ContainsAsciiIgnoreCase(line, "echo"u8)
            || ContainsAsciiIgnoreCase(line, "printf"u8)
            || ContainsAsciiIgnoreCase(line, "write-host"u8)
            || ContainsAsciiIgnoreCase(line, "write-output"u8))
        {
            return true;
        }

        if (additionalOutputCommands.Count == 0)
        {
            return false;
        }

        foreach (var cmd in additionalOutputCommands)
        {
            var cmdBytes = System.Text.Encoding.UTF8.GetBytes(cmd);
            if (ContainsAsciiIgnoreCase(line, cmdBytes))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindPosixVariableReference(
        ReadOnlySpan<byte> line,
        ReadOnlySpan<char> variableName,
        out int localOffset,
        out int tokenLength)
    {
        localOffset = 0;
        tokenLength = 0;

        var text = System.Text.Encoding.UTF8.GetString(line);
        var bracketToken = "${" + variableName.ToString() + "}";
        var simpleToken = "$" + variableName.ToString();

        var bracketIndex = text.IndexOf(bracketToken, StringComparison.Ordinal);
        var simpleIndex = text.IndexOf(simpleToken, StringComparison.Ordinal);

        if (bracketIndex < 0 && simpleIndex < 0)
        {
            return false;
        }

        if (bracketIndex >= 0 && (simpleIndex < 0 || bracketIndex <= simpleIndex))
        {
            localOffset = bracketIndex;
            tokenLength = bracketToken.Length;
            return true;
        }

        localOffset = simpleIndex;
        tokenLength = simpleToken.Length;
        return true;
    }

    private static bool TryFindPowerShellVariableReference(
        ReadOnlySpan<byte> line,
        ReadOnlySpan<char> variableName,
        out int localOffset,
        out int tokenLength)
    {
        localOffset = 0;
        tokenLength = 0;

        if (line.Length == 0 || variableName.Length == 0)
        {
            return false;
        }

        Span<byte> prefix = stackalloc byte[5] { (byte)'$', (byte)'e', (byte)'n', (byte)'v', (byte)':' };
        if (!ContainsAsciiIgnoreCase(line, prefix))
        {
            return false;
        }

        var marker = "$env:";
        var text = System.Text.Encoding.UTF8.GetString(line);
        var start = 0;
        while (true)
        {
            var idx = text.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            var valueStart = idx + marker.Length;
            if (valueStart + variableName.Length <= text.Length
                && text.AsSpan(valueStart, variableName.Length).SequenceEqual(variableName))
            {
                localOffset = idx;
                tokenLength = marker.Length + variableName.Length;
                return true;
            }

            start = valueStart;
        }
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
