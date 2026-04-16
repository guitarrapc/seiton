using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Core.Linting.Fixing;

public static class FixEngine
{
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
}
