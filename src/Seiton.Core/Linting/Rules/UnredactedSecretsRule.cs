using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;
using static Seiton.Core.Linting.RuleConfigHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class UnredactedSecretsRule : RuleBase
{
    Workflow? currentWorkflow;
    Job? currentJob;
    HashSet<string> additionalOutputCommands = [];

    public override string Id => "unredacted-secrets";

    public override string Name => "Unredacted Secrets Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalOutputCommands = config.GetRuleConfig(Id)?.Specific is UnredactedSecretsSpecificConfig specific
            ? BuildNormalizedSet(specific.OutputCommands)
            : [];
    }

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        currentWorkflow = workflow;
        currentJob = null;
    }

    public override void VisitWorkflowPost(Workflow workflow)
    {
        currentWorkflow = null;
        currentJob = null;
    }

    public override void VisitJobPre(Job job)
    {
        currentJob = job;
    }

    public override void VisitJobPost(Job job)
    {
        currentJob = null;
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecRun run)
        {
            return;
        }

        var secretVars = CollectSecretDerivedEnvVarNames(step);
        if (secretVars is null || secretVars.Count == 0)
        {
            return;
        }

        var runText = run.Run.Value.AsSpan(Config.Utf8Yaml);
        foreach (var name in secretVars)
        {
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
            return;
        }
    }

    TextRange BuildRunTextLocation(StringNode runNode, int relativeOffset, int tokenLength)
    {
        var absoluteStart = runNode.Value.Offset + relativeOffset;
        var absoluteLength = tokenLength;
        if (Config.Utf8Yaml is null || absoluteStart < 0 || absoluteLength <= 0)
        {
            return runNode.Range;
        }

        var lineStarts = BuildLineStarts(Config.Utf8Yaml);
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

    HashSet<string>? CollectSecretDerivedEnvVarNames(Step step)
    {
        if (Config.Utf8Yaml is null)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        AddSecretMappedVars(step.Env, names);
        AddSecretMappedVars(currentJob?.Env, names);
        AddSecretMappedVars(currentWorkflow?.Env, names);
        return names;
    }

    void AddSecretMappedVars(Env? env, HashSet<string> names)
    {
        if (env?.Vars is null || env.Vars.Count == 0 || Config.Utf8Yaml is null)
        {
            return;
        }

        foreach (var pair in env.Vars)
        {
            var envVar = pair.Value;
            if (!ContainsSecretsReference(envVar.Value))
            {
                continue;
            }

            var name = Decode(envVar.Name.Value);
            if (IsSimpleIdentifier(name))
            {
                names.Add(name);
            }
        }
    }

    bool ContainsSecretsReference(StringNode node)
    {
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        if (ContainsSecretsReferenceInValue(node.Value.AsSpan(Config.Utf8Yaml)))
        {
            return true;
        }

        if (node.Expression is null)
        {
            return false;
        }

        var expression = TrimAsciiWhiteSpace(node.Expression.Value.AsSpan(Config.Utf8Yaml));
        return ContainsSecretsReferenceInExpression(expression);
    }

    static bool TryFindOutputOfVariableLocation(
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

    static bool ContainsOutputCommand(ReadOnlySpan<byte> line, HashSet<string> additionalOutputCommands)
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

    static bool TryFindPosixVariableReference(
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

    static bool TryFindPowerShellVariableReference(
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
    static bool ContainsSecretsReferenceInValue(ReadOnlySpan<byte> value)
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

    static bool ContainsSecretsReferenceInExpression(ReadOnlySpan<byte> expression)
    {
        if (expression.Length == 0)
        {
            return false;
        }

        var parseResult = ExpressionParser.Parse(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            return false;
        }

        return ContainsSecretsReference(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);
    }

    static bool ContainsSecretsReference(int nodeId, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression)
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

    static bool ContainsSecretsReferenceInFunctionCall(ExpressionNode functionCallNode, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression)
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
