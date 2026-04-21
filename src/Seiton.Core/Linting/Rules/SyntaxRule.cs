using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class SyntaxRule : IRule
{
    private readonly IRule[] rules = RuleCatalog.CreateDefaultRules();

    public string Id => "syntax";

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

    public void VisitWorkflowPre(Workflow workflow)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitWorkflowPre(workflow);
        }
    }

    public void VisitWorkflowPost(Workflow workflow)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitWorkflowPost(workflow);
        }
    }

    public void VisitEvent(Event ev)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitEvent(ev);
        }
    }

    public void VisitJobPre(Job job)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitJobPre(job);
        }
    }

    public void VisitJobPost(Job job)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitJobPost(job);
        }
    }

    public void VisitStep(Step step)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i].VisitStep(step);
        }
    }
}
