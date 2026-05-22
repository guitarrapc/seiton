using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags workflows and jobs that omit explicit human-readable names.</summary>
public sealed class AnonymousDefinitionRule() : RuleBase(RuleId.AnonymousDefinition)
{
    public override string Name => "Anonymous Definition Rule";

    public override bool SupportsDocumentKind(DocumentKind documentKind) => documentKind == DocumentKind.Workflow;

    public override void VisitWorkflowPre(Workflow workflow)
    {
        if (Config.Utf8Yaml is null || workflow.Name.HasValue)
        {
            return;
        }

        AddWorkflowInfo(
            workflow,
            "workflow is missing an explicit name",
            new TextRange(
                Start: workflow.Range.Start,
                Length: 0,
                StartLine: workflow.Range.StartLine,
                StartColumn: workflow.Range.StartColumn,
                EndLine: workflow.Range.StartLine,
                EndColumn: workflow.Range.StartColumn));
    }

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null || job.Name.HasValue)
        {
            return;
        }

        var jobId = Decode(Arena.GetStringSlice(job.Id));
        AddJobInfo(job, $"jobs.'{jobId}' is missing an explicit name", BuildJobLocation(job));
    }
}
