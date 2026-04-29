using Seiton.Core.Linting.Fixing;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;
using static Seiton.Core.Linting.Rules.RunContextDirectUseAnalyzer;

namespace Seiton.Core.Linting.Rules;

/// <summary>Detects expressions in <c>run:</c> scripts that may be vulnerable to template injection attacks.</summary>
public sealed class TemplateInjectionRule() : RuleBase(RuleId.TemplateInjection)
{
    private Workflow? _currentWorkflow;
    private Job? _currentJob;
    private static readonly string[][] untrustedPaths =
    [
        ["github", "event", "issue", "title"],
        ["github", "event", "issue", "body"],
        ["github", "event", "pull_request", "title"],
        ["github", "event", "pull_request", "body"],
        ["github", "event", "pull_request", "head", "ref"],
        ["github", "event", "pull_request", "head", "label"],
        ["github", "event", "pull_request", "head", "repo", "default_branch"],
        ["github", "event", "comment", "body"],
        ["github", "event", "review", "body"],
        ["github", "event", "review_comment", "body"],
        ["github", "event", "pages", "*", "page_name"],
        ["github", "event", "commits", "*", "message"],
        ["github", "event", "commits", "*", "author", "email"],
        ["github", "event", "commits", "*", "author", "name"],
        ["github", "event", "head_commit", "message"],
        ["github", "event", "head_commit", "author", "email"],
        ["github", "event", "head_commit", "author", "name"],
        ["github", "event", "discussion", "title"],
        ["github", "event", "discussion", "body"],
        ["github", "head_ref"],
    ];

    public override string Name => "Template Injection Rule";

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
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        if (step.Exec is ExecRun run)
        {
            CheckSink(step, run.Run, "run");
        }
        else if (step.Exec is ExecAction action)
        {
            CheckActionScriptSink(step, action);
        }
    }

    private void CheckActionScriptSink(Step step, ExecAction action)
    {
        if (!action.Uses.HasValue || action.Inputs is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var uses = Arena.GetStringValue(action.Uses);
        if (!IsGithubScriptAction(uses))
        {
            return;
        }

        foreach (var pair in action.Inputs)
        {
            var keySpan = pair.Key.AsSpan(Config.Utf8Yaml);
            if (keySpan.SequenceEqual("script"u8))
            {
                CheckSink(step, pair.Value, "script");
                return;
            }
        }
    }

    private static bool IsGithubScriptAction(ReadOnlySpan<byte> uses)
    {
        // Match actions/github-script@<any version>
        const byte AtSign = (byte)'@';
        var atIndex = uses.IndexOf(AtSign);
        if (atIndex < 0)
        {
            return false;
        }

        return uses[..atIndex].SequenceEqual("actions/github-script"u8);
    }

    private void CheckSink(Step step, StringNodeId valueNode, string sinkName)
    {
        if (!valueNode.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var value = Arena.GetStringValue(valueNode);
        var valueSlice = Arena.GetStringSlice(valueNode);
        var lineStarts = Config.GetLineStarts();
        var searchStart = 0;
        while (TryFindExpression(value, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;

            var expression = TrimAsciiWhiteSpace(value.Slice(bodyStart, bodyLength));
            if (expression.Length == 0)
            {
                continue;
            }

            // Compute trim offset: how many bytes were trimmed from the left
            var rawExpression = value.Slice(bodyStart, bodyLength);
            var trimOffset = 0;
            while (trimOffset < rawExpression.Length && IsAsciiWhiteSpace(rawExpression[trimOffset]))
            {
                trimOffset++;
            }

            var parseResult = Config.ParseExpression(expression);
            if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
            {
                continue;
            }

            // Full ${{ ... }} expression span within the source
            var exprAbsoluteOffset = valueSlice.Offset + bodyStart - 3;
            var exprLength = nextSearchStart - (bodyStart - 3);

            ReportUntrustedReferences(step, parseResult, expression, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
        }
    }

    private void ReportUntrustedReferences(
        Step step,
        ExpressionParseResult parseResult,
        ReadOnlySpan<byte> expression,
        Utf8Slice valueSlice,
        int bodyStart,
        int trimOffset,
        int[] lineStarts,
        string sinkName,
        int exprAbsoluteOffset,
        int exprLength)
    {
        CollectUntrustedReferences(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression, safeDepth: 0,
            step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
    }

    private void CollectUntrustedReferences(
        int nodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        int safeDepth,
        Step step,
        Utf8Slice valueSlice,
        int bodyStart,
        int trimOffset,
        int[] lineStarts,
        string sinkName,
        int exprAbsoluteOffset,
        int exprLength)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return;
        }

        if (safeDepth == 0 && IsUntrustedReference(nodeId, nodes, expression))
        {
            EmitUntrustedDiagnostic(step, nodeId, nodes, expression, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
            // Also check index expressions within this path for nested untrusted references
            CollectNestedIndexReferences(nodeId, nodes, arguments, expression, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
            return;
        }

        var node = nodes[nodeId];
        switch (node.Kind)
        {
            case ExpressionNodeKind.Unary:
                CollectUntrustedReferences(node.Left, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
                break;
            case ExpressionNodeKind.Binary:
                CollectUntrustedReferences(node.Left, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
                CollectUntrustedReferences(node.Right, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
                break;
            case ExpressionNodeKind.MemberAccess:
                CollectUntrustedReferences(node.Left, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
                break;
            case ExpressionNodeKind.WildcardAccess:
                CollectUntrustedReferences(node.Left, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
                break;
            case ExpressionNodeKind.IndexAccess:
                CollectUntrustedReferences(node.Left, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
                CollectUntrustedReferences(node.Right, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
                break;
            case ExpressionNodeKind.FunctionCall:
                CollectUntrustedReferencesInFunction(node, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
                break;
        }
    }

    private void CollectUntrustedReferencesInFunction(
        ExpressionNode functionCallNode,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        int safeDepth,
        Step step,
        Utf8Slice valueSlice,
        int bodyStart,
        int trimOffset,
        int[] lineStarts,
        string sinkName,
        int exprAbsoluteOffset,
        int exprLength)
    {
        var calleeSafeDepth = safeDepth;
        if (IsSafeFunctionCall(functionCallNode, nodes, expression))
        {
            calleeSafeDepth++;
        }

        CollectUntrustedReferences(functionCallNode.Left, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);

        for (var i = 0; i < functionCallNode.ArgCount; i++)
        {
            var argIndex = functionCallNode.ArgStart + i;
            if (argIndex < 0 || argIndex >= arguments.Length)
            {
                continue;
            }

            CollectUntrustedReferences(arguments[argIndex], nodes, arguments, expression, calleeSafeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
        }
    }

    /// <summary>Walk a matched untrusted path tree and check IndexAccess right-side sub-expressions for nested untrusted references.</summary>
    private void CollectNestedIndexReferences(
        int nodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        Step step,
        Utf8Slice valueSlice,
        int bodyStart,
        int trimOffset,
        int[] lineStarts,
        string sinkName,
        int exprAbsoluteOffset,
        int exprLength)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return;
        }

        var node = nodes[nodeId];
        switch (node.Kind)
        {
            case ExpressionNodeKind.MemberAccess:
            case ExpressionNodeKind.WildcardAccess:
                CollectNestedIndexReferences(node.Left, nodes, arguments, expression, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
                break;
            case ExpressionNodeKind.IndexAccess:
                CollectNestedIndexReferences(node.Left, nodes, arguments, expression, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
                CollectUntrustedReferences(node.Right, nodes, arguments, expression, safeDepth: 0, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName, exprAbsoluteOffset, exprLength);
                break;
        }
    }

    private void EmitUntrustedDiagnostic(
        Step step,
        int nodeId,
        ExpressionNode[] nodes,
        ReadOnlySpan<byte> expression,
        Utf8Slice valueSlice,
        int bodyStart,
        int trimOffset,
        int[] lineStarts,
        string sinkName,
        int exprAbsoluteOffset,
        int exprLength)
    {
        // Build the dotted path string for the untrusted reference
        Span<PathSegment> segments = stackalloc PathSegment[16];
        if (!TryBuildPathSegments(nodeId, nodes, expression, segments, out var segCount))
        {
            return;
        }

        var pathString = BuildPathString(segments[..segCount], expression);

        // Find the root identifier token offset (leftmost identifier in the chain)
        var rootTokenOffset = FindRootIdentifierOffset(nodeId, nodes);

        // Compute precise position: absolute offset in UTF-8 YAML
        var absoluteStart = valueSlice.Offset + bodyStart + trimOffset + rootTokenOffset;
        var start = OffsetToLineColumn(lineStarts, absoluteStart);

        // End position spans the entire path expression
        var lastNode = nodes[nodeId];
        var endOffset = lastNode.Token.Offset + lastNode.Token.Length;
        var absoluteEnd = valueSlice.Offset + bodyStart + trimOffset + endOffset;
        var end = OffsetToLineColumn(lineStarts, absoluteEnd - 1);

        var location = new TextRange(
            Start: absoluteStart,
            Length: absoluteEnd - absoluteStart,
            StartLine: start.Line,
            StartColumn: start.Column,
            EndLine: end.Line,
            EndColumn: end.Column);

        var message = $"\"{pathString}\" is potentially untrusted. avoid using it directly in inline scripts. instead, pass it through an environment variable. see https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions#good-practices-for-mitigating-script-injection-attacks for more details";

        if (TryBuildFix(step, pathString, sinkName, exprAbsoluteOffset, exprLength, out var fix))
        {
            AddStepError(step, message, location, fix);
        }
        else
        {
            AddStepError(step, message, location);
        }
    }

    private bool TryBuildFix(Step step, string pathString, string sinkName, int exprAbsoluteOffset, int exprLength, out DiagnosticFix fix)
    {
        fix = default;
        if (!Config.Fix.Enabled || Config.Utf8Yaml is null)
        {
            return false;
        }

        // Wildcard paths can't generate a deterministic env var name
        if (pathString.Contains('*'))
        {
            return false;
        }

        // Only fix run: sinks (not github-script); inserting env on a uses step is more complex
        if (sinkName != "run")
        {
            return false;
        }

        // Check if an existing env mapping already points to this expression
        if (TryFindExistingEnvMapping(step, pathString, out var existingVarName))
        {
            // Only need to replace the expression with the shell variable reference
            var replacement = IsPowerShell(Arena, step, Config.Utf8Yaml)
                ? "$env:" + existingVarName
                : "${" + existingVarName + "}";

            fix = new DiagnosticFix(
                $"replace untrusted expression with existing env variable {existingVarName}",
                [new TextEdit(exprAbsoluteOffset, exprLength, replacement)]);
            return true;
        }

        // Generate mechanical env var name and deduplicate
        var envVarName = PathToEnvVarName(pathString);
        envVarName = DeduplicateEnvName(envVarName, step);

        // Build shell variable replacement
        var shellReplacement = IsPowerShell(Arena, step, Config.Utf8Yaml)
            ? "$env:" + envVarName
            : "${" + envVarName + "}";

        // Build env insertion: insert env line before the run key
        if (!TryBuildEnvInsertionEdit(step, envVarName, pathString, out var insertEdit))
        {
            return false;
        }

        fix = new DiagnosticFix(
            $"map untrusted expression to env variable {envVarName}",
            [insertEdit, new TextEdit(exprAbsoluteOffset, exprLength, shellReplacement)]);
        return true;
    }

    private bool TryFindExistingEnvMapping(Step step, string pathString, out string variableName)
    {
        variableName = string.Empty;
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        // Check step env, job env, workflow env for a unique mapping to this expression
        var matchCount = 0;
        if (TryFindEnvMappingInEnv(step.Env, pathString, out var stepVar))
        {
            variableName = stepVar;
            matchCount++;
        }

        if (TryFindEnvMappingInEnv(_currentJob?.Env, pathString, out var jobVar))
        {
            variableName = jobVar;
            matchCount++;
        }

        if (TryFindEnvMappingInEnv(_currentWorkflow?.Env, pathString, out var workflowVar))
        {
            variableName = workflowVar;
            matchCount++;
        }

        return matchCount == 1;
    }

    private bool TryFindEnvMappingInEnv(Env? env, string pathString, out string variableName)
    {
        variableName = string.Empty;
        if (env?.Vars is null || env.Vars.Value.Count == 0 || Config.Utf8Yaml is null)
        {
            return false;
        }

        var matches = 0;
        foreach (var pair in env.Vars.Value)
        {
            var envVar = pair.Value;
            if (!TryExtractExpressionBody(Arena, envVar.Value, Config.Utf8Yaml, out var body))
            {
                continue;
            }

            // Compare the expression body (trimmed) against pathString
            var bodyStr = System.Text.Encoding.UTF8.GetString(body);
            if (!string.Equals(bodyStr, pathString, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Read the env var name
            var nameBytes = Arena.GetStringValue(envVar.Name);
            var nameIndex = 0;
            if (!TryReadIdentifier(nameBytes, ref nameIndex, out var candidateName)
                || nameIndex != Arena.GetStringSlice(envVar.Name).Length)
            {
                continue;
            }

            variableName = candidateName;
            matches++;
            if (matches > 1)
            {
                return false;
            }
        }

        return matches == 1;
    }

    private bool TryBuildEnvInsertionEdit(Step step, string envVarName, string pathString, out TextEdit edit)
    {
        edit = default;
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        var utf8Yaml = Config.Utf8Yaml;
        var lineEnding = FixFormatting.DetectDominantLineEnding(utf8Yaml);

        // Find the run: key line by scanning backwards from the value offset.
        // For block scalars (run: |), the value range points into the script body,
        // so we must locate the actual "run:" key which is always before the value.
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

        // Compute the step-key indent, accounting for list item marker (- )
        var stepKeyIndent = GetStepKeyIndentation(utf8Yaml, runLine);

        if (step.Env?.Vars is not null && step.Env.Vars.Value.Count > 0)
        {
            // Existing env mapping: insert after the last env entry
            var lastEnvLine = FindLastEnvEntryLine(step.Env);
            if (lastEnvLine < 1)
            {
                return false;
            }

            var childIndent = FixFormatting.GetLineIndentation(utf8Yaml, lastEnvLine);
            var insertOffset = FindLineEndOffsetIncludingNewLine(utf8Yaml, lastEnvLine);
            var insertText = childIndent + envVarName + ": ${{ " + pathString + " }}" + lineEnding;
            edit = new TextEdit(insertOffset, 0, insertText);
            return true;
        }

        // No existing env: insert env block after the run: line (or block scalar content).
        // Inserting before `- run:` would place env: outside the list item mapping.
        var childIndentUnit = FixFormatting.InferIndentationUnit(utf8Yaml);
        var envChildIndent = stepKeyIndent + childIndentUnit;
        var runEndLine = FindRunEndLine(utf8Yaml, runLine, stepKeyIndent);
        var insertAfterRun = FindLineEndOffsetIncludingNewLine(utf8Yaml, runEndLine);
        // If the file doesn't end with a newline, prepend one before the env block
        var needsLeadingNewline = insertAfterRun == utf8Yaml.Length && utf8Yaml.Length > 0 && utf8Yaml[^1] != (byte)'\n';
        var envBlock = (needsLeadingNewline ? lineEnding : "")
            + stepKeyIndent + "env:" + lineEnding
            + envChildIndent + envVarName + ": ${{ " + pathString + " }}" + lineEnding;
        edit = new TextEdit(insertAfterRun, 0, envBlock);
        return true;
    }

    private int FindLastEnvEntryLine(Env env)
    {
        if (env.Vars is null || Config.Utf8Yaml is null)
        {
            return -1;
        }

        var maxLine = -1;
        foreach (var pair in env.Vars.Value)
        {
            var valueLine = FindLineNumberFromOffset(Config.Utf8Yaml, Arena.GetStringSlice(pair.Value.Value).Offset);
            if (valueLine > maxLine)
            {
                maxLine = valueLine;
            }
        }

        return maxLine;
    }

    private string DeduplicateEnvName(string baseName, Step step)
    {
        var existing = CollectExistingEnvNames(step);
        if (!existing.Contains(baseName, StringComparer.OrdinalIgnoreCase))
        {
            return baseName;
        }

        for (var i = 2; i <= 99; i++)
        {
            var candidate = baseName + "_" + i;
            if (!existing.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return baseName + "_99";
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

    internal static string PathToEnvVarName(string pathString)
    {
        var sb = new System.Text.StringBuilder(pathString.Length);
        for (var i = 0; i < pathString.Length; i++)
        {
            var c = pathString[i];
            if (c is '.' or '-')
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

    /// <summary>
    /// Gets the step key indentation, accounting for the YAML list item marker (<c>- </c>).
    /// For a line like <c>    - run: echo hello</c>, returns <c>"      "</c> (6 spaces)
    /// so that sibling keys align with <c>run:</c> inside the list item mapping.
    /// </summary>
    private static string GetStepKeyIndentation(byte[] utf8Yaml, int lineNumber)
    {
        var baseIndent = FixFormatting.GetLineIndentation(utf8Yaml, lineNumber);
        var lineStart = FindLineStartOffset(utf8Yaml, lineNumber);
        var offset = lineStart + baseIndent.Length;
        return offset + 1 < utf8Yaml.Length && utf8Yaml[offset] == (byte)'-' && utf8Yaml[offset + 1] == (byte)' '
            ? baseIndent + "  "
            : baseIndent;
    }

    /// <summary>
    /// Finds the last line of the run: value content.
    /// For inline scalars, this is the run: key line itself.
    /// For block scalars (run: |), this is the last indented content line.
    /// </summary>
    private static int FindRunEndLine(byte[] utf8Yaml, int runKeyLine, string stepKeyIndent)
    {
        var lastContentLine = runKeyLine;
        var stepKeyIndentLen = stepKeyIndent.Length;
        var currentLine = runKeyLine;
        var pos = FindLineStartOffset(utf8Yaml, runKeyLine);

        // Advance past the run key line
        while (pos < utf8Yaml.Length && utf8Yaml[pos] != (byte)'\n')
        {
            pos++;
        }

        if (pos < utf8Yaml.Length)
        {
            pos++; // skip '\n'
        }

        currentLine++;

        // Walk subsequent lines: content lines of a block scalar are indented
        // deeper than the step key indent. Stop at the first line at or less than
        // step key indent (next sibling key or dedent).
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
                pos++; // skip '\n'
            }

            // Empty or whitespace-only lines are part of the block scalar
            var indent = 0;
            while (indent < lineLen && utf8Yaml[lineStart + indent] == (byte)' ')
            {
                indent++;
            }

            if (indent >= lineLen || (lineLen > 0 && lineStart + indent < utf8Yaml.Length && utf8Yaml[lineStart + indent] == (byte)'\r' && indent + 1 >= lineLen))
            {
                // Empty line — still part of block scalar
                lastContentLine = currentLine;
                currentLine++;
                continue;
            }

            // Non-empty line: check if it's still indented deeper than the step key
            if (indent > stepKeyIndentLen)
            {
                lastContentLine = currentLine;
                currentLine++;
                continue;
            }

            // Line is at same or less indent — not part of the run value
            break;
        }

        return lastContentLine;
    }

    private static int FindRunKeyOffset(byte[] utf8Yaml, int valueStart)
    {
        // Scan backwards line-by-line from the value start to find the "run:" key.
        // For block scalars (run: |), the value range points into the script body,
        // so we must locate the actual "run:" key line which is always before the value.
        // We check each line for `run:` as a YAML key (preceded only by whitespace or `- `).
        var pos = Math.Min(valueStart, utf8Yaml.Length);
        while (pos > 0)
        {
            // Find start of the current line
            var lineStart = pos - 1;
            while (lineStart > 0 && utf8Yaml[lineStart - 1] != (byte)'\n')
            {
                lineStart--;
            }

            // Skip leading whitespace
            var i = lineStart;
            while (i < pos && utf8Yaml[i] == (byte)' ')
            {
                i++;
            }

            // Skip optional list item marker `- `
            if (i + 1 < pos && utf8Yaml[i] == (byte)'-' && utf8Yaml[i + 1] == (byte)' ')
            {
                i += 2;
            }

            // Check for `run:` at this position
            if (i + 3 < utf8Yaml.Length
                && utf8Yaml[i] == (byte)'r'
                && utf8Yaml[i + 1] == (byte)'u'
                && utf8Yaml[i + 2] == (byte)'n'
                && utf8Yaml[i + 3] == (byte)':')
            {
                return i;
            }

            // Move to previous line
            pos = lineStart;
        }

        return -1;
    }

    private static int FindRootIdentifierOffset(int nodeId, ExpressionNode[] nodes)
    {
        var current = nodeId;
        while (current >= 0 && current < nodes.Length)
        {
            var node = nodes[current];
            if (node.Kind == ExpressionNodeKind.Identifier)
            {
                return node.Token.Offset;
            }

            if (node.Kind is ExpressionNodeKind.MemberAccess or ExpressionNodeKind.WildcardAccess or ExpressionNodeKind.IndexAccess)
            {
                current = node.Left;
            }
            else
            {
                break;
            }
        }

        return 0;
    }

    private static string BuildPathString(ReadOnlySpan<PathSegment> segments, ReadOnlySpan<byte> expression)
    {
        var sb = new System.Text.StringBuilder(64);
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('.');
            }

            if (segments[i].IsWildcard)
            {
                sb.Append('*');
            }
            else
            {
                var span = segments[i].Token.AsSpan(expression);
                for (var j = 0; j < span.Length; j++)
                {
                    sb.Append((char)span[j]);
                }
            }
        }

        return sb.ToString();
    }

    private static bool IsSafeFunctionCall(ExpressionNode functionCallNode, ExpressionNode[] nodes, ReadOnlySpan<byte> expression)
    {
        if (functionCallNode.Left < 0 || functionCallNode.Left >= nodes.Length)
        {
            return false;
        }

        var callee = nodes[functionCallNode.Left];
        if (callee.Kind != ExpressionNodeKind.Identifier)
        {
            return false;
        }

        var calleeName = callee.Token.AsSpan(expression);
        return TokenEqualsIgnoreCase(calleeName, "contains"u8)
            || TokenEqualsIgnoreCase(calleeName, "startswith"u8)
            || TokenEqualsIgnoreCase(calleeName, "endswith"u8);
    }

    private static bool IsUntrustedReference(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression)
    {
        Span<PathSegment> segments = stackalloc PathSegment[16];
        if (!TryBuildPathSegments(nodeId, nodes, expression, segments, out var count))
        {
            return false;
        }

        for (var i = 0; i < untrustedPaths.Length; i++)
        {
            if (IsPathMatch(segments[..count], untrustedPaths[i], expression))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildPathSegments(
        int nodeId,
        ExpressionNode[] nodes,
        ReadOnlySpan<byte> expression,
        Span<PathSegment> destination,
        out int count)
    {
        count = 0;
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        switch (node.Kind)
        {
            case ExpressionNodeKind.Identifier:
                destination[0] = new PathSegment(node.Token, false);
                count = 1;
                return true;
            case ExpressionNodeKind.MemberAccess:
                if (!TryBuildPathSegments(node.Left, nodes, expression, destination, out count))
                {
                    return false;
                }

                if (count >= destination.Length)
                {
                    return false;
                }

                destination[count++] = new PathSegment(node.Token, false);
                return true;
            case ExpressionNodeKind.WildcardAccess:
                if (!TryBuildPathSegments(node.Left, nodes, expression, destination, out count))
                {
                    return false;
                }

                if (count >= destination.Length)
                {
                    return false;
                }

                destination[count++] = new PathSegment(default, true);
                return true;
            case ExpressionNodeKind.IndexAccess:
                if (!TryBuildPathSegments(node.Left, nodes, expression, destination, out count))
                {
                    return false;
                }

                if (count >= destination.Length)
                {
                    return false;
                }

                if (TryGetIndexSegment(node.Right, nodes, out var token))
                {
                    destination[count++] = new PathSegment(token, false);
                }
                else
                {
                    destination[count++] = new PathSegment(default, true);
                }

                return true;
            default:
                return false;
        }
    }

    private static bool TryGetIndexSegment(int nodeId, ExpressionNode[] nodes, out Utf8Slice token)
    {
        token = default;
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind is ExpressionNodeKind.StringLiteral or ExpressionNodeKind.Identifier)
        {
            token = node.Token;
            return true;
        }

        return false;
    }

    private static bool IsPathMatch(ReadOnlySpan<PathSegment> actual, string[] expected, ReadOnlySpan<byte> expression)
    {
        if (actual.Length != expected.Length)
        {
            return false;
        }

        for (var i = 0; i < actual.Length; i++)
        {
            var expectedSegment = expected[i];
            var actualSegment = actual[i];

            // Expected wildcard matches any actual segment
            if (expectedSegment == "*")
            {
                continue;
            }

            // Actual wildcard (e.g., github.event.*.body) matches any expected segment
            if (actualSegment.IsWildcard)
            {
                continue;
            }

            if (!TokenEqualsIgnoreCase(actualSegment.Token.AsSpan(expression), expectedSegment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TokenEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
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

    private static bool TokenEqualsIgnoreCase(ReadOnlySpan<byte> left, string right)
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

            if (r is >= 'A' and <= 'Z')
            {
                r = (char)(r + 32);
            }

            if (l != (byte)r)
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct PathSegment(Utf8Slice Token, bool IsWildcard);
}
