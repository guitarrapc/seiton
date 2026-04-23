using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public abstract class RuleBase : IRule
{
    private readonly List<Diagnostic> diagnostics = [];
    protected LintConfig Config { get; private set; } = LintConfig.Empty;

    public RuleId Id { get; }

    protected RuleBase(RuleId id) => Id = id;

    public abstract string Name { get; }

    public virtual bool SupportsDocumentKind(DocumentKind documentKind)
    {
        return documentKind == DocumentKind.Workflow || documentKind == DocumentKind.ActionMetadata;
    }

    public IReadOnlyList<Diagnostic> GetDiagnostics() => diagnostics;

    public virtual void SetConfig(LintConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Config = config;
    }

    public virtual void VisitWorkflowPre(Workflow workflow)
    {
        diagnostics.Clear();
    }

    public virtual void VisitWorkflowPost(Workflow workflow)
    {
    }

    public virtual void VisitActionMetadataPre(ActionMetadata metadata)
    {
        diagnostics.Clear();
    }

    public virtual void VisitActionMetadataPost(ActionMetadata metadata)
    {
    }

    public virtual void VisitEvent(Event ev)
    {
    }

    public virtual void VisitJobPre(Job job)
    {
    }

    public virtual void VisitJobPost(Job job)
    {
    }

    public virtual void VisitStep(Step step)
    {
    }

    protected void AddError(string message, TextRange location)
    {
        AddDiagnostic(DiagnosticSeverity.Error, message, location);
    }

    protected void AddWarning(string message, TextRange location)
    {
        AddDiagnostic(DiagnosticSeverity.Warning, message, location);
    }

    protected void AddJobError(Job job, string message)
    {
        AddDiagnostic(DiagnosticSeverity.Error, message, BuildJobLocation(job));
    }

    protected void AddStepWarning(Step step, string message)
    {
        AddDiagnostic(DiagnosticSeverity.Warning, message, BuildStepLocation(step));
    }

    protected void AddStepWarning(Step step, string message, TextRange location)
    {
        AddDiagnostic(DiagnosticSeverity.Warning, message, location);
    }

    protected void AddStepWarning(Step step, string message, TextRange location, DiagnosticFix fix)
    {
        AddDiagnostic(DiagnosticSeverity.Warning, message, location, fix);
    }

    protected void AddStepWarning(Step step, string message, TextRange location, IReadOnlyDictionary<string, string> metadata)
    {
        AddDiagnostic(DiagnosticSeverity.Warning, message, location, fix: null, metadata);
    }

    protected void AddStepInfo(Step step, string message, TextRange location)
    {
        AddDiagnostic(DiagnosticSeverity.Info, message, location);
    }

    protected void AddStepInfo(Step step, string message, TextRange location, DiagnosticFix fix)
    {
        AddDiagnostic(DiagnosticSeverity.Info, message, location, fix);
    }

    protected void AddStepError(Step step, string message, TextRange location)
    {
        AddDiagnostic(DiagnosticSeverity.Error, message, location);
    }

    protected void AddStepError(Step step, string message, TextRange location, DiagnosticFix fix)
    {
        AddDiagnostic(DiagnosticSeverity.Error, message, location, fix);
    }

    protected void AddJobWarning(Job job, string message)
    {
        AddDiagnostic(DiagnosticSeverity.Warning, message, BuildJobLocation(job));
    }

    protected void AddJobWarning(Job job, string message, TextRange location)
    {
        AddDiagnostic(DiagnosticSeverity.Warning, message, location);
    }

    protected void AddJobWarning(Job job, string message, TextRange location, DiagnosticFix fix)
    {
        AddDiagnostic(DiagnosticSeverity.Warning, message, location, fix);
    }

    protected void AddJobWarning(Job job, string message, TextRange location, IReadOnlyDictionary<string, string> metadata)
    {
        AddDiagnostic(DiagnosticSeverity.Warning, message, location, fix: null, metadata);
    }

    protected void AddEventWarning(Event ev, string message)
    {
        AddDiagnostic(DiagnosticSeverity.Warning, message, BuildEventLocation(ev));
    }

    protected void AddEventError(Event ev, string message, TextRange location)
    {
        AddDiagnostic(DiagnosticSeverity.Error, message, location);
    }

    protected void AddWorkflowError(Workflow workflow, string message, TextRange location)
    {
        AddDiagnostic(DiagnosticSeverity.Error, message, location);
    }

    protected void AddWorkflowWarning(Workflow workflow, string message, TextRange location)
    {
        AddDiagnostic(DiagnosticSeverity.Warning, message, location);
    }

    protected void AddWorkflowError(Workflow workflow, string message, TextRange location, DiagnosticFix fix)
    {
        AddDiagnostic(DiagnosticSeverity.Error, message, location, fix);
    }

    protected void AddJobError(Job job, string message, TextRange location)
    {
        AddDiagnostic(DiagnosticSeverity.Error, message, location);
    }

    protected void AddJobError(Job job, string message, TextRange location, DiagnosticFix fix)
    {
        AddDiagnostic(DiagnosticSeverity.Error, message, location, fix);
    }

    private void AddDiagnostic(
        DiagnosticSeverity severity,
        string message,
        TextRange location,
        DiagnosticFix? fix = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        diagnostics.Add(new Diagnostic(
            severity,
            message,
            location,
            RuleId: Id.ToId(),
            FilePath: Config.FilePath,
            Fix: fix,
            Metadata: metadata));
    }

    protected string Decode(Utf8Slice slice)
    {
        if (Config.Utf8Yaml is null || slice.Length <= 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(slice.AsSpan(Config.Utf8Yaml));
    }

    protected static string Decode(Utf8String value)
    {
        return value.Length == 0 ? string.Empty : Encoding.UTF8.GetString(value.Span);
    }

    protected TextRange BuildJobLocation(Job job)
    {
        var range = Arena.GetStringRange(job.Id);
        return new TextRange(
            Start: range.Start,
            Length: 0,
            StartLine: range.StartLine,
            StartColumn: range.StartColumn,
            EndLine: range.StartLine,
            EndColumn: range.StartColumn);
    }

    protected TextRange BuildEventLocation(Event ev)
    {
        var range = Arena.GetStringRange(ev.EventName);
        return new TextRange(
            Start: range.Start,
            Length: 0,
            StartLine: range.StartLine,
            StartColumn: range.StartColumn,
            EndLine: range.StartLine,
            EndColumn: range.StartColumn);
    }

    protected static TextRange BuildStepLocation(Step step)
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

    protected AstArena Arena => Config.Arena!;

    protected TextRange BuildUsesLocation(ExecAction action)
    {
        return action.UsesKeyRange ?? Arena.GetStringRange(action.Uses);
    }

    protected TextRange BuildUsesLocation(WorkflowCall workflowCall)
    {
        return workflowCall.UsesKeyRange ?? Arena.GetStringRange(workflowCall.Uses);
    }

    protected static bool HasNodeValue(StringNodeId node, AstArena arena)
    {
        return node.HasValue && arena.GetStringSlice(node).Length > 0;
    }
}
