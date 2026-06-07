using System.Text;
using Seiton.Core.Parsing;

using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting.PinRemediation;

/// <summary>Builds <see cref="DiagnosticFix"/> edits that pin action references to commit SHAs or image digests.</summary>
public static class PinFixFormatter
{
    /// <summary>Builds a <see cref="DiagnosticFix"/> that pins an action reference to the resolved commit SHA.</summary>
    public static DiagnosticFix? BuildActionsShaFix(
        Diagnostic diagnostic,
        string sha40,
        string tagComment,
        byte[] utf8Yaml)
    {
        if (!PinDiagnosticMetadata.TryGetUsesRef(diagnostic, out var usesRef))
        {
            return null;
        }

        var at = usesRef.LastIndexOf('@');
        if (at < 0 || at + 1 >= usesRef.Length)
        {
            return null;
        }

        var currentRef = usesRef[(at + 1)..];
        if (IsFullCommitSha(currentRef))
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

    /// <summary>Builds a <see cref="DiagnosticFix"/> that pins an image reference to the resolved digest.</summary>
    public static DiagnosticFix? BuildImageDigestFix(
        Diagnostic diagnostic,
        string digest,
        byte[] utf8Yaml)
    {
        if (!PinDiagnosticMetadata.TryGetImageRef(diagnostic, out var imageRef))
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

    private static bool TryBuildReplacementFix(
        Diagnostic diagnostic,
        string oldValue,
        string newValue,
        byte[] utf8Yaml,
        string description,
        out DiagnosticFix fix)
    {
        var oldBytes = Encoding.UTF8.GetBytes(oldValue);
        var anchorOffset = Math.Max(0, diagnostic.Location.Start);

        if (TryFindReplacementOffset(utf8Yaml.AsSpan(), oldBytes, anchorOffset, out var offset))
        {
            fix = new DiagnosticFix(description, [new TextEdit(offset, oldBytes.Length, newValue)]);
            return true;
        }

        fix = default;
        return false;
    }

    /// <summary>
    /// Locates <paramref name="oldBytes"/> using the diagnostic anchor (typically the <c>@ref</c> span).
    /// Avoids matching the file's first occurrence when the same action reference appears multiple times.
    /// </summary>
    internal static bool TryFindReplacementOffset(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> oldBytes,
        int anchorOffset,
        out int offset)
    {
        offset = 0;
        if (oldBytes.IsEmpty || source.IsEmpty)
        {
            return false;
        }

        anchorOffset = Math.Clamp(anchorOffset, 0, source.Length);

        // Prefer the occurrence whose byte span contains the diagnostic anchor.
        var windowStart = Math.Max(0, anchorOffset - oldBytes.Length + 1);
        var windowEnd = Math.Min(source.Length, anchorOffset + oldBytes.Length);
        if (windowStart < windowEnd)
        {
            var window = source[windowStart..windowEnd];
            var searchFrom = 0;
            while (searchFrom < window.Length)
            {
                var relative = window[searchFrom..].IndexOf(oldBytes);
                if (relative < 0)
                {
                    break;
                }

                var candidate = windowStart + searchFrom + relative;
                if (candidate <= anchorOffset && anchorOffset < candidate + oldBytes.Length)
                {
                    offset = candidate;
                    return true;
                }

                searchFrom += relative + 1;
            }
        }

        // Fallback when the diagnostic range spans the full replacement value.
        var forward = source[anchorOffset..].IndexOf(oldBytes);
        if (forward >= 0)
        {
            offset = anchorOffset + forward;
            return true;
        }

        return false;
    }
}
