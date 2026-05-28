using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags <c>if:</c> conditions that are missing the <c>${{ }}</c> expression wrapper and offers an auto-fix.</summary>
public sealed class IfExprWrapperRule() : RuleBase(RuleId.IfExprWrapper)
{
    private const string FixDescription = "wrap in ${{ }}";

    // §9: Diagnostic message deduplication — cache last emitted message for repeated conditions
    private byte[]? _lastYaml;
    private Utf8Slice _lastSlice;
    private string? _lastMessage;
    private string? _lastFixText;
    private bool _lastContainsMarker;

    public override string Name => "If Expression Wrapper Rule";

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        // Reset cache on new file to avoid stale offset references
        _lastYaml = null;
        _lastMessage = null;
        _lastFixText = null;
        _lastSlice = default;
    }

    public override void VisitJobPre(Job job)
    {
        ValidateCondition(job.If, job, null);

        // snapshot.if
        if (job.Snapshot is { } snapshot)
        {
            ValidateCondition(snapshot.If, job, null);
        }
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

        // Determine if auto-fix is safe
        var containsMarker = ExpressionScanHelpers.ContainsExpressionMarker(raw);
        var canFix = CanOfferAutoFix(raw, containsMarker);

        // This is an expression without ${{ }} wrapper — emit warning with optional auto-fix
        var slice = Arena.GetStringSlice(condition);
        var range = Arena.GetStringRange(condition);
        var (message, fixText) = GetOrBuildDiagnosticStrings(slice, Config.Utf8Yaml, containsMarker);

        DiagnosticFix? fix = null;
        if (canFix)
        {
            var edit = BuildFixEdit(condition, slice, fixText);
            fix = new DiagnosticFix(FixDescription, [edit]);
        }

        if (job is not null)
        {
            if (fix is { } f)
            {
                AddJobWarning(job, message, range, f);
            }
            else
            {
                AddJobWarning(job, message, range);
            }
        }

        if (step is not null)
        {
            if (fix is { } f)
            {
                AddStepWarning(step, message, range, f);
            }
            else
            {
                AddStepWarning(step, message, range);
            }
        }
    }

    /// <summary>Determines whether auto-fix is safe for this condition.</summary>
    private static bool CanOfferAutoFix(ReadOnlySpan<byte> raw, bool containsMarker)
    {
        // Block scalars (trailing newline) — fix would break YAML structure
        if (raw.Length > 0 && raw[raw.Length - 1] is (byte)'\n' or (byte)'\r')
        {
            return false;
        }

        // Multi-line source content (internal newline) — block scalar, fix would break structure
        if (raw.IndexOfAny((byte)'\n', (byte)'\r') >= 0)
        {
            return false;
        }

        // Contains ${{ marker but isn't a clean wrapper — fix would nest markers
        if (containsMarker)
        {
            return false;
        }

        return true;
    }

    /// <summary>Builds the TextEdit, expanding range to include surrounding quotes if needed.</summary>
    private TextEdit BuildFixEdit(StringNodeId condition, Utf8Slice slice, string fixText)
    {
        var offset = slice.Offset;
        var length = slice.Length;

        // If the node is quoted, expand the range to include surrounding quotes
        if (Arena.GetStringQuoted(condition) && Config.Utf8Yaml is not null)
        {
            var before = offset - 1;
            var after = offset + length;
            if (before >= 0 && after < Config.Utf8Yaml.Length)
            {
                var bc = Config.Utf8Yaml[before];
                var ac = Config.Utf8Yaml[after];
                if ((bc == (byte)'\'' && ac == (byte)'\'') || (bc == (byte)'"' && ac == (byte)'"'))
                {
                    offset = before;
                    length += 2;
                }
            }
        }

        return new TextEdit(offset, length, fixText);
    }

    private (string Message, string FixText) GetOrBuildDiagnosticStrings(Utf8Slice currentSlice, byte[] utf8Yaml, bool containsMarker)
    {
        // §9: reuse cached strings when the same condition bytes repeat (same file only)
        if (_lastMessage is not null
            && _lastContainsMarker == containsMarker
            && ReferenceEquals(_lastYaml, utf8Yaml)
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

        // Collapse internal newline+whitespace runs to single space for readable diagnostics
        if (rawText.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            rawText = CollapseInternalWhitespace(rawText);
        }

        var message = containsMarker
            ? $"if: condition \"{rawText}\" is not properly wrapped in ${{{{ }}}}; use a single ${{{{ expression }}}}"
            : $"if: condition \"{rawText}\" is missing ${{{{ }}}} wrapper; expressions should be wrapped in ${{{{ }}}}";
        var fixText = $"${{{{ {rawText} }}}}";

        _lastYaml = utf8Yaml;
        _lastSlice = currentSlice;
        _lastMessage = message;
        _lastFixText = fixText;
        _lastContainsMarker = containsMarker;

        return (message, fixText);
    }

    private static bool IsBareStatusFunction(ReadOnlySpan<byte> raw)
    {
        return raw.SequenceEqual("always()"u8)
            || raw.SequenceEqual("failure()"u8)
            || raw.SequenceEqual("cancelled()"u8)
            || raw.SequenceEqual("success()"u8);
    }

    /// <summary>Collapses sequences containing at least one newline into a single space.</summary>
    private static string CollapseInternalWhitespace(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '\r' || ch == '\n')
            {
                // Trim trailing whitespace before the newline
                while (sb.Length > 0 && sb[sb.Length - 1] is ' ' or '\t')
                {
                    sb.Length--;
                }

                i++;
                // Skip remaining whitespace in the run
                while (i < text.Length && text[i] is '\r' or '\n' or ' ' or '\t')
                {
                    i++;
                }

                // Insert single space separator (not at start or end)
                if (sb.Length > 0 && i < text.Length)
                {
                    sb.Append(' ');
                }
            }
            else
            {
                sb.Append(ch);
                i++;
            }
        }

        return sb.ToString();
    }
}
