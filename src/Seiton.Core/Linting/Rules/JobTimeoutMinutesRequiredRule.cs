using Seiton.Core.Parsing.Ast;
using Seiton.Core.Parsing;
using Seiton.Core.Linting.Fixing;

namespace Seiton.Core.Linting.Rules;

/// <summary>Requires each job to declare an explicit <c>timeout-minutes</c> value.</summary>
public sealed class JobTimeoutMinutesRequiredRule() : RuleBase(RuleId.JobTimeoutMinutesRequired)
{
    public override string Name => "Job Timeout Minutes Required Rule";

    public override void VisitJobPre(Job job)
    {
        if (!IsExecutableJob(job) || job.TimeoutMinutes.HasValue)
        {
            return;
        }

        if (AllStepsHaveTimeout(job.Steps))
        {
            return;
        }

        var jobId = Decode(Arena.GetStringSlice(job.Id));
        var message = $"jobs.'{jobId}' should define timeout-minutes (default is 360 minutes); if not possible, set timeout-minutes on each step instead";
        if (Config.Fix.Enabled
            && Config.Utf8Yaml is not null
            && Config.Fix.Defaults.JobTimeoutMinutes is not null
            && Config.Fix.Defaults.JobTimeoutMinutes.Value > 0
            && TryBuildJobTimeoutInsertFix(Config, job, Config.Utf8Yaml, Config.Fix.Defaults.JobTimeoutMinutes.Value, out var fix))
        {
            AddJobError(job, message, BuildJobLocation(job), fix);
            return;
        }

        AddJobError(job, message, BuildJobLocation(job));
    }

    private static bool IsExecutableJob(Job job)
    {
        return job.WorkflowCall is null
            && job.Steps is not null
            && job.Steps.Count > 0;
    }

    private static bool AllStepsHaveTimeout(IReadOnlyList<Step>? steps)
    {
        if (steps is null || steps.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            if (!steps[i].TimeoutMinutes.HasValue)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryBuildJobTimeoutInsertFix(LintConfig config, Job job, byte[] utf8Yaml, int timeoutMinutes, out DiagnosticFix fix)
    {
        fix = default;
        if (timeoutMinutes <= 0)
        {
            return false;
        }

        if (utf8Yaml.Length == 0)
        {
            return false;
        }

        var jobLine = Arena.GetStringRange(job.Id).StartLine;
        if (jobLine < 1)
        {
            return false;
        }

        var jobEndLine = job.Range.EndLine;
        if (jobEndLine < jobLine + 1)
        {
            jobEndLine = jobLine + 1;
        }

        var parentIndent = FixFormatting.GetLineIndentation(utf8Yaml, jobLine);
        var firstChildLine = FindFirstChildLine(utf8Yaml, jobLine + 1, jobEndLine, parentIndent);
        if (firstChildLine < 0)
        {
            return false;
        }

        var scopeStartLine = Math.Max(1, jobLine + 1);
        var scopeEndLine = Math.Max(scopeStartLine, jobEndLine);
        if (!FixFormatting.TryInferIndentation(
                utf8Yaml,
                firstChildLine,
                parentLineNumber: jobLine,
                scopeStartLine: scopeStartLine,
                scopeEndLine: scopeEndLine,
                out var bodyIndent))
        {
            return false;
        }

        var lineEnding = FixFormatting.DetectDominantLineEnding(utf8Yaml);
        var timeoutLine = bodyIndent + "timeout-minutes: " + timeoutMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + lineEnding;
        var anchorLine = FindKeyLine(utf8Yaml, jobLine + 1, jobEndLine, bodyIndent, "runs-on:"u8);

        int insertOffset;
        string insertText;

        if (anchorLine >= 0)
        {
            insertOffset = FindLineEndOffsetIncludingNewLine(utf8Yaml, anchorLine);
            if (insertOffset > 0 && insertOffset <= utf8Yaml.Length && utf8Yaml[insertOffset - 1] != (byte)'\n')
            {
                insertText = lineEnding + timeoutLine;
            }
            else
            {
                insertText = timeoutLine;
            }
        }
        else
        {
            var firstSiblingLine = FindFirstMappingSiblingLine(utf8Yaml, jobLine + 1, jobEndLine, bodyIndent);
            if (firstSiblingLine >= 0)
            {
                insertOffset = FindLineStartOffset(utf8Yaml, firstSiblingLine);
                insertText = timeoutLine;
            }
            else
            {
                insertOffset = FindLineEndOffsetIncludingNewLine(utf8Yaml, jobLine);
                if (insertOffset > 0 && insertOffset <= utf8Yaml.Length && utf8Yaml[insertOffset - 1] != (byte)'\n')
                {
                    insertText = lineEnding + timeoutLine;
                }
                else
                {
                    insertText = timeoutLine;
                }
            }
        }

        fix = new DiagnosticFix(
            "insert timeout-minutes using configured default",
            [new TextEdit(insertOffset, 0, insertText)]);
        return true;
    }

    private static int FindKeyLine(byte[] utf8Yaml, int startLine, int endLine, string indent, ReadOnlySpan<byte> keyPrefix)
    {
        var currentLine = 1;
        var pos = 0;
        while (currentLine < startLine && pos < utf8Yaml.Length)
            if (utf8Yaml[pos++] == (byte)'\n') currentLine++;

        while (currentLine <= endLine && pos <= utf8Yaml.Length)
        {
            if (pos >= utf8Yaml.Length) break;
            var lineStart = pos;
            while (pos < utf8Yaml.Length && utf8Yaml[pos] != (byte)'\n') pos++;
            var lineEnd = pos;
            if (lineEnd > lineStart && utf8Yaml[lineEnd - 1] == (byte)'\r') lineEnd--;
            if (pos < utf8Yaml.Length) pos++;

            if (ByteLineHasKeyAtIndent(utf8Yaml, lineStart, lineEnd, indent, keyPrefix))
                return currentLine;

            currentLine++;
        }
        return -1;
    }

    private static int FindFirstMappingSiblingLine(byte[] utf8Yaml, int startLine, int endLine, string indent)
    {
        var currentLine = 1;
        var pos = 0;
        while (currentLine < startLine && pos < utf8Yaml.Length)
            if (utf8Yaml[pos++] == (byte)'\n') currentLine++;

        while (currentLine <= endLine && pos <= utf8Yaml.Length)
        {
            if (pos >= utf8Yaml.Length) break;
            var lineStart = pos;
            while (pos < utf8Yaml.Length && utf8Yaml[pos] != (byte)'\n') pos++;
            var lineEnd = pos;
            if (lineEnd > lineStart && utf8Yaml[lineEnd - 1] == (byte)'\r') lineEnd--;
            if (pos < utf8Yaml.Length) pos++;

            if (IsMappingSiblingLine(utf8Yaml, lineStart, lineEnd, indent))
                return currentLine;

            currentLine++;
        }
        return -1;
    }

    private static int FindFirstChildLine(byte[] utf8Yaml, int startLine, int endLine, string parentIndent)
    {
        var currentLine = 1;
        var pos = 0;
        while (currentLine < startLine && pos < utf8Yaml.Length)
            if (utf8Yaml[pos++] == (byte)'\n') currentLine++;

        while (currentLine <= endLine && pos <= utf8Yaml.Length)
        {
            if (pos >= utf8Yaml.Length) break;
            var lineStart = pos;
            while (pos < utf8Yaml.Length && utf8Yaml[pos] != (byte)'\n') pos++;
            var lineEnd = pos;
            if (lineEnd > lineStart && utf8Yaml[lineEnd - 1] == (byte)'\r') lineEnd--;
            if (pos < utf8Yaml.Length) pos++;

            if (IsChildLine(utf8Yaml, lineStart, lineEnd, parentIndent))
                return currentLine;

            currentLine++;
        }
        return -1;
    }

    private static bool IsChildLine(byte[] utf8Yaml, int lineStart, int lineEnd, string parentIndent)
    {
        var lineLen = lineEnd - lineStart;
        if (lineLen == 0) return false;
        var firstNonWs = lineStart;
        while (firstNonWs < lineEnd && (utf8Yaml[firstNonWs] == (byte)' ' || utf8Yaml[firstNonWs] == (byte)'\t')) firstNonWs++;
        if (firstNonWs >= lineEnd) return false;
        if (lineLen < parentIndent.Length) return false;
        for (var k = 0; k < parentIndent.Length; k++)
            if (utf8Yaml[lineStart + k] != (byte)parentIndent[k]) return false;
        var tailStart = lineStart + parentIndent.Length;
        if (tailStart >= lineEnd) return false;
        var tailByte = utf8Yaml[tailStart];
        if (tailByte != (byte)' ' && tailByte != (byte)'\t') return false;
        var restStart = tailStart;
        while (restStart < lineEnd && (utf8Yaml[restStart] == (byte)' ' || utf8Yaml[restStart] == (byte)'\t')) restStart++;
        return restStart < lineEnd && utf8Yaml[restStart] != (byte)'#';
    }

    private static bool IsMappingSiblingLine(byte[] utf8Yaml, int lineStart, int lineEnd, string indent)
    {
        var lineLen = lineEnd - lineStart;
        if (lineLen == 0) return false;
        var firstNonWs = lineStart;
        while (firstNonWs < lineEnd && (utf8Yaml[firstNonWs] == (byte)' ' || utf8Yaml[firstNonWs] == (byte)'\t')) firstNonWs++;
        if (firstNonWs >= lineEnd) return false;
        if (lineLen < indent.Length) return false;
        for (var k = 0; k < indent.Length; k++)
            if (utf8Yaml[lineStart + k] != (byte)indent[k]) return false;
        var restStart = lineStart + indent.Length;
        while (restStart < lineEnd && (utf8Yaml[restStart] == (byte)' ' || utf8Yaml[restStart] == (byte)'\t')) restStart++;
        if (restStart >= lineEnd) return false;
        return utf8Yaml[restStart] != (byte)'#';
    }

    private static bool ByteLineHasKeyAtIndent(byte[] utf8Yaml, int lineStart, int lineEnd, string indent, ReadOnlySpan<byte> keyBytes)
    {
        if (lineEnd - lineStart < indent.Length) return false;
        for (var k = 0; k < indent.Length; k++)
            if (utf8Yaml[lineStart + k] != (byte)indent[k]) return false;
        var idx = lineStart + indent.Length;
        while (idx < lineEnd && (utf8Yaml[idx] == (byte)' ' || utf8Yaml[idx] == (byte)'\t')) idx++;
        var remaining = lineEnd - idx;
        if (remaining < keyBytes.Length) return false;
        return utf8Yaml.AsSpan(idx, keyBytes.Length).SequenceEqual(keyBytes);
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
}
