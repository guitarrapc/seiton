using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Core.Linting.Fixing;

public static class FixEngine
{
    public static RevalidationResult ApplyAndRelint(
        LintEngine lintEngine,
        byte[] utf8Yaml,
        string filePath,
        IEnumerable<Diagnostic> diagnosticsWithFix,
        LintConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(lintEngine);
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(diagnosticsWithFix);

        var selectedDiagnostics = new List<Diagnostic>();
        foreach (var diagnostic in diagnosticsWithFix)
        {
            if (diagnostic.Fix is null)
            {
                continue;
            }

            selectedDiagnostics.Add(diagnostic);
        }

        var before = lintEngine.Check(utf8Yaml, filePath, config);
        var updatedUtf8Yaml = Apply(utf8Yaml, selectedDiagnostics);
        var after = lintEngine.Check(updatedUtf8Yaml, filePath, config);

        ValidateRevalidation(before, after, selectedDiagnostics);
        return new RevalidationResult(before, after, updatedUtf8Yaml);
    }

    public static RevalidationResult ApplyAndRelint(
        LintEngine lintEngine,
        byte[] utf8Yaml,
        string filePath,
        IEnumerable<DiagnosticFix> fixes,
        IEnumerable<string>? expectedClearedRuleIds = null,
        LintConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(lintEngine);
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(fixes);

        var before = lintEngine.Check(utf8Yaml, filePath, config);
        var updatedUtf8Yaml = Apply(utf8Yaml, fixes);
        var after = lintEngine.Check(updatedUtf8Yaml, filePath, config);

        ValidateRevalidation(before, after, selectedDiagnostics: null);

        if (expectedClearedRuleIds is not null)
        {
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ruleId in expectedClearedRuleIds)
            {
                if (!string.IsNullOrWhiteSpace(ruleId))
                {
                    expected.Add(ruleId);
                }
            }

            for (var i = 0; i < after.Diagnostics.Length; i++)
            {
                var ruleId = after.Diagnostics[i].RuleId;
                if (ruleId is not null && expected.Contains(ruleId))
                {
                    throw new InvalidOperationException($"revalidation failed: expected diagnostics for rule '{ruleId}' to be cleared after fix apply");
                }
            }
        }

        return new RevalidationResult(before, after, updatedUtf8Yaml);
    }

    public static byte[] Apply(byte[] utf8Yaml, IEnumerable<DiagnosticFix> fixes)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(fixes);

        var edits = new List<TextEdit>();
        foreach (var fix in fixes)
        {
            for (var i = 0; i < fix.Edits.Length; i++)
            {
                edits.Add(fix.Edits[i]);
            }
        }

        return Apply(utf8Yaml, edits);
    }

    public static byte[] Apply(byte[] utf8Yaml, IEnumerable<Diagnostic> diagnosticsWithFix)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(diagnosticsWithFix);

        var fixes = new List<DiagnosticFix>();
        foreach (var diagnostic in diagnosticsWithFix)
        {
            if (diagnostic.Fix is null)
            {
                continue;
            }

            fixes.Add(diagnostic.Fix.Value);
        }

        return Apply(utf8Yaml, fixes);
    }

    public static byte[] Apply(byte[] utf8Yaml, IReadOnlyList<TextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(edits);

        if (edits.Count == 0)
        {
            return [.. utf8Yaml];
        }

        ValidateEdits(utf8Yaml.Length, edits);

        var orderedEdits = edits.ToArray();
        Array.Sort(orderedEdits, static (left, right) => right.Offset.CompareTo(left.Offset));

        var result = new List<byte>(utf8Yaml.Length);
        result.AddRange(utf8Yaml);

        for (var i = 0; i < orderedEdits.Length; i++)
        {
            var edit = orderedEdits[i];
            var replacement = Encoding.UTF8.GetBytes(edit.NewText);
            result.RemoveRange(edit.Offset, edit.Length);
            result.InsertRange(edit.Offset, replacement);
        }

        return [.. result];
    }

    static void ValidateEdits(int sourceLength, IReadOnlyList<TextEdit> edits)
    {
        var ordered = edits.ToArray();
        Array.Sort(ordered, static (left, right) => left.Offset.CompareTo(right.Offset));

        var hasPrevious = false;
        var previousOffset = 0;
        var previousEnd = 0;

        for (var i = 0; i < ordered.Length; i++)
        {
            var edit = ordered[i];
            if (edit.Offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(edits), $"edit offset must be non-negative: {edit.Offset}");
            }

            if (edit.Length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(edits), $"edit length must be non-negative: {edit.Length}");
            }

            if (edit.Offset > sourceLength || edit.Offset + edit.Length > sourceLength)
            {
                throw new ArgumentOutOfRangeException(nameof(edits), $"edit ({edit.Offset}, {edit.Length}) exceeds source length {sourceLength}");
            }

            if (hasPrevious)
            {
                if (edit.Offset < previousEnd || edit.Offset == previousOffset)
                {
                    throw new InvalidOperationException($"overlapping or conflicting edits detected at offset {edit.Offset}");
                }
            }

            previousOffset = edit.Offset;
            previousEnd = edit.Offset + edit.Length;
            hasPrevious = true;
        }
    }

    static void ValidateRevalidation(LintResult before, LintResult after, IReadOnlyList<Diagnostic>? selectedDiagnostics)
    {
        if (!before.HasFatalError && after.HasFatalError)
        {
            throw new InvalidOperationException("revalidation failed: fix application introduced fatal YAML parse errors");
        }

        if (selectedDiagnostics is null || selectedDiagnostics.Count == 0)
        {
            return;
        }

        var selectedIdentities = new HashSet<DiagnosticIdentity>();
        for (var i = 0; i < selectedDiagnostics.Count; i++)
        {
            selectedIdentities.Add(new DiagnosticIdentity(selectedDiagnostics[i]));
        }

        for (var i = 0; i < after.Diagnostics.Length; i++)
        {
            if (selectedIdentities.Contains(new DiagnosticIdentity(after.Diagnostics[i])))
            {
                var diagnostic = after.Diagnostics[i];
                throw new InvalidOperationException($"revalidation failed: selected diagnostic '{diagnostic.RuleId ?? "<unknown>"}' at {diagnostic.Location.StartLine}:{diagnostic.Location.StartColumn} still exists after fix apply");
            }
        }
    }

    readonly record struct DiagnosticIdentity(
        DiagnosticSeverity Severity,
        string Message,
        string? RuleId,
        int Start,
        int Length,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn)
    {
        public DiagnosticIdentity(Diagnostic diagnostic)
            : this(
                diagnostic.Severity,
                diagnostic.Message,
                diagnostic.RuleId,
                diagnostic.Location.Start,
                diagnostic.Location.Length,
                diagnostic.Location.StartLine,
                diagnostic.Location.StartColumn,
                diagnostic.Location.EndLine,
                diagnostic.Location.EndColumn)
        {
        }
    }
}

public readonly record struct RevalidationResult(
    LintResult Before,
    LintResult After,
    byte[] UpdatedUtf8Yaml);
