using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Seiton.Output;

/// <summary>
/// Maps 1-based byte columns in a source line to terminal display width for caret alignment.
/// </summary>
internal static class SourceDisplayWidth
{
    internal const int TabWidth = 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetWidthBeforeColumn(ReadOnlySpan<byte> line, int column)
    {
        if (column <= 1 || line.IsEmpty)
        {
            return 0;
        }

        var byteCount = Math.Min(column - 1, line.Length);
        return GetDisplayWidth(line[..byteCount]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetWidthBetweenColumnsInclusive(ReadOnlySpan<byte> line, int startColumn, int endColumnInclusive)
    {
        if (endColumnInclusive < startColumn)
        {
            return 0;
        }

        var startIndex = Math.Max(0, startColumn - 1);
        var endIndex = Math.Min(line.Length, endColumnInclusive);
        if (endIndex <= startIndex)
        {
            return 0;
        }

        return GetDisplayWidth(line[startIndex..endIndex]);
    }

    private static int GetDisplayWidth(ReadOnlySpan<byte> span)
    {
        var width = 0;
        for (var i = 0; i < span.Length;)
        {
            var b = span[i];
            if (b == (byte)'\t')
            {
                width += TabWidth - (width % TabWidth);
                i++;
                continue;
            }

            if (b < 0x80)
            {
                width++;
                i++;
                continue;
            }

            if (Rune.DecodeFromUtf8(span[i..], out var rune, out var consumed) != OperationStatus.Done)
            {
                width++;
                i++;
                continue;
            }

            width += GetRuneWidth(rune);
            i += consumed;
        }

        return width;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetRuneWidth(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark)
        {
            return 0;
        }

        return IsEastAsianWide(rune.Value) ? 2 : 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsEastAsianWide(int codePoint)
    {
        return codePoint switch
        {
            >= 0x1100 and <= 0x115F => true,
            >= 0x2E80 and <= 0xA4CF => true,
            >= 0xAC00 and <= 0xD7A3 => true,
            >= 0xF900 and <= 0xFAFF => true,
            >= 0xFE10 and <= 0xFE19 => true,
            >= 0xFE30 and <= 0xFE6F => true,
            >= 0xFF00 and <= 0xFF60 => true,
            >= 0xFFE0 and <= 0xFFE6 => true,
            _ => false,
        };
    }
}
