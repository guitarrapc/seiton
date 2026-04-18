using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class UnredactedSecretsRule : RuleBase
{
    Workflow? currentWorkflow;
    Job? currentJob;
    HashSet<string>? additionalOutputCommands;

    public override string Id => "unredacted-secrets";

    public override string Name => "Unredacted Secrets Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalOutputCommands = BuildNormalizedSet(config.GetRuleConfig(Id)?.OutputCommands?.Extend);
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
            if (!ContainsOutputOfVariable(runText, name.AsSpan(), additionalOutputCommands))
            {
                continue;
            }

            AddStepWarning(
                step,
                $"run script may print secret-derived variable '{name}' without masking; avoid echo/printf/Write-Host of secret values",
                run.Run.Range);
            return;
        }
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

    static bool ContainsOutputOfVariable(ReadOnlySpan<byte> runText, ReadOnlySpan<char> variableName, HashSet<string>? additionalOutputCommands)
    {
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
            if (ContainsOutputCommand(line, additionalOutputCommands)
                && (ContainsPosixVariableReference(line, variableName)
                    || ContainsPowerShellVariableReference(line, variableName)))
            {
                return true;
            }

            lineStart = lineEnd + 1;
        }

        return false;
    }

    static bool ContainsOutputCommand(ReadOnlySpan<byte> line, HashSet<string>? additionalOutputCommands)
    {
        if (ContainsAsciiIgnoreCase(line, "echo"u8)
            || ContainsAsciiIgnoreCase(line, "printf"u8)
            || ContainsAsciiIgnoreCase(line, "write-host"u8)
            || ContainsAsciiIgnoreCase(line, "write-output"u8))
        {
            return true;
        }

        if (additionalOutputCommands is null || additionalOutputCommands.Count == 0)
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

    static bool ContainsPosixVariableReference(ReadOnlySpan<byte> line, ReadOnlySpan<char> variableName)
    {
        if (ContainsAscii(line, '$', '{', variableName, '}'))
        {
            return true;
        }

        return ContainsAscii(line, '$', variableName);
    }

    static bool ContainsPowerShellVariableReference(ReadOnlySpan<byte> line, ReadOnlySpan<char> variableName)
    {
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
                return true;
            }

            start = valueStart;
        }
    }

    static bool ContainsAscii(ReadOnlySpan<byte> line, char sigil, ReadOnlySpan<char> variableName)
    {
        var text = System.Text.Encoding.UTF8.GetString(line);
        var token = string.Concat(sigil, variableName.ToString());
        return text.Contains(token, StringComparison.Ordinal);
    }

    static bool ContainsAscii(ReadOnlySpan<byte> line, char sigil, char open, ReadOnlySpan<char> variableName, char close)
    {
        var text = System.Text.Encoding.UTF8.GetString(line);
        var token = string.Concat(sigil, open, variableName.ToString(), close);
        return text.Contains(token, StringComparison.Ordinal);
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

    static bool TryFindExpression(ReadOnlySpan<byte> value, int searchStart, out int bodyStart, out int bodyLength, out int nextSearchStart)
    {
        bodyStart = 0;
        bodyLength = 0;
        nextSearchStart = 0;

        if ((uint)searchStart >= (uint)value.Length)
        {
            return false;
        }

        var start = value[searchStart..].IndexOf("${{"u8);
        if (start < 0)
        {
            return false;
        }

        bodyStart = searchStart + start + 3;
        var close = value[bodyStart..].IndexOf("}}"u8);
        if (close < 0)
        {
            return false;
        }

        bodyLength = close;
        nextSearchStart = bodyStart + close + 2;
        return true;
    }

    static ReadOnlySpan<byte> TrimAsciiWhiteSpace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && IsAsciiWhiteSpace(value[start]))
        {
            start++;
        }

        var end = value.Length - 1;
        while (end >= start && IsAsciiWhiteSpace(value[end]))
        {
            end--;
        }

        return end >= start ? value.Slice(start, end - start + 1) : [];
    }

    static bool IsAsciiWhiteSpace(byte ch)
    {
        return ch == (byte)' ' || ch == (byte)'\t' || ch == (byte)'\n' || ch == (byte)'\r';
    }

    static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i];
            var r = right[i];
            if (l is >= (byte)'A' and <= (byte)'Z')
            {
                l = (byte)(l + 32);
            }

            if (r is >= (byte)'A' and <= (byte)'Z')
            {
                r = (byte)(r + 32);
            }

            if (l != r)
            {
                return false;
            }
        }

        return true;
    }

    static bool IsSimpleIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var first = value[0];
        if (!((first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z') || first == '_'))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (!((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_'))
            {
                return false;
            }
        }

        return true;
    }

    static HashSet<string>? BuildNormalizedSet(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
    }
}
