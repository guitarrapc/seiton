using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public interface IPass
{
    void VisitWorkflowPre(Workflow workflow);

    void VisitWorkflowPost(Workflow workflow);

    void VisitJobPre(Job job);

    void VisitJobPost(Job job);

    void VisitStep(Step step);
}
