using System.Buffers;
using System.Globalization;
using System.Text;

namespace Seiton.Core.Linting;

/// <summary>Unified remote <c>uses:</c> parsing (<c>owner/repo[@path]@ref</c>) and policy wildcard matching.</summary>
internal readonly ref struct ParsedActionRef
{
    private readonly ReadOnlySpan<byte> _uses;
    private readonly int _at;

    internal ParsedActionRef(ReadOnlySpan<byte> uses, int at)
    {
        _uses = uses;
        _at = at;
    }

    public ReadOnlySpan<byte> ActionPath => _uses[.._at];

    public ReadOnlySpan<byte> Ref => _uses[(_at + 1)..];
}

internal static class ActionRefHelpers
{
    /// <summary>
    /// Validates GitHub remote <c>uses</c> shape (not <c>./</c>, <c>docker://</c>) and splits <c>actionPath</c> / <c>ref</c> at the last <c>@</c>.
    /// </summary>
    internal static bool TryParseRemoteUses(ReadOnlySpan<byte> uses, out ParsedActionRef parsed)
    {
        parsed = default;
        if (uses.IsEmpty || uses.StartsWith("./"u8) || uses.StartsWith("../"u8) || uses.StartsWith("docker://"u8))
        {
            return false;
        }

        var at = uses.LastIndexOf((byte)'@');
        if (at <= 0 || at + 1 >= uses.Length)
        {
            return false;
        }

        var left = uses[..at];
        var firstSlash = left.IndexOf((byte)'/');
        if (firstSlash <= 0 || firstSlash + 1 >= left.Length)
        {
            return false;
        }

        var secondSegment = left[(firstSlash + 1)..];
        if (secondSegment.IsEmpty)
        {
            return false;
        }

        if (secondSegment.IndexOf((byte)'/') == 0)
        {
            return false;
        }

        parsed = new ParsedActionRef(uses, at);
        return true;
    }

    internal static bool TryParseActionReference(string usesRef, out string owner, out string repo, out string reference)
    {
        owner = string.Empty;
        repo = string.Empty;
        reference = string.Empty;
        if (string.IsNullOrEmpty(usesRef))
        {
            return false;
        }

        var max = Encoding.UTF8.GetMaxByteCount(usesRef.Length);
        byte[]? rented = null;
        var buf = max <= 512 ? stackalloc byte[512] : (rented = ArrayPool<byte>.Shared.Rent(max));
        try
        {
            var n = Encoding.UTF8.GetBytes(usesRef, buf);
            if (!TryParseRemoteUses(buf[..n], out var parsed))
            {
                return false;
            }

            reference = Encoding.UTF8.GetString(parsed.Ref);
            return TrySplitOwnerRepoFromActionPath(parsed.ActionPath, out owner, out repo);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>First two path segments as UTF-8 spans (before further <c>/</c> path).</summary>
    internal static bool TryParseOwnerRepoSegments(ReadOnlySpan<byte> actionPath, out ReadOnlySpan<byte> owner, out ReadOnlySpan<byte> repo)
    {
        owner = default;
        repo = default;
        var slash1 = actionPath.IndexOf((byte)'/');
        if (slash1 <= 0 || slash1 + 1 >= actionPath.Length)
        {
            return false;
        }

        var rest = actionPath[(slash1 + 1)..];
        var slash2 = rest.IndexOf((byte)'/');
        if (slash2 == 0)
        {
            return false;
        }

        var ownerSpan = actionPath[..slash1];
        var repoSpan = slash2 < 0 ? rest : rest[..slash2];
        if (ownerSpan.IsEmpty || repoSpan.IsEmpty)
        {
            return false;
        }

        owner = ownerSpan;
        repo = repoSpan;
        return true;
    }

    /// <summary>First two path segments as separate strings (case preserved), for API / resolution.</summary>
    internal static bool TrySplitOwnerRepoFromActionPath(ReadOnlySpan<byte> actionPath, out string owner, out string repo)
    {
        if (!TryParseOwnerRepoSegments(actionPath, out var ownerSpan, out var repoSpan))
        {
            owner = string.Empty;
            repo = string.Empty;
            return false;
        }

        owner = Encoding.UTF8.GetString(ownerSpan);
        repo = Encoding.UTF8.GetString(repoSpan);
        return true;
    }

    /// <summary>
    /// Writes ASCII-lowercased <c>owner/repo</c> UTF-8 into <paramref name="scratch"/>; <paramref name="key"/> slices that buffer.
    /// </summary>
    internal static bool TryGetOwnerRepoPolicyKey(ReadOnlySpan<byte> actionPath, Span<byte> scratch, out ReadOnlySpan<byte> key)
    {
        key = default;
        if (!TryParseOwnerRepoSegments(actionPath, out var owner, out var repo))
        {
            return false;
        }

        var needed = owner.Length + 1 + repo.Length;
        if (scratch.Length < needed)
        {
            return false;
        }

        var o = 0;
        for (var i = 0; i < owner.Length; i++)
        {
            scratch[o++] = AsciiLowerByte(owner[i]);
        }

        scratch[o++] = (byte)'/';
        for (var i = 0; i < repo.Length; i++)
        {
            scratch[o++] = AsciiLowerByte(repo[i]);
        }

        key = scratch[..needed];
        return true;
    }

    private static byte AsciiLowerByte(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    /// <summary>
    /// Path-style wildcard used by <c>forbidden-uses</c> (<c>*</c> / <c>?</c>; <c>*</c> may span <c>/</c>).
    /// Distinct from <see cref="GlobMatch"/> (segment-oriented <c>*</c> / <c>**</c>).
    /// </summary>
    internal static bool WildcardMatchUsesPolicy(ReadOnlySpan<byte> text, ReadOnlySpan<byte> pattern)
    {
        var textIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var matchIndex = 0;

        while (textIndex < text.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == (byte)'?' || pattern[patternIndex] == text[textIndex]))
            {
                patternIndex++;
                textIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == (byte)'*')
            {
                starIndex = patternIndex;
                matchIndex = textIndex;
                patternIndex++;
                continue;
            }

            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                matchIndex++;
                textIndex = matchIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == (byte)'*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    internal static bool IsFullCommitSha(string reference)
    {
        if (reference.Length != 40)
        {
            return false;
        }

        for (var i = 0; i < reference.Length; i++)
        {
            var ch = reference[i];
            var isDigit = ch is >= '0' and <= '9';
            var isLowerHex = ch is >= 'a' and <= 'f';
            var isUpperHex = ch is >= 'A' and <= 'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsFullCommitSha(ReadOnlySpan<byte> reference)
    {
        if (reference.Length != 40)
        {
            return false;
        }

        for (var i = 0; i < reference.Length; i++)
        {
            var ch = reference[i];
            var isDigit = ch is >= (byte)'0' and <= (byte)'9';
            var isLowerHex = ch is >= (byte)'a' and <= (byte)'f';
            var isUpperHex = ch is >= (byte)'A' and <= (byte)'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsSha256DigestPinned(ReadOnlySpan<byte> image)
    {
        var at = image.LastIndexOf((byte)'@');
        if (at < 0 || at + 1 >= image.Length)
        {
            return false;
        }

        var digest = image[(at + 1)..];
        if (!digest.StartsWith("sha256:"u8))
        {
            return false;
        }

        var hash = digest["sha256:"u8.Length..];
        if (hash.Length != 64)
        {
            return false;
        }

        for (var i = 0; i < hash.Length; i++)
        {
            var b = hash[i];
            var isDigit = b is >= (byte)'0' and <= (byte)'9';
            var isLowerHex = b is >= (byte)'a' and <= (byte)'f';
            var isUpperHex = b is >= (byte)'A' and <= (byte)'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryExtractRefVersionMajor(ReadOnlySpan<byte> reference, out int major)
    {
        major = 0;
        if (reference.Length < 2 || reference[0] is not ((byte)'v' or (byte)'V'))
        {
            return false;
        }

        var end = 1;
        while (end < reference.Length && IsAsciiDigit(reference[end]))
        {
            end++;
        }

        if (end == 1)
        {
            return false;
        }

        return int.TryParse(Encoding.UTF8.GetString(reference[1..end]), NumberStyles.Integer, CultureInfo.InvariantCulture, out major);
    }

    internal static bool TryExtractPathVersionMajor(ReadOnlySpan<byte> actionPath, out int major)
    {
        major = 0;
        var slash1 = actionPath.IndexOf((byte)'/');
        if (slash1 <= 0 || slash1 + 1 >= actionPath.Length)
        {
            return false;
        }

        var remainder = actionPath[(slash1 + 1)..];
        var slash2 = remainder.IndexOf((byte)'/');
        if (slash2 <= 0)
        {
            return TryExtractMajorFromPathSegment(slash2 < 0 ? remainder : remainder[..slash2], out major);
        }

        var repo = remainder[..slash2];
        if (TryExtractMajorFromPathSegment(repo, out major))
        {
            return true;
        }

        var subPath = remainder[(slash2 + 1)..];
        while (subPath.Length > 0)
        {
            var slash = subPath.IndexOf((byte)'/');
            var segment = slash < 0 ? subPath : subPath[..slash];
            if (TryExtractMajorFromPathSegment(segment, out major))
            {
                return true;
            }

            if (slash < 0)
            {
                break;
            }

            subPath = subPath[(slash + 1)..];
        }

        return false;
    }

    private static bool TryExtractMajorFromPathSegment(ReadOnlySpan<byte> segment, out int major)
    {
        major = 0;
        if (segment.Length == 0)
        {
            return false;
        }

        var trimmed = TrimKnownYamlExtension(segment);
        if (trimmed.Length < 2)
        {
            return false;
        }

        var candidateStart = -1;
        if ((trimmed[0] is (byte)'v' or (byte)'V') && IsAsciiDigit(trimmed[1]))
        {
            candidateStart = 1;
        }
        else
        {
            for (var i = 1; i + 1 < trimmed.Length; i++)
            {
                if ((trimmed[i - 1] is (byte)'-' or (byte)'_')
                    && (trimmed[i] is (byte)'v' or (byte)'V')
                    && IsAsciiDigit(trimmed[i + 1]))
                {
                    candidateStart = i + 1;
                    break;
                }
            }
        }

        if (candidateStart < 0)
        {
            return false;
        }

        var end = candidateStart;
        while (end < trimmed.Length && IsAsciiDigit(trimmed[end]))
        {
            end++;
        }

        return int.TryParse(
            Encoding.UTF8.GetString(trimmed[candidateStart..end]),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out major);
    }

    private static ReadOnlySpan<byte> TrimKnownYamlExtension(ReadOnlySpan<byte> segment)
    {
        if (segment.EndsWith(".yml"u8))
        {
            return segment[..^4];
        }

        if (segment.EndsWith(".yaml"u8))
        {
            return segment[..^5];
        }

        return segment;
    }

    private static bool IsAsciiDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    internal static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    internal static bool GlobMatch(string pattern, string path)
    {
        if (pattern.Length == 0)
        {
            return path.Length == 0;
        }

        var normalizedPattern = pattern.Replace('\\', '/');
        var normalizedPath = path.Replace('\\', '/');
        var cache = new Dictionary<(int PatternIndex, int PathIndex), bool>();
        return GlobMatchCore(normalizedPattern, normalizedPath, 0, 0, cache);
    }

    private static bool GlobMatchCore(string pattern, string path, int patternIndex, int pathIndex, Dictionary<(int PatternIndex, int PathIndex), bool> cache)
    {
        if (cache.TryGetValue((patternIndex, pathIndex), out var cached))
        {
            return cached;
        }

        var patternLength = pattern.Length;
        var pathLength = path.Length;

        while (patternIndex < patternLength)
        {
            var ch = pattern[patternIndex];
            if (ch == '*')
            {
                var isDoubleStar = patternIndex + 1 < patternLength && pattern[patternIndex + 1] == '*';
                if (isDoubleStar)
                {
                    patternIndex += 2;
                    while (patternIndex < patternLength && pattern[patternIndex] == '*')
                    {
                        patternIndex++;
                    }

                    if (patternIndex >= patternLength)
                    {
                        cache[(patternIndex, pathIndex)] = true;
                        return true;
                    }

                    for (var cursor = pathIndex; cursor <= pathLength; cursor++)
                    {
                        if (GlobMatchCore(pattern, path, patternIndex, cursor, cache))
                        {
                            cache[(patternIndex, pathIndex)] = true;
                            return true;
                        }
                    }

                    cache[(patternIndex, pathIndex)] = false;
                    return false;
                }

                patternIndex++;
                for (var cursor = pathIndex; ; cursor++)
                {
                    if (GlobMatchCore(pattern, path, patternIndex, cursor, cache))
                    {
                        cache[(patternIndex, pathIndex)] = true;
                        return true;
                    }

                    if (cursor >= pathLength || path[cursor] == '/')
                    {
                        break;
                    }
                }

                cache[(patternIndex, pathIndex)] = false;
                return false;
            }

            if (pathIndex >= pathLength)
            {
                cache[(patternIndex, pathIndex)] = false;
                return false;
            }

            if (ch == '?')
            {
                if (path[pathIndex] == '/')
                {
                    cache[(patternIndex, pathIndex)] = false;
                    return false;
                }

                patternIndex++;
                pathIndex++;
                continue;
            }

            if (ch != path[pathIndex])
            {
                cache[(patternIndex, pathIndex)] = false;
                return false;
            }

            patternIndex++;
            pathIndex++;
        }

        var result = pathIndex == pathLength;
        cache[(patternIndex, pathIndex)] = result;
        return result;
    }

    /// <summary>
    /// Builds a GitHub tree URL from an action reference string (e.g., "actions/setup-node@v4"
    /// → "https://github.com/actions/setup-node/tree/v4").
    /// Returns null if the reference cannot be parsed.
    /// </summary>
    internal static string? BuildGitHubUrl(string actionRef)
    {
        if (!TryParseActionReference(actionRef, out var owner, out var repo, out var reference))
        {
            return null;
        }

        return $"https://github.com/{owner}/{repo}/tree/{reference}";
    }
}
