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
