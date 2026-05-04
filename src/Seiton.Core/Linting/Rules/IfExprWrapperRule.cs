using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags <c>if:</c> conditions that are missing the <c>${{ }}</c> expression wrapper and offers an auto-fix.</summary>
public sealed class IfExprWrapperRule() : RuleBase(RuleId.IfExprWrapper)
{
    private const string FixDescription = "wrap in ${{ }}";

    // §9: Diagnostic message deduplication — cache last emitted message for repeated conditions
    private Utf8Slice _lastSlice;
    private string? _lastMessage;
    private string? _lastFixText;

    public override string Name => "If Expression Wrapper Rule";

    public override void VisitJobPre(Job job)
    {
        ValidateCondition(job.If, job, null);
    }

    public override void VisitStep(Step step)
    {
        ValidateCondition(step.If, null, step);
    }

    private void ValidateCondition(StringNodeId condition, Job? job, Step? step)
    {
        if (!condition.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var raw = Arena.GetStringValue(condition);
        if (raw.Length == 0)
        {
            return;
        }

        // Already wrapped in ${{ }} — nothing to do
        if (ExpressionScanHelpers.TryExtractExpressionBody(raw, out _))
        {
            return;
        }

        // Skip bare boolean literals: true, false
        if (raw.SequenceEqual("true"u8) || raw.SequenceEqual("false"u8))
        {
            return;
        }

        // Skip bare status check functions: always(), failure(), cancelled(), success()
        if (IsBareStatusFunction(raw))
        {
            return;
        }

        // This is an expression without ${{ }} wrapper — emit warning with auto-fix
        var slice = Arena.GetStringSlice(condition);
        var range = Arena.GetStringRange(condition);
        var (message, fixText) = GetOrBuildDiagnosticStrings(slice, Config.Utf8Yaml);

        var edit = new TextEdit(slice.Offset, slice.Length, fixText);
        var fix = new DiagnosticFix(FixDescription, [edit]);

        if (job is not null)
        {
            AddJobWarning(job, message, range, fix);
        }

        if (step is not null)
        {
            AddStepWarning(step, message, range, fix);
        }
    }

    private (string Message, string FixText) GetOrBuildDiagnosticStrings(Utf8Slice currentSlice, byte[] utf8Yaml)
    {
        // §9: reuse cached strings when the same condition bytes repeat
        if (_lastMessage is not null
            && currentSlice.Length == _lastSlice.Length
            && utf8Yaml.AsSpan(currentSlice.Offset, currentSlice.Length)
                .SequenceEqual(utf8Yaml.AsSpan(_lastSlice.Offset, _lastSlice.Length)))
        {
            return (_lastMessage, _lastFixText!);
        }

        // Trim trailing whitespace/newlines (block scalars include trailing \n)
        var rawSpan = utf8Yaml.AsSpan(currentSlice.Offset, currentSlice.Length);
        while (rawSpan.Length > 0 && rawSpan[rawSpan.Length - 1] is (byte)'\n' or (byte)'\r' or (byte)' ' or (byte)'\t')
        {
            rawSpan = rawSpan[..^1];
        }

        var rawText = System.Text.Encoding.UTF8.GetString(rawSpan);
        var message = $"if: condition \"{rawText}\" is missing ${{{{ }}}} wrapper; expressions should be wrapped in ${{{{ }}}}";
        var fixText = $"${{{{ {rawText} }}}}";

        _lastSlice = currentSlice;
        _lastMessage = message;
        _lastFixText = fixText;

        return (message, fixText);
    }

    private static bool IsBareStatusFunction(ReadOnlySpan<byte> raw)
    {
        return raw.SequenceEqual("always()"u8)
            || raw.SequenceEqual("failure()"u8)
            || raw.SequenceEqual("cancelled()"u8)
            || raw.SequenceEqual("success()"u8);
    }
}
