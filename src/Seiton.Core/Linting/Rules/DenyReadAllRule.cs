using Seiton.Core.Parsing.Ast;
using Seiton.Core.Parsing;
using Seiton.Core.Linting.Fixing;

namespace Seiton.Core.Linting.Rules;

public sealed class DenyReadAllRule : RuleBase
{
    public override string Id => "deny-read-all";

    public override string Name => "Deny Read-All Rule";

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

        if (!value.SequenceEqual("read-all"u8))
        {
            return;
        }

        var edit = BuildExplicitMappingReplacementEdit(allNode, Config.Utf8Yaml);
        var fix = new DiagnosticFix(
            "replace read-all with explicit permissions mapping baseline",
            [edit]);

        report("permissions scalar 'read-all' is forbidden; use explicit least-privilege scopes", allNode.Range, fix);
    }

    static TextEdit BuildExplicitMappingReplacementEdit(StringNode allNode, byte[] utf8Yaml)
    {
        var start = allNode.Value.Offset;
        var end = allNode.Value.Offset + allNode.Value.Length;
        if (start < 0 || end > utf8Yaml.Length || start > end)
        {
            return new TextEdit(allNode.Value.Offset, allNode.Value.Length, "{}");
        }

        if (start > 0 && end < utf8Yaml.Length)
        {
            var before = utf8Yaml[start - 1];
            var after = utf8Yaml[end];
            if ((before == (byte)'\'' && after == (byte)'\'') || (before == (byte)'"' && after == (byte)'"'))
            {
                start--;
                end++;
            }
        }

        if (start < end && end - 1 < utf8Yaml.Length)
        {
            var first = utf8Yaml[start];
            var last = utf8Yaml[end - 1];
            if ((first == (byte)'\'' && last == (byte)'\'') || (first == (byte)'"' && last == (byte)'"'))
            {
                // Range already includes quote chars.
            }
        }

        return new TextEdit(start, end - start, "{}");
    }
}
