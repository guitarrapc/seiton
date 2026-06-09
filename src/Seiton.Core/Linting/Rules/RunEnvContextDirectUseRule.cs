using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using System.Buffers;

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
            else if (!TryGetSimpleEnvNameBounds(expression, out _, out _))
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

        if (!TryGetSimpleEnvNameBounds(expression, out _, out _))
        {
            return false;
        }

        var isPowerShell = RunContextDirectUseAnalyzer.IsPowerShellWithDefaults(Arena, step, _currentJob, _currentWorkflow, Config.Utf8Yaml);
        if (isPowerShell is null)
        {
            return false;
        }

        if (IsInsideShellSingleQuotes(Config.Utf8Yaml, absoluteOffset))
        {
            if (!TryBuildSingleQuotedSimpleEdit(Config.Utf8Yaml, absoluteOffset, expressionLength, expression, isPowerShell.Value, out var singleQuotedEdit))
            {
                return false;
            }

            fix = new DiagnosticFix(
                "replace direct env context expansion with shell variable",
                [singleQuotedEdit]);
            return true;
        }

        if (!TryBuildShellReplacement(expression, isPowerShell.Value, wrapInDoubleQuotes: false, out var replacement))
        {
            return false;
        }

        fix = new DiagnosticFix(
            "replace direct env context expansion with shell variable",
            [new TextEdit(absoluteOffset, expressionLength, replacement)]);
        return true;
    }

    private static bool TryBuildSingleQuotedSimpleEdit(
        byte[] source,
        int absoluteOffset,
        int expressionLength,
        ReadOnlySpan<byte> expression,
        bool isPowerShell,
        out TextEdit edit)
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

        if (!TryBuildShellReplacement(expression, isPowerShell, wrapInDoubleQuotes: true, out var replacement))
        {
            return false;
        }

        edit = new TextEdit(singleQuoteStart, expressionLength + 2, replacement);
        return true;
    }

    private static bool TryBuildShellReplacement(ReadOnlySpan<byte> expression, bool isPowerShell, bool wrapInDoubleQuotes, out string replacement)
    {
        replacement = string.Empty;
        if (!TryGetSimpleEnvNameBounds(expression, out var nameStart, out var nameLength))
        {
            return false;
        }

        var prefix = isPowerShell ? "$env:" : "${";
        var suffix = isPowerShell ? string.Empty : "}";
        var quoteChars = wrapInDoubleQuotes ? 2 : 0;
        var totalLength = quoteChars + prefix.Length + nameLength + suffix.Length;

        char[]? rented = null;
        Span<char> buffer = totalLength <= 128
            ? stackalloc char[totalLength]
            : (rented = ArrayPool<char>.Shared.Rent(totalLength));

        try
        {
            var destination = buffer[..totalLength];
            var index = 0;
            if (wrapInDoubleQuotes)
            {
                destination[index++] = '"';
            }

            prefix.AsSpan().CopyTo(destination[index..]);
            index += prefix.Length;

            var name = expression.Slice(nameStart, nameLength);
            for (var i = 0; i < name.Length; i++)
            {
                destination[index + i] = (char)name[i];
            }

            index += nameLength;
            if (!isPowerShell)
            {
                destination[index++] = '}';
            }

            if (wrapInDoubleQuotes)
            {
                destination[index] = '"';
            }

            replacement = new string(destination);
            return true;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    private static bool TryGetSimpleEnvNameBounds(ReadOnlySpan<byte> expression, out int nameStart, out int nameLength)
    {
        nameStart = 0;
        nameLength = 0;

        var index = 0;
        SkipAsciiWhiteSpace(expression, ref index);
        if (!TryConsumeAsciiIgnoreCase(expression, ref index, "env"u8))
        {
            return false;
        }

        SkipAsciiWhiteSpace(expression, ref index);
        if (index >= expression.Length)
        {
            return false;
        }

        if (expression[index] == (byte)'.')
        {
            index++;
            if (!TryReadIdentifierBounds(expression, ref index, out nameStart, out nameLength))
            {
                return false;
            }

            SkipAsciiWhiteSpace(expression, ref index);
            return index == expression.Length;
        }

        if (expression[index] != (byte)'[')
        {
            return false;
        }

        index++;
        SkipAsciiWhiteSpace(expression, ref index);
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
        nameStart = index;
        while (index < expression.Length && expression[index] != quote)
        {
            index++;
        }

        if (index >= expression.Length)
        {
            return false;
        }

        nameLength = index - nameStart;
        if (nameLength == 0 || !IsSimpleIdentifierAscii(expression.Slice(nameStart, nameLength)))
        {
            return false;
        }

        index++;
        SkipAsciiWhiteSpace(expression, ref index);
        if (index >= expression.Length || expression[index] != (byte)']')
        {
            return false;
        }

        index++;
        SkipAsciiWhiteSpace(expression, ref index);
        return index == expression.Length;
    }

    private static bool TryReadIdentifierBounds(ReadOnlySpan<byte> expression, ref int index, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (index >= expression.Length || !IsIdentifierStartAscii(expression[index]))
        {
            return false;
        }

        start = index;
        index++;
        while (index < expression.Length && IsIdentifierPartAscii(expression[index]))
        {
            index++;
        }

        length = index - start;
        return true;
    }

    private static bool IsSimpleIdentifierAscii(ReadOnlySpan<byte> identifier)
    {
        if (identifier.Length == 0 || !IsIdentifierStartAscii(identifier[0]))
        {
            return false;
        }

        for (var i = 1; i < identifier.Length; i++)
        {
            if (!IsIdentifierPartAscii(identifier[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void SkipAsciiWhiteSpace(ReadOnlySpan<byte> expression, ref int index)
    {
        while (index < expression.Length && expression[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            index++;
        }
    }

    private static bool TryConsumeAsciiIgnoreCase(ReadOnlySpan<byte> expression, ref int index, ReadOnlySpan<byte> token)
    {
        if (index + token.Length > expression.Length)
        {
            return false;
        }

        for (var i = 0; i < token.Length; i++)
        {
            var value = expression[index + i];
            if (value >= (byte)'A' && value <= (byte)'Z')
            {
                value = (byte)(value + 32);
            }

            if (value != token[i])
            {
                return false;
            }
        }

        index += token.Length;
        return true;
    }

    private static bool IsIdentifierStartAscii(byte value)
        => (value >= (byte)'A' && value <= (byte)'Z')
            || (value >= (byte)'a' && value <= (byte)'z')
            || value == (byte)'_';

    private static bool IsIdentifierPartAscii(byte value)
        => IsIdentifierStartAscii(value)
            || (value >= (byte)'0' && value <= (byte)'9');

    private static bool IsInsideNoExpandHereDoc(byte[] source, int targetOffset)
        => RunContextDirectUseAnalyzer.IsInsideNoExpandHereDoc(source, targetOffset);
}
