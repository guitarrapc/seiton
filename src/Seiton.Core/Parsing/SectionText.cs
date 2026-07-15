namespace Seiton.Core.Parsing;

/// <summary>
/// Lazily formatted section name / error message for parser diagnostics.
/// Building "jobs.'x'.steps[3] env must be object"-style strings eagerly on clean parses
/// was a dominant per-step allocation source; this struct carries the pieces and formats
/// only when a diagnostic is actually emitted (via <see cref="ToString"/> at the AddError
/// site). Implicitly convertible from <see cref="string"/> so call sites that already pass
/// literal (allocation-free) messages stay unchanged.
/// </summary>
internal readonly struct SectionText
{
    private readonly string? _prefix;
    private readonly string? _suffix;
    // >= 0 → step mode: formatted as {_prefix}[{_stepIndex}]{_suffix}
    private readonly int _stepIndex;

    public SectionText(string text)
    {
        _prefix = text;
        _suffix = null;
        _stepIndex = -1;
    }

    public SectionText(string stepPathPrefix, int stepIndex, string suffix = "")
    {
        _prefix = stepPathPrefix;
        _suffix = suffix;
        _stepIndex = stepIndex;
    }

    /// <summary>True for <c>default</c> — used as an "unset" sentinel for optional parameters.</summary>
    public bool IsEmpty => _prefix is null;

    public static implicit operator SectionText(string text) => new(text);

    public override string ToString()
    {
        // Check the sentinel first: default(SectionText) has _stepIndex == 0, so testing
        // _stepIndex before _prefix would format the empty sentinel as "[0]".
        if (_prefix is null)
        {
            return string.Empty;
        }

        if (_stepIndex >= 0)
        {
            return $"{_prefix}[{_stepIndex}]{_suffix}";
        }

        return string.IsNullOrEmpty(_suffix) ? _prefix : string.Concat(_prefix, _suffix);
    }
}
