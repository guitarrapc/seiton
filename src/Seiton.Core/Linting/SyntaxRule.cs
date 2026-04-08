using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public sealed class SyntaxRule : IRule
{
    List<Diagnostic> diagnostics = [];
    LintConfig config = LintConfig.Empty;

    public string Id => "syntax";

    public string Name => "Syntax Rule";

    public Diagnostic[] GetDiagnostics() => diagnostics.ToArray();

    public void SetConfig(LintConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        this.config = config;
    }

    public void VisitWorkflowPre(Workflow workflow)
    {
        diagnostics.Clear();
    }

    public void VisitWorkflowPost(Workflow workflow)
    {
    }

    public void VisitJobPre(Job job)
    {
        var hasUses = job.WorkflowCall is not null;
        var hasRunsOn = job.RunsOn is not null;
        var hasSteps = job.Steps is not null;
        var jobId = Decode(job.Id.Value);

        if (hasUses && hasSteps)
        {
            AddJobError(job, $"job '{jobId}' cannot have both uses and steps");
        }

        if (hasUses && hasRunsOn)
        {
            AddJobError(job, $"job '{jobId}' cannot have both uses and runs-on");
        }

        if (!hasUses && !hasRunsOn)
        {
            AddJobError(job, $"job '{jobId}' requires runs-on (or uses)");
        }

        if (!hasUses && !hasSteps)
        {
            AddJobError(job, $"job '{jobId}' requires steps (or uses)");
        }
    }

    public void VisitJobPost(Job job)
    {
    }

    public void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec || actionExec.Inputs is null || actionExec.Inputs.Count == 0)
        {
            return;
        }

        var usesText = actionExec.Uses.Value.AsSpan(config.Utf8Yaml);
        if (!PopularActions.TryGet(usesText, out var actionSpec))
        {
            return;
        }

        var actionName = Decode(actionExec.Uses.Value);
        foreach (var pair in actionExec.Inputs)
        {
            if (actionSpec.IsInputAllowed(pair.Key.Span))
            {
                continue;
            }

            var inputName = Encoding.UTF8.GetString(pair.Key.Span);
            AddStepWarning(step, $"unknown input '{inputName}' for action '{actionName}'");
        }
    }

    void AddJobError(Job job, string message)
    {
        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, BuildJobLocation(job)));
    }

    TextRange BuildJobLocation(Job job)
    {
        var range = job.Id.Range;
        return new TextRange(
            Start: range.Start,
            Length: 0,
            StartLine: range.StartLine,
            StartColumn: range.StartColumn,
            EndLine: range.StartLine,
            EndColumn: range.StartColumn);
    }

    void AddStepWarning(Step step, string message)
    {
        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, message, BuildStepLocation(step)));
    }

    TextRange BuildStepLocation(Step step)
    {
        var range = step.Range;
        return new TextRange(
            Start: range.Start,
            Length: 0,
            StartLine: range.StartLine,
            StartColumn: range.StartColumn,
            EndLine: range.StartLine,
            EndColumn: range.StartColumn);
    }

    string Decode(Utf8Slice slice)
    {
        if (config.Utf8Yaml is null)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(slice.AsSpan(config.Utf8Yaml));
    }
}
