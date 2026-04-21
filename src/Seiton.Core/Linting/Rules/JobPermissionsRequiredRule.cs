using Seiton.Core.Parsing.Ast;
using Seiton.Core.Parsing;
using Seiton.Core.Linting.Fixing;
using System.Text;

namespace Seiton.Core.Linting.Rules;

public sealed class JobPermissionsRequiredRule : RuleBase
{
    public override string Id => "job-permissions-required";

    public override string Name => "Job Permissions Required Rule";

    public override void VisitJobPre(Job job)
    {
        if (job.Permissions is not null)
        {
            return;
        }

        var jobId = Decode(job.Id.Value);
        var message = $"job '{jobId}' does not have permissions defined; set explicit permissions to follow least-privilege principle";
        if (Config.Fix.Enabled && Config.Utf8Yaml is not null && TryBuildPermissionsInsertFix(job, Config.Utf8Yaml, out var fix))
        {
            AddJobWarning(job, message, BuildJobLocation(job), fix);
            return;
        }

        AddJobWarning(job, message);
    }

    static bool TryBuildPermissionsInsertFix(Job job, byte[] utf8Yaml, out DiagnosticFix fix)
    {
        fix = default;
        var sourceText = Encoding.UTF8.GetString(utf8Yaml);
        var normalized = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (lines.Length == 0)
        {
            return false;
        }

        var jobLine = job.Id.Range.StartLine;
        if (jobLine < 1 || jobLine > lines.Length)
        {
            return false;
        }

        var jobEndLine = job.Range.EndLine;
        if (jobEndLine < jobLine + 1)
        {
            jobEndLine = Math.Min(lines.Length, jobLine + 1);
        }

        var parentIndent = FixFormatting.GetLineIndentation(sourceText, jobLine);
        var firstChildLine = FindFirstChildLine(lines, jobLine + 1, jobEndLine, parentIndent);
        if (firstChildLine < 0)
        {
            return false;
        }

        var scopeStartLine = Math.Min(Math.Max(1, jobLine + 1), lines.Length);
        var scopeEndLine = Math.Min(lines.Length, Math.Max(scopeStartLine, jobEndLine));
        if (!FixFormatting.TryInferIndentation(
                sourceText,
                firstChildLine,
                parentLineNumber: jobLine,
                scopeStartLine: scopeStartLine,
                scopeEndLine: scopeEndLine,
                out var bodyIndent))
        {
            return false;
        }

        var lineEnding = FixFormatting.DetectDominantLineEnding(utf8Yaml);
        var permissionsLine = bodyIndent + "permissions: {}" + lineEnding;

        var anchorLine = FindKeyLine(lines, jobLine + 1, jobEndLine, bodyIndent, "runs-on:");
        if (anchorLine < 0)
        {
            anchorLine = FindKeyLine(lines, jobLine + 1, jobEndLine, bodyIndent, "uses:");
        }

        int insertOffset;
        string insertText;

        if (anchorLine >= 0)
        {
            insertOffset = FindLineEndOffsetIncludingNewLine(utf8Yaml, anchorLine);
            if (insertOffset > 0 && insertOffset <= utf8Yaml.Length && utf8Yaml[insertOffset - 1] != (byte)'\n')
            {
                insertText = lineEnding + permissionsLine;
            }
            else
            {
                insertText = permissionsLine;
            }
        }
        else
        {
            var firstSiblingLine = FindFirstMappingSiblingLine(lines, jobLine + 1, jobEndLine, bodyIndent);
            if (firstSiblingLine >= 0)
            {
                insertOffset = FindLineStartOffset(utf8Yaml, firstSiblingLine);
                insertText = permissionsLine;
            }
            else
            {
                insertOffset = FindLineEndOffsetIncludingNewLine(utf8Yaml, jobLine);
                if (insertOffset > 0 && insertOffset <= utf8Yaml.Length && utf8Yaml[insertOffset - 1] != (byte)'\n')
                {
                    insertText = lineEnding + permissionsLine;
                }
                else
                {
                    insertText = permissionsLine;
                }
            }
        }

        fix = new DiagnosticFix(
            "insert explicit job permissions mapping",
            [new TextEdit(insertOffset, 0, insertText)]);
        return true;
    }

    static int FindKeyLine(string[] lines, int startLine, int endLine, string indent, string keyPrefix)
    {
        var maxLine = Math.Min(lines.Length, endLine);
        for (var lineNumber = Math.Max(1, startLine); lineNumber <= maxLine; lineNumber++)
        {
            var line = lines[lineNumber - 1];
            if (!line.StartsWith(indent, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line[indent.Length..].TrimStart();
            if (rest.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                return lineNumber;
            }
        }

        return -1;
    }

    static int FindFirstMappingSiblingLine(string[] lines, int startLine, int endLine, string indent)
    {
        var maxLine = Math.Min(lines.Length, endLine);
        for (var lineNumber = Math.Max(1, startLine); lineNumber <= maxLine; lineNumber++)
        {
            var line = lines[lineNumber - 1];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!line.StartsWith(indent, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line[indent.Length..].TrimStart();
            if (rest.Length == 0 || rest[0] == '#')
            {
                continue;
            }

            return lineNumber;
        }

        return -1;
    }

    static int FindFirstChildLine(string[] lines, int startLine, int endLine, string parentIndent)
    {
        var maxLine = Math.Min(lines.Length, endLine);
        for (var lineNumber = Math.Max(1, startLine); lineNumber <= maxLine; lineNumber++)
        {
            var line = lines[lineNumber - 1];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!line.StartsWith(parentIndent, StringComparison.Ordinal))
            {
                continue;
            }

            var tail = line[parentIndent.Length..];
            if (tail.Length == 0)
            {
                continue;
            }

            if (tail[0] != ' ' && tail[0] != '\t')
            {
                continue;
            }

            var rest = tail.TrimStart();
            if (rest.Length == 0 || rest[0] == '#')
            {
                continue;
            }

            return lineNumber;
        }

        return -1;
    }

    static int FindLineStartOffset(byte[] utf8Yaml, int lineNumber)
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

    static int FindLineEndOffsetIncludingNewLine(byte[] utf8Yaml, int lineNumber)
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
