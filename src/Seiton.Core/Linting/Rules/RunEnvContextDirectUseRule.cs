using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;
using static Seiton.Core.Linting.Rules.RunContextDirectUseAnalyzer;

namespace Seiton.Core.Linting.Rules;

public sealed class RunEnvContextDirectUseRule : RuleBase
{
    public override string Id => "run-env-context-direct-use";

    public override string Name => "Run Env Context Direct Use Rule";

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

            if (TryBuildFix(run, runNode, expression, bodyStart, nextSearchStart - (bodyStart - 3), out var fix))
            {
                AddStepError(
                    step,
                    "run script must not reference ${{ env.* }} directly; use shell variables instead (e.g. $NAME or $env:NAME)",
                    location,
                    fix);
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

    private bool TryBuildFix(ExecRun run, StringNodeId runNode, ReadOnlySpan<byte> expression, int expressionBodyStart, int expressionLength, out DiagnosticFix fix)
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

        var replacement = RunContextDirectUseAnalyzer.IsPowerShell(Arena, run.Shell, Config.Utf8Yaml)
            ? "$env:" + variableName
            : "${" + variableName + "}";

        fix = new DiagnosticFix(
            "replace direct env context expansion with shell variable",
            [new TextEdit(absoluteOffset, expressionLength, replacement)]);
        return true;
    }

    private static bool IsInsideNoExpandHereDoc(byte[] source, int targetOffset)
    {
        if (source.Length == 0 || (uint)targetOffset >= (uint)source.Length)
        {
            return false;
        }

        var hereDocs = new List<HereDocState>(2);
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

            if (hereDocs.Count > 0)
            {
                var top = hereDocs[^1];
                var candidate = line;
                if (top.StripTabs)
                {
                    var trimIndex = 0;
                    while (trimIndex < candidate.Length && candidate[trimIndex] == (byte)'\t')
                    {
                        trimIndex++;
                    }

                    candidate = candidate[trimIndex..];
                }

                if (candidate.SequenceEqual(top.Terminator))
                {
                    hereDocs.RemoveAt(hereDocs.Count - 1);
                }
                else if (isTargetLine)
                {
                    return true;
                }
            }
            else
            {
                if (TryParseNoExpandHereDocStart(line, out var state))
                {
                    hereDocs.Add(state);
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

    private static bool TryParseNoExpandHereDocStart(ReadOnlySpan<byte> line, out HereDocState state)
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

            state = new HereDocState(line[start..i].ToArray(), stripTabs);
            return true;
        }

        return false;
    }

    private readonly record struct HereDocState(byte[] Terminator, bool StripTabs);
}
