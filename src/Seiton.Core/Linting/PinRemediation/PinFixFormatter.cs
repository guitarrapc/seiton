using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.PinRemediation;

public static class PinFixFormatter
{
    public static DiagnosticFix? BuildActionsShaFix(
        Diagnostic diagnostic,
        string sha40,
        string tagComment,
        byte[] utf8Yaml)
    {
        if (!TryExtractQuotedValue(diagnostic.Message, out var usesRef))
        {
            return null;
        }

        var at = usesRef.LastIndexOf('@');
        if (at < 0 || at + 1 >= usesRef.Length)
        {
            return null;
        }

        var currentRef = usesRef[(at + 1)..];
        if (IsFullLengthCommitSha(currentRef))
        {
            return null;
        }

        var replacement = usesRef[..(at + 1)] + sha40 + " # " + tagComment;
        return TryBuildReplacementFix(
            diagnostic,
            usesRef,
            replacement,
            utf8Yaml,
            "Pin action reference to resolved SHA",
            out var fix)
            ? fix
            : null;
    }

    public static DiagnosticFix? BuildImageDigestFix(
        Diagnostic diagnostic,
        string digest,
        byte[] utf8Yaml)
    {
        if (!TryExtractQuotedValue(diagnostic.Message, out var imageRef))
        {
            return null;
        }

        if (imageRef.Contains("@sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var replacement = imageRef + "@" + digest;
        return TryBuildReplacementFix(
            diagnostic,
            imageRef,
            replacement,
            utf8Yaml,
            "Pin image reference to resolved digest",
            out var fix)
            ? fix
            : null;
    }

    static bool TryExtractQuotedValue(string message, out string value)
    {
        var first = message.IndexOf('\'');
        if (first < 0)
        {
            value = string.Empty;
            return false;
        }

        var second = message.IndexOf('\'', first + 1);
        if (second <= first)
        {
            value = string.Empty;
            return false;
        }

        value = message[(first + 1)..second];
        return !string.IsNullOrEmpty(value);
    }

    static bool IsFullLengthCommitSha(string value)
    {
        if (value.Length != 40)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isDigit = c is >= '0' and <= '9';
            var isLowerHex = c is >= 'a' and <= 'f';
            var isUpperHex = c is >= 'A' and <= 'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }

    static bool TryBuildReplacementFix(
        Diagnostic diagnostic,
        string oldValue,
        string newValue,
        byte[] utf8Yaml,
        string description,
        out DiagnosticFix fix)
    {
        var oldBytes = Encoding.UTF8.GetBytes(oldValue);

        var rangeStart = Math.Max(0, diagnostic.Location.Start);
        var rangeLength = Math.Max(0, diagnostic.Location.Length);
        var rangeEnd = Math.Min(utf8Yaml.Length, rangeStart + rangeLength);

        if (rangeStart <= rangeEnd)
        {
            var segment = utf8Yaml.AsSpan(rangeStart, rangeEnd - rangeStart);
            var local = segment.IndexOf(oldBytes);
            if (local >= 0)
            {
                var offset = rangeStart + local;
                fix = new DiagnosticFix(description, [new TextEdit(offset, oldBytes.Length, newValue)]);
                return true;
            }
        }

        var global = utf8Yaml.AsSpan().IndexOf(oldBytes);
        if (global >= 0)
        {
            fix = new DiagnosticFix(description, [new TextEdit(global, oldBytes.Length, newValue)]);
            return true;
        }

        fix = default;
        return false;
    }
}
