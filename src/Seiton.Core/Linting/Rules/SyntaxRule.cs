using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Surfaces parser diagnostics as lint-level syntax errors (bridges parsing and linting layers).</summary>
public sealed class SyntaxRule : IRule
{
    private readonly IRule[] rules = RuleCatalog.CreateDefaultRules();

    public RuleId Id => RuleId.Syntax;

    public string Name => "Syntax Rule";

    public bool SupportsDocumentKind(DocumentKind documentKind)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            if (rules[i].SupportsDocumentKind(documentKind))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<Diagnostic> GetDiagnostics()
    {
        var diagnostics = new List<Diagnostic>();
        for (var i = 0; i < rules.Length; i++)
        {
            var ruleDiags = rules[i].GetDiagnostics();
            for (var j = 0; j < ruleDiags.Count; j++)
            {
                diagnostics.Add(ruleDiags[j]);
            }
        }

        return diagnostics;
    }

    public void SetConfig(LintConfig config)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].SetConfig(config);
        }
    }

    public void VisitWorkflowPre(WorkflowRef workflow)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitWorkflowPre(workflow);
        }
    }

    public void VisitWorkflowPost(WorkflowRef workflow)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitWorkflowPost(workflow);
        }
    }

    public void VisitActionMetadataPre(ActionMetadataRef metadata)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitActionMetadataPre(metadata);
        }
    }

    public void VisitActionMetadataPost(ActionMetadataRef metadata)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitActionMetadataPost(metadata);
        }
    }

    public void VisitEvent(EventRef ev)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitEvent(ev);
        }
    }

    public void VisitJobPre(JobRef job)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitJobPre(job);
        }
    }

    public void VisitJobPost(JobRef job)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitJobPost(job);
        }
    }

    public void VisitStep(StepRef step)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitStep(step);
        }
    }
}
