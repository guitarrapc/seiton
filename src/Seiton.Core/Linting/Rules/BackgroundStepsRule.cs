using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates background step references and concurrent background limits in workflow jobs.</summary>
public sealed class BackgroundStepsRule() : RuleBase(RuleId.BackgroundSteps)
{
    private readonly BackgroundStepFlowAnalyzer.State _state = new();

    public override string Name => "Background Steps Rule";

    public override bool SupportsDocumentKind(DocumentKind documentKind) => documentKind == DocumentKind.Workflow;

    public override void VisitJobPre(Job job)
    {
        _state.Registry.Clear();
        _state.ActiveIds.Clear();
        _state.ActiveCount = 0;
        _state.Findings.Clear();
    }

    public override void VisitJobPost(Job job)
    {
        if (job.Steps is null or { Count: 0 } || Config.Utf8Yaml is null)
        {
            return;
        }

        var jobId = Decode(Arena.GetStringSlice(job.Id));
        var jobStructurePrefix = $"jobs.'{jobId}'";
        BackgroundStepFlowAnalyzer.Analyze(job, Arena, Config, jobStructurePrefix, _state);

        for (var i = 0; i < _state.Findings.Count; i++)
        {
            var finding = _state.Findings[i];
            var metadata = StructurePathDiagnosticMetadata.For(finding.StructurePath);
            if (finding.Severity == DiagnosticSeverity.Error)
            {
                AddStepError(finding.Step, finding.Message, finding.Location, metadata);
            }
            else
            {
                AddStepWarning(finding.Step, finding.Message, finding.Location, metadata);
            }
        }
    }
}
