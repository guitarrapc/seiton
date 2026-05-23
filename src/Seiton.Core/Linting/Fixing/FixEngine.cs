using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Core.Linting.Fixing;

/// <summary>
/// Applies auto-fix edits from diagnostics to YAML source bytes, then re-lints to validate
/// that fixes did not introduce new errors.
/// </summary>
public static class FixEngine
{
    private enum DiffKind
    {
        Equal,
        Delete,
        Insert,
    }

    /// <summary>Applies fixes from diagnostics, re-lints the result, and validates that no new errors were introduced.</summary>
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
        try
        {
            var updatedUtf8Yaml = Apply(utf8Yaml, selectedDiagnostics);
            var after = lintEngine.Check(updatedUtf8Yaml, filePath, config);
            try
            {
                ValidateRevalidation(before, after, selectedDiagnostics);
                return new RevalidationResult(before, after, updatedUtf8Yaml);
            }
            catch
            {
                after.Dispose();
                throw;
            }
        }
        catch
        {
            before.Dispose();
            throw;
        }
    }

    /// <summary>Builds a unified diff string by applying the given <paramref name="fixes"/> to the YAML.</summary>
    public static string BuildUnifiedDiff(
        byte[] utf8Yaml,
        IEnumerable<DiagnosticFix> fixes,
        string filePath,
        int contextLines = 2)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(fixes);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        if (contextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLines), "contextLines must be non-negative");
        }

        var updatedUtf8Yaml = Apply(utf8Yaml, fixes);
        return BuildUnifiedDiffCore(utf8Yaml, updatedUtf8Yaml, filePath, contextLines);
    }

    /// <summary>Builds a unified diff string by applying fixes from the given diagnostics.</summary>
    public static string BuildUnifiedDiff(
        byte[] utf8Yaml,
        IEnumerable<Diagnostic> diagnosticsWithFix,
        string filePath,
        int contextLines = 2)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentNullException.ThrowIfNull(diagnosticsWithFix);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        if (contextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLines), "contextLines must be non-negative");
        }

        var updatedUtf8Yaml = Apply(utf8Yaml, diagnosticsWithFix);
        return BuildUnifiedDiffCore(utf8Yaml, updatedUtf8Yaml, filePath, contextLines);
    }

    /// <summary>Writes a unified diff to <paramref name="writer"/> by applying the given <paramref name="fixes"/>.</summary>
    public static void WriteUnifiedDiff(
        TextWriter writer,
        byte[] utf8Yaml,
        IEnumerable<DiagnosticFix> fixes,
        string filePath,
        int contextLines = 2)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var diff = BuildUnifiedDiff(utf8Yaml, fixes, filePath, contextLines);
        if (diff.Length == 0)
        {
            return;
        }

        writer.Write(diff);
    }

    /// <summary>Writes a unified diff to <paramref name="writer"/> by applying fixes from the given diagnostics and returns whether any diff was emitted.</summary>
    public static bool TryWriteUnifiedDiff(
        TextWriter writer,
        byte[] utf8Yaml,
        IEnumerable<Diagnostic> diagnosticsWithFix,
        string filePath,
        int contextLines = 2)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var diff = BuildUnifiedDiff(utf8Yaml, diagnosticsWithFix, filePath, contextLines);
        if (diff.Length == 0)
        {
            return false;
        }

        writer.Write(diff);
        return true;
    }

    /// <summary>Builds a unified diff string from pre-computed original and updated YAML bytes.</summary>
    public static string BuildUnifiedDiffFromBytes(
        byte[] originalUtf8Yaml,
        byte[] updatedUtf8Yaml,
        string filePath,
        int contextLines = 2)
    {
        ArgumentNullException.ThrowIfNull(originalUtf8Yaml);
        ArgumentNullException.ThrowIfNull(updatedUtf8Yaml);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        if (contextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLines), "contextLines must be non-negative");
        }

        return BuildUnifiedDiffCore(originalUtf8Yaml, updatedUtf8Yaml, filePath, contextLines);
    }

    /// <summary>Writes a unified diff to <paramref name="writer"/> by applying fixes from the given diagnostics.</summary>
    public static void WriteUnifiedDiff(
        TextWriter writer,
        byte[] utf8Yaml,
        IEnumerable<Diagnostic> diagnosticsWithFix,
        string filePath,
        int contextLines = 2)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var diff = BuildUnifiedDiff(utf8Yaml, diagnosticsWithFix, filePath, contextLines);
        if (diff.Length == 0)
        {
            return;
        }

        writer.Write(diff);
    }

    /// <summary>Applies fixes, re-lints, and optionally verifies that specific rule IDs were cleared.</summary>
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
        try
        {
            var updatedUtf8Yaml = Apply(utf8Yaml, fixes);
            var after = lintEngine.Check(updatedUtf8Yaml, filePath, config);
            try
            {
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
            catch
            {
                after.Dispose();
                throw;
            }
        }
        catch
        {
            before.Dispose();
            throw;
        }
    }

    /// <summary>Applies the given <paramref name="fixes"/> to the YAML and returns the updated bytes.</summary>
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

    /// <summary>Extracts fixes from the given diagnostics, applies them, and returns the updated bytes.</summary>
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

    /// <summary>Applies the given text <paramref name="edits"/> to the YAML bytes and returns the result.</summary>
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
        Array.Sort(orderedEdits, static (left, right) => left.Offset.CompareTo(right.Offset));

        // Pre-compute output size; encode each NewText into a temporary buffer.
        var replacements = new byte[orderedEdits.Length][];
        var outputSize = utf8Yaml.Length;
        for (var i = 0; i < orderedEdits.Length; i++)
        {
            replacements[i] = Encoding.UTF8.GetBytes(orderedEdits[i].NewText);
            outputSize = outputSize - orderedEdits[i].Length + replacements[i].Length;
        }

        var output = new byte[outputSize];
        var srcPos = 0;
        var dstPos = 0;
        for (var i = 0; i < orderedEdits.Length; i++)
        {
            var edit = orderedEdits[i];
            var copyLen = edit.Offset - srcPos;
            utf8Yaml.AsSpan(srcPos, copyLen).CopyTo(output.AsSpan(dstPos));
            dstPos += copyLen;
            srcPos = edit.Offset + edit.Length;

            replacements[i].AsSpan().CopyTo(output.AsSpan(dstPos));
            dstPos += replacements[i].Length;
        }

        utf8Yaml.AsSpan(srcPos).CopyTo(output.AsSpan(dstPos));
        return output;
    }

    private static void ValidateEdits(int sourceLength, IReadOnlyList<TextEdit> edits)
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
                    throw new InvalidOperationException(
                        $"overlapping or conflicting edits detected at offset {edit.Offset} " +
                        $"(previous edit at offset {previousOffset} with length {previousEnd - previousOffset}, " +
                        $"current edit at offset {edit.Offset} with length {edit.Length}; " +
                        $"total {edits.Count} edits in batch)");
                }
            }

            previousOffset = edit.Offset;
            previousEnd = edit.Offset + edit.Length;
            hasPrevious = true;
        }
    }

    private static void ValidateRevalidation(LintResult before, LintResult after, IReadOnlyList<Diagnostic>? selectedDiagnostics)
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

    private static string BuildUnifiedDiffCore(
        byte[] originalUtf8Yaml,
        byte[] updatedUtf8Yaml,
        string filePath,
        int contextLines)
    {
        var originalLines = SplitLines(Encoding.UTF8.GetString(originalUtf8Yaml));
        var updatedLines = SplitLines(Encoding.UTF8.GetString(updatedUtf8Yaml));

        if (AreSameLines(originalLines, updatedLines))
        {
            return string.Empty;
        }

        var ops = BuildDiffOperations(originalLines, updatedLines);
        var hasDiff = false;
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind != DiffKind.Equal)
            {
                hasDiff = true;
                break;
            }
        }

        if (!hasDiff)
        {
            return string.Empty;
        }

        var oldPrefix = new int[ops.Count + 1];
        var newPrefix = new int[ops.Count + 1];
        for (var i = 0; i < ops.Count; i++)
        {
            oldPrefix[i + 1] = oldPrefix[i] + (ops[i].Kind == DiffKind.Insert ? 0 : 1);
            newPrefix[i + 1] = newPrefix[i] + (ops[i].Kind == DiffKind.Delete ? 0 : 1);
        }

        var sb = new StringBuilder();
        sb.Append("--- ");
        sb.AppendLine(filePath);
        sb.Append("+++ ");
        sb.AppendLine(filePath);

        var index = 0;
        while (index < ops.Count)
        {
            while (index < ops.Count && ops[index].Kind == DiffKind.Equal)
            {
                index++;
            }

            if (index >= ops.Count)
            {
                break;
            }

            var firstDiff = index;
            var hunkStart = Math.Max(0, firstDiff - contextLines);
            var lastDiff = firstDiff;
            index = firstDiff + 1;
            while (index < ops.Count)
            {
                if (ops[index].Kind != DiffKind.Equal)
                {
                    lastDiff = index;
                }
                else if (index - lastDiff > contextLines)
                {
                    break;
                }

                index++;
            }

            var hunkEndExclusive = Math.Min(ops.Count, lastDiff + contextLines + 1);
            var oldStart = oldPrefix[hunkStart] + 1;
            var newStart = newPrefix[hunkStart] + 1;
            var oldCount = oldPrefix[hunkEndExclusive] - oldPrefix[hunkStart];
            var newCount = newPrefix[hunkEndExclusive] - newPrefix[hunkStart];

            sb.Append("@@ -");
            sb.Append(oldStart);
            sb.Append(',');
            sb.Append(oldCount);
            sb.Append(" +");
            sb.Append(newStart);
            sb.Append(',');
            sb.Append(newCount);
            sb.AppendLine(" @@");

            for (var i = hunkStart; i < hunkEndExclusive; i++)
            {
                var prefix = ops[i].Kind switch
                {
                    DiffKind.Equal => ' ',
                    DiffKind.Delete => '-',
                    DiffKind.Insert => '+',
                    _ => ' ',
                };

                sb.Append(prefix);
                sb.AppendLine(ops[i].Text);
            }
        }

        return sb.ToString();
    }

    private static bool AreSameLines(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static List<DiffOp> BuildDiffOperations(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var oldCount = oldLines.Count;
        var newCount = newLines.Count;
        var lcs = new int[oldCount + 1, newCount + 1];

        for (var i = oldCount - 1; i >= 0; i--)
        {
            for (var j = newCount - 1; j >= 0; j--)
            {
                if (string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal))
                {
                    lcs[i, j] = lcs[i + 1, j + 1] + 1;
                }
                else
                {
                    lcs[i, j] = Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }
        }

        var ops = new List<DiffOp>(oldCount + newCount);
        var oldIndex = 0;
        var newIndex = 0;
        while (oldIndex < oldCount && newIndex < newCount)
        {
            if (string.Equals(oldLines[oldIndex], newLines[newIndex], StringComparison.Ordinal))
            {
                ops.Add(new DiffOp(DiffKind.Equal, oldLines[oldIndex]));
                oldIndex++;
                newIndex++;
                continue;
            }

            if (lcs[oldIndex + 1, newIndex] >= lcs[oldIndex, newIndex + 1])
            {
                ops.Add(new DiffOp(DiffKind.Delete, oldLines[oldIndex]));
                oldIndex++;
            }
            else
            {
                ops.Add(new DiffOp(DiffKind.Insert, newLines[newIndex]));
                newIndex++;
            }
        }

        while (oldIndex < oldCount)
        {
            ops.Add(new DiffOp(DiffKind.Delete, oldLines[oldIndex]));
            oldIndex++;
        }

        while (newIndex < newCount)
        {
            ops.Add(new DiffOp(DiffKind.Insert, newLines[newIndex]));
            newIndex++;
        }

        return ops;
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var parts = text.Split('\n');
        var count = parts.Length;
        if (count > 0 && parts[count - 1].Length == 0)
        {
            count--;
        }

        if (count == 0)
        {
            return [];
        }

        var lines = new string[count];
        for (var i = 0; i < count; i++)
        {
            var line = parts[i];
            lines[i] = line.EndsWith('\r') ? line[..^1] : line;
        }

        return lines;
    }

    private readonly record struct DiagnosticIdentity(
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

    private readonly record struct DiffOp(DiffKind Kind, string Text);
}

/// <summary>Result of applying auto-fixes and re-linting, containing before/after diagnostics and the patched YAML.</summary>
/// <remarks>
/// This type owns <see cref="Before"/> and <see cref="After"/>. Callers must dispose the instance,
/// typically with <c>using var</c>, to release the underlying arenas and pooled buffers held by those results.
/// </remarks>
public sealed class RevalidationResult : IDisposable
{
    internal RevalidationResult(LintResult before, LintResult after, byte[] updatedUtf8Yaml)
    {
        Before = before;
        After = after;
        UpdatedUtf8Yaml = updatedUtf8Yaml;
    }

    public LintResult Before { get; }

    public LintResult After { get; }

    public byte[] UpdatedUtf8Yaml { get; }

    public void Dispose()
    {
        Before.Dispose();
        After.Dispose();
    }
}
