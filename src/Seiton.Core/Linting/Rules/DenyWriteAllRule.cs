using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using Seiton.Core.Linting.Fixing;

namespace Seiton.Core.Linting.Rules;

public sealed class DenyWriteAllRule : RuleBase
{
    public override string Id => "deny-write-all";

    public override string Name => "Deny Write-All Rule";

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        ValidatePermissionsAll(
            workflow.Permissions,
            (message, location, fix) =>
            {
                if (fix is null)
                {
                    AddWorkflowError(workflow, message, location);
                    return;
                }

                AddWorkflowError(workflow, message, location, fix.Value);
            });
    }

    public override void VisitJobPre(Job job)
    {
        ValidatePermissionsAll(
            job.Permissions,
            (message, location, fix) =>
            {
                if (fix is null)
                {
                    AddJobError(job, message, location);
                    return;
                }

                AddJobError(job, message, location, fix.Value);
            });
    }

    void ValidatePermissionsAll(Permissions? permissions, Action<string, TextRange, DiagnosticFix?> report)
    {
        if (Config.Utf8Yaml is null || permissions?.All is null)
        {
            return;
        }

        var allNode = permissions.All;
        var value = allNode.Value.AsSpan(Config.Utf8Yaml);
        if (allNode.Expression is not null || value.IndexOf("${{"u8) >= 0)
        {
            return;
        }

        if (!value.SequenceEqual("write-all"u8))
        {
            return;
        }

        var replacement = BuildReplacementText(allNode, Config.Utf8Yaml);
        var fix = new DiagnosticFix(
            "replace write-all with read-all",
            [new TextEdit(allNode.Value.Offset, allNode.Value.Length, replacement)]);
        report("permissions scalar 'write-all' is forbidden; use least-privilege scopes or 'read-all'", allNode.Range, fix);
    }

    static string BuildReplacementText(StringNode allNode, byte[] utf8Yaml)
    {
        var valueStart = allNode.Value.Offset;
        var valueEnd = allNode.Value.Offset + allNode.Value.Length;
        if (valueStart < 0 || valueEnd > utf8Yaml.Length || valueStart > valueEnd)
        {
            return "read-all";
        }

        var valueSpan = allNode.Value.AsSpan(utf8Yaml);
        if (allNode.Quoted)
        {
            if (valueSpan.Length >= 2 && valueSpan[0] == (byte)'\'' && valueSpan[^1] == (byte)'\'')
            {
                return "'read-all'";
            }

            if (valueSpan.Length >= 2 && valueSpan[0] == (byte)'"' && valueSpan[^1] == (byte)'"')
            {
                return "\"read-all\"";
            }
        }

        var style = FixFormatting.DetectQuoteStyle(utf8Yaml, allNode.Range, allNode.Quoted);
        if (style == ScalarQuoteStyle.Unquoted)
        {
            return "read-all";
        }

        var quoteChar = style == ScalarQuoteStyle.SingleQuoted ? (byte)'\'' : (byte)'"';
        var start = valueStart;
        var end = valueEnd;

        // Most parser ranges point to scalar value bytes (without quote chars).
        if (start > 0 && end < utf8Yaml.Length && utf8Yaml[start - 1] == quoteChar && utf8Yaml[end] == quoteChar)
        {
            return "read-all";
        }

        if (start >= 0 && end - 1 >= start && end - 1 < utf8Yaml.Length && utf8Yaml[start] == quoteChar && utf8Yaml[end - 1] == quoteChar)
        {
            return style == ScalarQuoteStyle.SingleQuoted ? "'read-all'" : "\"read-all\"";
        }

        return "read-all";
    }
}
