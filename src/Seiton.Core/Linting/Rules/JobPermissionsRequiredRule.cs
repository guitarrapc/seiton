using Seiton.Core.Parsing.Ast;
using Seiton.Core.Parsing;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Generated;

namespace Seiton.Core.Linting.Rules;

/// <summary>Requires each job to declare explicit <c>permissions:</c> for least-privilege enforcement.</summary>
public sealed class JobPermissionsRequiredRule() : RuleBase(RuleId.JobPermissionsRequired)
{
    public override string Name => "Job Permissions Required Rule";

    public override void VisitJobPre(JobRef job)
    {
        if (job.Permissions.HasValue)
        {
            return;
        }

        // Decode the job id into a stack buffer so the diagnostic costs a single string
        // (the message itself) instead of message + intermediate job-id string.
        Span<char> jobIdBuffer = stackalloc char[128];
        var jobId = DecodeChars(job.Id.Slice, jobIdBuffer);
        var message = $"jobs.'{jobId}' does not have permissions defined; set explicit permissions to follow least-privilege principle";
        if (Config.Fix.Enabled && Config.Utf8Yaml is not null && TryBuildPermissionsInsertFix(job, Config.Utf8Yaml, out var fix))
        {
            AddJobWarning(job, message, BuildJobLocation(job), fix);
            return;
        }

        AddJobWarning(job, message);
    }

    private bool TryBuildPermissionsInsertFix(JobRef job, byte[] utf8Yaml, out DiagnosticFix fix)
    {
        fix = default;

        if (utf8Yaml.Length == 0)
        {
            return false;
        }

        var jobLine = job.Id.Range.StartLine;
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
        var permissionsText = BuildPermissionsText(job, parentIndent, bodyIndent, lineEnding);

        var anchorLine = Utf8YamlLineHelpers.FindLineWithKey(utf8Yaml, jobLine + 1, jobEndLine, bodyIndent, "runs-on:"u8);
        if (anchorLine < 0)
        {
            anchorLine = Utf8YamlLineHelpers.FindLineWithKey(utf8Yaml, jobLine + 1, jobEndLine, bodyIndent, "uses:"u8);
        }

        int insertOffset;
        string insertText;

        if (anchorLine >= 0)
        {
            insertOffset = Utf8YamlLineHelpers.FindLineEndOffsetIncludingNewLine(utf8Yaml, anchorLine);
            if (insertOffset > 0 && insertOffset <= utf8Yaml.Length && utf8Yaml[insertOffset - 1] != (byte)'\n')
            {
                insertText = lineEnding + permissionsText;
            }
            else
            {
                insertText = permissionsText;
            }
        }
        else
        {
            var firstSiblingLine = FindFirstMappingSiblingLine(utf8Yaml, jobLine + 1, jobEndLine, bodyIndent);
            if (firstSiblingLine >= 0)
            {
                insertOffset = Utf8YamlLineHelpers.FindLineStartOffset(utf8Yaml, firstSiblingLine);
                insertText = permissionsText;
            }
            else
            {
                insertOffset = Utf8YamlLineHelpers.FindLineEndOffsetIncludingNewLine(utf8Yaml, jobLine);
                if (insertOffset > 0 && insertOffset <= utf8Yaml.Length && utf8Yaml[insertOffset - 1] != (byte)'\n')
                {
                    insertText = lineEnding + permissionsText;
                }
                else
                {
                    insertText = permissionsText;
                }
            }
        }

        fix = new DiagnosticFix(
            "insert explicit job permissions mapping",
            [new TextEdit(insertOffset, 0, insertText)]);
        return true;
    }

    private string BuildPermissionsText(JobRef job, string parentIndent, string bodyIndent, string lineEnding)
    {
        var merged = CollectRequiredPermissions(job);
        if (merged.Count == 0)
        {
            return bodyIndent + "permissions: {}" + lineEnding;
        }

        // Infer child indent from parentIndent→bodyIndent relationship (add one level)
        var indentUnit = InferIndentUnit(parentIndent, bodyIndent);
        var childIndent = bodyIndent + indentUnit;

        var sb = new System.Text.StringBuilder();
        sb.Append(bodyIndent);
        sb.Append("permissions:");
        sb.Append(lineEnding);
        foreach (var (scope, access) in merged.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(childIndent);
            sb.Append(scope);
            sb.Append(": ");
            sb.Append(access);
            sb.Append(lineEnding);
        }

        return sb.ToString();
    }

    private Dictionary<string, string> CollectRequiredPermissions(JobRef job)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!job.Steps.HasValue)
        {
            return merged;
        }

        foreach (var step in job.Steps)
        {
            if (step.Exec.Kind != StepExecKind.Action)
            {
                continue;
            }

            var action = step.Exec.AsAction();
            var usesValue = action.Uses.Value;
            if (usesValue.IsEmpty)
            {
                continue;
            }

            if (!PopularActions.TryGet(usesValue, out var spec))
            {
                continue;
            }

            var perms = spec.GetRequiredPermissions();
            for (var i = 0; i < perms.Length; i++)
            {
                var (scope, access) = perms[i];
                if (merged.TryGetValue(scope, out var existing))
                {
                    if (AccessLevel(access) > AccessLevel(existing))
                    {
                        merged[scope] = access;
                    }
                }
                else
                {
                    merged[scope] = access;
                }
            }
        }

        return merged;
    }

    private static int AccessLevel(string access)
    {
        return access switch
        {
            "none" => 0,
            "read" => 1,
            "write" => 2,
            _ => -1,
        };
    }

    private static string InferIndentUnit(string parentIndent, string bodyIndent)
    {
        // bodyIndent = parentIndent + indentUnit. Derive the unit from the difference.
        if (bodyIndent.Length == 0)
        {
            return "  ";
        }

        if (bodyIndent[0] == '\t')
        {
            if (parentIndent.Length < bodyIndent.Length
                && bodyIndent.StartsWith(parentIndent, StringComparison.Ordinal))
            {
                var tabUnit = bodyIndent[parentIndent.Length..];
                if (tabUnit.Length > 0 && tabUnit.AsSpan().IndexOfAnyExcept('\t') < 0)
                {
                    return tabUnit;
                }
            }

            return "\t";
        }

        if (parentIndent.Length < bodyIndent.Length
            && bodyIndent.StartsWith(parentIndent, StringComparison.Ordinal))
        {
            var spaceUnit = bodyIndent[parentIndent.Length..];
            if (spaceUnit.Length > 0 && spaceUnit.AsSpan().IndexOfAnyExcept(' ') < 0)
            {
                return spaceUnit;
            }
        }

        // Fallback: safer common YAML default
        return "  ";
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

    // True if the line starts with indent, followed immediately by a deeper indentation char (space/tab),
    // and then contains a non-whitespace, non-comment character.
    private static bool IsChildLine(byte[] utf8Yaml, int lineStart, int lineEnd, string parentIndent)
    {
        var lineLen = lineEnd - lineStart;
        if (lineLen == 0) return false;
        // check all-whitespace
        var firstNonWs = lineStart;
        while (firstNonWs < lineEnd && (utf8Yaml[firstNonWs] == (byte)' ' || utf8Yaml[firstNonWs] == (byte)'\t')) firstNonWs++;
        if (firstNonWs >= lineEnd) return false;
        // startsWith parentIndent
        if (lineLen < parentIndent.Length) return false;
        for (var k = 0; k < parentIndent.Length; k++)
            if (utf8Yaml[lineStart + k] != (byte)parentIndent[k]) return false;
        var tailStart = lineStart + parentIndent.Length;
        if (tailStart >= lineEnd) return false;
        var tailByte = utf8Yaml[tailStart];
        if (tailByte != (byte)' ' && tailByte != (byte)'\t') return false;
        // find first non-ws in tail
        var restStart = tailStart;
        while (restStart < lineEnd && (utf8Yaml[restStart] == (byte)' ' || utf8Yaml[restStart] == (byte)'\t')) restStart++;
        return restStart < lineEnd && utf8Yaml[restStart] != (byte)'#';
    }

    // True if line starts with indent (not deeper), not whitespace-only, not a comment.
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

}
