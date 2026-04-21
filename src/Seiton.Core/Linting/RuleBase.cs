using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public abstract class RuleBase : IRule
{
    readonly List<Diagnostic> diagnostics = [];
    protected LintConfig Config { get; private set; } = LintConfig.Empty;

    public abstract string Id { get; }

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

    void AddDiagnostic(DiagnosticSeverity severity, string message, TextRange location, DiagnosticFix? fix = null)
    {
        diagnostics.Add(new Diagnostic(
            severity,
            message,
            location,
            RuleId: Id,
            FilePath: Config.FilePath,
            Fix: fix));
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

    protected static TextRange BuildJobLocation(Job job)
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

    protected static TextRange BuildEventLocation(Event ev)
    {
        var range = ev.EventName.Range;
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

    protected static TextRange BuildUsesLocation(ExecAction action)
    {
        return action.UsesKeyRange ?? action.Uses.Range;
    }

    protected static TextRange BuildUsesLocation(WorkflowCall workflowCall)
    {
        return workflowCall.UsesKeyRange ?? workflowCall.Uses.Range;
    }

    protected static bool HasNodeValue(StringNode? node)
    {
        return node is not null && node.Value.Length > 0;
    }

    protected static bool IsSha256DigestPinned(ReadOnlySpan<byte> image)
    {
        var at = image.LastIndexOf((byte)'@');
        if (at < 0 || at + 1 >= image.Length)
        {
            return false;
        }

        var digest = image[(at + 1)..];
        if (!digest.StartsWith("sha256:"u8))
        {
            return false;
        }

        var hash = digest["sha256:"u8.Length..];
        if (hash.Length != 64)
        {
            return false;
        }

        for (var i = 0; i < hash.Length; i++)
        {
            var b = hash[i];
            var isDigit = b is >= (byte)'0' and <= (byte)'9';
            var isLowerHex = b is >= (byte)'a' and <= (byte)'f';
            var isUpperHex = b is >= (byte)'A' and <= (byte)'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }
}
