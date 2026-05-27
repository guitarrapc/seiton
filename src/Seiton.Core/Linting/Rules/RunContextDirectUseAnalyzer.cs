using Seiton.Core.Linting.Fixing;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using System.Runtime.CompilerServices;
using System.Text;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>
/// Shared scanning, location-building, and fix-generation utilities for
/// RunEnvContextDirectUseRule, RunInputsContextDirectUseRule, and RunSecretsContextDirectUseRule.
/// </summary>
internal static class RunContextDirectUseAnalyzer
{
    internal delegate bool SimpleReferenceParser(ReadOnlySpan<byte> expression, out string name);

    // Expression Location

    internal static TextRange BuildExpressionLocation(AstArena arena, byte[] utf8Yaml, StringNodeId runNode, int bodyStart, int nextSearchStart, int[] lineStarts)
    {
        var absoluteStart = arena.GetStringSlice(runNode).Offset + bodyStart - 3;
        var absoluteLength = nextSearchStart - (bodyStart - 3);
        if (absoluteStart < 0 || absoluteLength <= 0)
        {
            return arena.GetStringRange(runNode);
        }

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

    // Shell Detection

    /// <summary>
    /// Resolves effective shell with fallback: step.Shell → job.Defaults.Run.Shell → workflow.Defaults.Run.Shell.
    /// </summary>
    internal static bool IsPowerShellWithDefaults(AstArena arena, Step step, Job? currentJob, Workflow? currentWorkflow, byte[] utf8Yaml)
    {
        // Priority 1: step-level shell
        if (step.Exec is ExecRun run && run.Shell.HasValue)
        {
            if (arena.GetStringExpression(run.Shell).HasValue)
            {
                return false;
            }

            return IsPowerShell(arena, run.Shell, utf8Yaml);
        }

        // Priority 2: job defaults
        if (currentJob?.Defaults?.Run.Shell is { HasValue: true } jobShell && !arena.GetStringExpression(jobShell).HasValue)
        {
            return IsPowerShell(arena, jobShell, utf8Yaml);
        }

        // Priority 3: workflow defaults
        if (currentWorkflow?.Defaults?.Run.Shell is { HasValue: true } wfShell && !arena.GetStringExpression(wfShell).HasValue)
        {
            return IsPowerShell(arena, wfShell, utf8Yaml);
        }

        return false;
    }

    internal static bool IsPowerShell(AstArena arena, StringNodeId shellNode, byte[] utf8Yaml)
    {
        if (!shellNode.HasValue || arena.GetStringExpression(shellNode).HasValue)
        {
            return false;
        }

        var shell = arena.GetStringValue(shellNode);
        return shell.SequenceEqual("pwsh"u8)
            || shell.SequenceEqual("powershell"u8)
            || shell.SequenceEqual("Pwsh"u8)
            || shell.SequenceEqual("PowerShell"u8)
            || shell.SequenceEqual("PWSH"u8)
            || shell.SequenceEqual("POWERSHELL"u8)
            || EqualsOrdinalIgnoreCaseUtf8(shell, "pwsh"u8)
            || EqualsOrdinalIgnoreCaseUtf8(shell, "powershell"u8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EqualsOrdinalIgnoreCaseUtf8(ReadOnlySpan<byte> value, ReadOnlySpan<byte> lowerExpected)
    {
        if (value.Length != lowerExpected.Length) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var b = value[i];
            // ASCII lowercase: if uppercase A-Z, convert to lowercase
            if (b >= (byte)'A' && b <= (byte)'Z') b = (byte)(b + 32);
            if (b != lowerExpected[i]) return false;
        }
        return true;
    }

    // Env Value Expression Extraction

    internal static bool TryExtractExpressionBody(AstArena arena, StringNodeId node, byte[] utf8Yaml, out ReadOnlySpan<byte> expressionBody)
    {
        expressionBody = [];

        var value = TrimAsciiWhiteSpace(arena.GetStringValue(node));
        if (value.Length == 0)
        {
            return false;
        }

        if (TryExtractEmbeddedExpressionBody(value, out expressionBody))
        {
            return true;
        }

        if (!arena.GetStringExpression(node).HasValue)
        {
            return false;
        }

        var expression = TrimAsciiWhiteSpace(arena.GetStringValue(arena.GetStringExpression(node)));
        if (TryExtractEmbeddedExpressionBody(expression, out expressionBody))
        {
            return true;
        }

        expressionBody = expression;
        return expressionBody.Length > 0;
    }

    internal static bool TryExtractEmbeddedExpressionBody(ReadOnlySpan<byte> value, out ReadOnlySpan<byte> expressionBody)
    {
        expressionBody = [];
        if (!value.StartsWith("${{"u8) || !value.EndsWith("}}"u8))
        {
            return false;
        }

        expressionBody = TrimAsciiWhiteSpace(value.Slice(3, value.Length - 5));
        return expressionBody.Length > 0;
    }

    // Simple Context Reference Parsing

    internal static bool TryConsumeMemberOrBracketName(ReadOnlySpan<byte> expression, ref int index, out string name)
    {
        name = string.Empty;
        if (index >= expression.Length)
        {
            return false;
        }

        if (expression[index] == (byte)'.')
        {
            index++;
            if (!TryReadIdentifier(expression, ref index, out name))
            {
                return false;
            }

            SkipWhiteSpace(expression, ref index);
            return index == expression.Length;
        }

        if (expression[index] != (byte)'[')
        {
            return false;
        }

        index++;
        SkipWhiteSpace(expression, ref index);
        if (index >= expression.Length)
        {
            return false;
        }

        var quote = expression[index];
        if (quote is not ((byte)'\'' or (byte)'"'))
        {
            return false;
        }

        index++;
        var start = index;
        while (index < expression.Length && expression[index] != quote)
        {
            index++;
        }

        if (index >= expression.Length)
        {
            return false;
        }

        var nameBytes = expression[start..index];
        index++;
        SkipWhiteSpace(expression, ref index);
        if (index >= expression.Length || expression[index] != (byte)']')
        {
            return false;
        }

        index++;
        SkipWhiteSpace(expression, ref index);
        if (index != expression.Length)
        {
            return false;
        }

        var parsedName = Encoding.UTF8.GetString(nameBytes);
        if (!IsSimpleIdentifier(parsedName))
        {
            return false;
        }

        name = parsedName;
        return true;
    }

    internal static bool TryParseSimpleContextReference(ReadOnlySpan<byte> expression, ReadOnlySpan<byte> rootToken, out string name)
    {
        name = string.Empty;
        var index = 0;
        if (!ConsumeWordIgnoreCase(expression, ref index, rootToken))
        {
            return false;
        }

        SkipWhiteSpace(expression, ref index);
        return TryConsumeMemberOrBracketName(expression, ref index, out name);
    }

    // AST Root Reference Detection

    internal static bool ContainsContextRootReference(
        int nodeId,
        int parentId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        ReadOnlySpan<byte> rootToken)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind == ExpressionNodeKind.Identifier
            && IsContextRootIdentifier(nodeId, parentId, nodes)
            && EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), rootToken))
        {
            return true;
        }

        return node.Kind switch
        {
            ExpressionNodeKind.Unary => ContainsContextRootReference(node.Left, nodeId, nodes, arguments, expression, rootToken),
            ExpressionNodeKind.Binary => ContainsContextRootReference(node.Left, nodeId, nodes, arguments, expression, rootToken)
                || ContainsContextRootReference(node.Right, nodeId, nodes, arguments, expression, rootToken),
            ExpressionNodeKind.MemberAccess => ContainsContextRootReference(node.Left, nodeId, nodes, arguments, expression, rootToken),
            ExpressionNodeKind.WildcardAccess => ContainsContextRootReference(node.Left, nodeId, nodes, arguments, expression, rootToken),
            ExpressionNodeKind.IndexAccess => ContainsContextRootReference(node.Left, nodeId, nodes, arguments, expression, rootToken)
                || ContainsContextRootReference(node.Right, nodeId, nodes, arguments, expression, rootToken),
            ExpressionNodeKind.FunctionCall => ContainsContextRootReferenceInFunction(node, nodeId, nodes, arguments, expression, rootToken),
            _ => false,
        };
    }

    private static bool ContainsContextRootReferenceInFunction(
        ExpressionNode functionCallNode,
        int functionCallNodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        ReadOnlySpan<byte> rootToken)
    {
        if (ContainsContextRootReference(functionCallNode.Left, functionCallNodeId, nodes, arguments, expression, rootToken))
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

            if (ContainsContextRootReference(arguments[argIndex], functionCallNodeId, nodes, arguments, expression, rootToken))
            {
                return true;
            }
        }

        return false;
    }

    // Env-Mapping Resolution

    internal static bool TryResolveShellVariableNameInEnv(AstArena arena, Env? env, byte[] utf8Yaml, string targetName, SimpleReferenceParser parser, out string variableName)
    {
        variableName = string.Empty;
        if (env?.Vars is null || env.Vars.Value.Count == 0)
        {
            return false;
        }

        var matches = 0;
        foreach (var pair in env.Vars.Value)
        {
            var envVar = pair.Value;
            var nameBytes = arena.GetStringValue(envVar.Name);
            var nameSliceLength = arena.GetStringSlice(envVar.Name).Length;

            // Span-based identifier validation — avoids string allocation for non-matching entries
            if (!IsValidIdentifierSpan(nameBytes, nameSliceLength))
            {
                continue;
            }

            if (!TryExtractExpressionBody(arena, envVar.Value, utf8Yaml, out var body)
                || !parser(body, out var candidateName)
                || !string.Equals(candidateName, targetName, StringComparison.Ordinal))
            {
                continue;
            }

            // Only allocate the env var name string after confirming value match
            variableName = Encoding.UTF8.GetString(nameBytes[..nameSliceLength]);
            matches++;
            if (matches > 1)
            {
                return false;
            }
        }

        return matches == 1;
    }

    /// <summary>
    /// Validates that the byte span up to <paramref name="length"/> forms a valid identifier
    /// (starts with letter/underscore, followed by letters/digits/underscores).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidIdentifierSpan(ReadOnlySpan<byte> nameBytes, int length)
    {
        if (length == 0 || nameBytes.Length < length)
        {
            return false;
        }

        if (!IsIdentifierStart(nameBytes[0]))
        {
            return false;
        }

        for (var i = 1; i < length; i++)
        {
            if (!IsIdentifierPart(nameBytes[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryResolveShellVariableName(
        AstArena arena,
        Env? stepEnv, Env? jobEnv, Env? workflowEnv,
        byte[] utf8Yaml, string targetName, SimpleReferenceParser parser,
        out string variableName)
    {
        variableName = string.Empty;
        var matchCount = 0;
        if (TryResolveShellVariableNameInEnv(arena, stepEnv, utf8Yaml, targetName, parser, out var stepVariable))
        {
            variableName = stepVariable;
            matchCount++;
        }

        if (TryResolveShellVariableNameInEnv(arena, jobEnv, utf8Yaml, targetName, parser, out var jobVariable))
        {
            variableName = jobVariable;
            matchCount++;
        }

        if (TryResolveShellVariableNameInEnv(arena, workflowEnv, utf8Yaml, targetName, parser, out var workflowVariable))
        {
            variableName = workflowVariable;
            matchCount++;
        }

        return matchCount == 1;
    }

    // HereDoc Detection

    internal static bool IsInsideNoExpandHereDoc(byte[] source, int targetOffset)
    {
        if (source.Length == 0 || (uint)targetOffset >= (uint)source.Length)
        {
            return false;
        }

        Span<HereDocState> hereDocs = stackalloc HereDocState[4];
        var hereDocCount = 0;
        var targetLine = 1;
        for (var i = 0; i < targetOffset; i++)
        {
            if (source[i] == (byte)'\n')
            {
                targetLine++;
            }
        }

        var currentLine = 1;
        var lineStart = 0;
        while (lineStart <= source.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < source.Length && source[lineEnd] != (byte)'\n')
            {
                lineEnd++;
            }

            var isTargetLine = currentLine == targetLine;
            var line = source.AsSpan(lineStart, lineEnd - lineStart);
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (hereDocCount > 0)
            {
                var top = hereDocs[hereDocCount - 1];
                var candidate = line;
                var yamlIndent = 0;
                while (yamlIndent < candidate.Length && yamlIndent < top.ContentIndentLength
                    && (candidate[yamlIndent] == (byte)' ' || candidate[yamlIndent] == (byte)'\t'))
                {
                    yamlIndent++;
                }

                candidate = candidate[yamlIndent..];
                if (top.StripTabs)
                {
                    var trimIndex = 0;
                    while (trimIndex < candidate.Length && candidate[trimIndex] == (byte)'\t')
                    {
                        trimIndex++;
                    }

                    candidate = candidate[trimIndex..];
                }

                if (candidate.SequenceEqual(source.AsSpan(top.TerminatorOffset, top.TerminatorLength)))
                {
                    hereDocCount--;
                }
                else if (isTargetLine)
                {
                    return true;
                }
            }
            else
            {
                if (TryParseNoExpandHereDocStart(line, lineStart, out var state) && hereDocCount < hereDocs.Length)
                {
                    hereDocs[hereDocCount++] = state;
                }

                if (isTargetLine)
                {
                    return false;
                }
            }

            if (lineEnd >= source.Length)
            {
                break;
            }

            currentLine++;
            lineStart = lineEnd + 1;
        }

        return false;
    }

    internal static bool TryParseNoExpandHereDocStart(ReadOnlySpan<byte> line, int lineStartInSource, out HereDocState state)
    {
        state = default;
        var i = 0;
        while (i < line.Length - 1)
        {
            if (line[i] != (byte)'<' || line[i + 1] != (byte)'<')
            {
                i++;
                continue;
            }

            i += 2;
            var stripTabs = false;
            if (i < line.Length && line[i] == (byte)'-')
            {
                stripTabs = true;
                i++;
            }

            while (i < line.Length && (line[i] == (byte)' ' || line[i] == (byte)'\t'))
            {
                i++;
            }

            if (i >= line.Length)
            {
                return false;
            }

            var quote = line[i];
            if (quote is not ((byte)'\'' or (byte)'"'))
            {
                return false;
            }

            i++;
            var start = i;
            while (i < line.Length && line[i] != quote)
            {
                i++;
            }

            if (i <= start || i >= line.Length)
            {
                return false;
            }

            var contentIndentLength = 0;
            while (contentIndentLength < line.Length && (line[contentIndentLength] == (byte)' ' || line[contentIndentLength] == (byte)'\t'))
            {
                contentIndentLength++;
            }

            state = new HereDocState(lineStartInSource + start, i - start, stripTabs, contentIndentLength);
            return true;
        }

        return false;
    }

    internal readonly record struct HereDocState(int TerminatorOffset, int TerminatorLength, bool StripTabs, int ContentIndentLength);

    // Single-Quote Detection

    /// <summary>
    /// Returns true when <paramref name="targetOffset"/> falls inside a shell single-quoted
    /// string on the same line. Shell single quotes suppress all variable expansion, so
    /// replacing ${{ }} with ${VAR} would be ineffective.
    /// </summary>
    internal static bool IsInsideShellSingleQuotes(byte[] source, int targetOffset)
    {
        if (source.Length == 0 || (uint)targetOffset >= (uint)source.Length)
        {
            return false;
        }

        // Find start of the line containing targetOffset
        var lineStart = targetOffset;
        while (lineStart > 0 && source[lineStart - 1] != (byte)'\n')
        {
            lineStart--;
        }

        // Walk from lineStart to targetOffset using a small single-line shell
        // quoting state machine. Single quotes only toggle when not inside
        // double quotes. Double quotes only toggle when not inside single
        // quotes. Backslashes escape the next character outside single quotes.
        var insideSingleQuote = false;
        var insideDoubleQuote = false;
        var escaped = false;
        for (var i = lineStart; i < targetOffset; i++)
        {
            var current = source[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (insideSingleQuote)
            {
                if (current == (byte)'\'')
                {
                    insideSingleQuote = false;
                }

                continue;
            }

            if (current == (byte)'\\')
            {
                escaped = true;
                continue;
            }

            if (current == (byte)'"')
            {
                insideDoubleQuote = !insideDoubleQuote;
                continue;
            }

            if (!insideDoubleQuote && current == (byte)'\'')
            {
                insideSingleQuote = true;
            }
        }

        return insideSingleQuote;
    }

    // Step Env Insertion Utilities

    /// <summary>
    /// Builds a <see cref="TextEdit"/> that inserts an <c>env:</c> entry (or appends to existing step env)
    /// mapping <paramref name="envVarName"/> to <c>${{ expressionString }}</c>.
    /// </summary>
    internal static bool TryBuildStepEnvInsertionEdit(
        AstArena arena, byte[] utf8Yaml, Step step,
        string envVarName, string expressionString, out TextEdit edit)
    {
        edit = default;
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

            var lastEnvLine = FindLastEnvEntryLine(arena, utf8Yaml, step.Env);
            if (lastEnvLine < 1)
            {
                return false;
            }

            var envKeyLine = FindEnvKeyLine(arena, utf8Yaml, step.Env);
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

        // Empty env mapping (env: {}) cannot be extended by insertion
        if (step.Env is not null)
        {
            return false;
        }

        // No existing env: insert env block after the run value
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

    /// <summary>Deduplicates an env var name against existing env names in the step/job/workflow scope.</summary>
    internal static string? DeduplicateEnvName(
        AstArena arena, string baseName,
        Env? stepEnv, Env? jobEnv, Env? workflowEnv)
    {
        // Fast path: span-based comparison avoids HashSet allocation when no conflict exists
        if (!EnvContainsNameIgnoreCase(arena, baseName, stepEnv)
            && !EnvContainsNameIgnoreCase(arena, baseName, jobEnv)
            && !EnvContainsNameIgnoreCase(arena, baseName, workflowEnv))
        {
            return baseName;
        }

        // Conflict found — need full set for numbered suffix search
        var existing = CollectExistingEnvNames(arena, stepEnv, jobEnv, workflowEnv);
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

    /// <summary>
    /// Checks if any env var in <paramref name="env"/> has a name matching <paramref name="name"/>
    /// (case-insensitive, pure span comparison, zero allocation).
    /// </summary>
    private static bool EnvContainsNameIgnoreCase(AstArena arena, string name, Env? env)
    {
        if (env?.Vars is null)
        {
            return false;
        }

        foreach (var pair in env.Vars.Value)
        {
            var nameBytes = arena.GetStringValue(pair.Value.Name);
            if (EqualsUtf8AsciiIgnoreCase(nameBytes, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Compares UTF-8 bytes against an ASCII string case-insensitively without allocating.
    /// Assumes both sides contain only ASCII characters.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EqualsUtf8AsciiIgnoreCase(ReadOnlySpan<byte> utf8, string ascii)
    {
        if (utf8.Length != ascii.Length)
        {
            return false;
        }

        for (var i = 0; i < utf8.Length; i++)
        {
            var b = utf8[i];
            var c = (byte)ascii[i];
            if (b >= (byte)'A' && b <= (byte)'Z') b = (byte)(b + 32);
            if (c >= (byte)'A' && c <= (byte)'Z') c = (byte)(c + 32);
            if (b != c)
            {
                return false;
            }
        }

        return true;
    }

    private static HashSet<string> CollectExistingEnvNames(
        AstArena arena,
        Env? stepEnv, Env? jobEnv, Env? workflowEnv)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectEnvNames(arena, stepEnv, names);
        CollectEnvNames(arena, jobEnv, names);
        CollectEnvNames(arena, workflowEnv, names);
        return names;
    }

    private static void CollectEnvNames(AstArena arena, Env? env, HashSet<string> names)
    {
        if (env?.Vars is null)
        {
            return;
        }

        foreach (var pair in env.Vars.Value)
        {
            var nameBytes = arena.GetStringValue(pair.Value.Name);
            var nameIndex = 0;
            if (TryReadIdentifier(nameBytes, ref nameIndex, out var name) && nameIndex == nameBytes.Length)
            {
                names.Add(name);
            }
        }
    }

    internal static int FindLastEnvEntryLine(AstArena arena, byte[] utf8Yaml, Env env)
    {
        if (env.Vars is null)
        {
            return -1;
        }

        var maxEndOffset = 0;
        foreach (var pair in env.Vars.Value)
        {
            var valueRange = arena.GetStringRange(pair.Value.Value);
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

        return FindLineNumberFromOffset(utf8Yaml, maxEndOffset - 1);
    }

    internal static int FindEnvKeyLine(AstArena arena, byte[] utf8Yaml, Env env)
    {
        if (env.Vars is null)
        {
            return -1;
        }

        foreach (var pair in env.Vars.Value)
        {
            var nameRange = arena.GetStringRange(pair.Value.Name);
            if (nameRange.Start >= 0)
            {
                return FindLineNumberFromOffset(utf8Yaml, nameRange.Start);
            }
        }

        return -1;
    }

    internal static bool IsFlowStyleEnv(byte[] utf8Yaml, Env env)
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

    internal static int FindRunKeyOffset(byte[] utf8Yaml, int valueStart)
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

            if (i + 3 < pos
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

    internal static int FindLineNumberFromOffset(byte[] utf8Yaml, int offset)
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

    internal static int FindLineStartOffset(byte[] utf8Yaml, int lineNumber)
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

    internal static int FindLineEndOffsetIncludingNewLine(byte[] utf8Yaml, int lineNumber)
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

    internal static string GetStepKeyIndentation(byte[] utf8Yaml, int lineNumber)
    {
        var baseIndent = FixFormatting.GetLineIndentation(utf8Yaml, lineNumber);
        var lineStart = FindLineStartOffset(utf8Yaml, lineNumber);
        var offset = lineStart + baseIndent.Length;
        return offset + 1 < utf8Yaml.Length && utf8Yaml[offset] == (byte)'-' && utf8Yaml[offset + 1] == (byte)' '
            ? baseIndent + "  "
            : baseIndent;
    }

    internal static int FindRunEndLine(byte[] utf8Yaml, int runKeyLine, string stepKeyIndent)
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

    /// <summary>
    /// Reads a GitHub Actions identifier allowing hyphens (e.g. "benchmark-config-path").
    /// Matches the expression parser's identifier behavior.
    /// </summary>
    internal static bool TryReadGitHubIdentifier(ReadOnlySpan<byte> expression, ref int index, out string identifier)
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

        identifier = Encoding.UTF8.GetString(expression[start..index]);
        return true;
    }

    /// <summary>
    /// Like <see cref="TryConsumeMemberOrBracketName"/> but allows hyphens in dot-access identifiers
    /// and bracket-access quoted names, matching GitHub Actions expression parser behavior.
    /// </summary>
    internal static bool TryConsumeGitHubMemberOrBracketName(ReadOnlySpan<byte> expression, ref int index, out string name)
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

        if (expression[index] != (byte)'[')
        {
            return false;
        }

        index++;
        SkipWhiteSpace(expression, ref index);
        if (index >= expression.Length)
        {
            return false;
        }

        var quote = expression[index];
        if (quote is not ((byte)'\'' or (byte)'"'))
        {
            return false;
        }

        index++;
        var start = index;
        while (index < expression.Length && expression[index] != quote)
        {
            index++;
        }

        if (index >= expression.Length)
        {
            return false;
        }

        var nameBytes = expression[start..index];
        index++;
        SkipWhiteSpace(expression, ref index);
        if (index >= expression.Length || expression[index] != (byte)']')
        {
            return false;
        }

        index++;
        SkipWhiteSpace(expression, ref index);
        if (index != expression.Length)
        {
            return false;
        }

        // Validate as a GitHub identifier (allows hyphens)
        var parsedIndex = 0;
        if (!TryReadGitHubIdentifier(nameBytes, ref parsedIndex, out name) || parsedIndex != nameBytes.Length)
        {
            name = string.Empty;
            return false;
        }

        return true;
    }
}
