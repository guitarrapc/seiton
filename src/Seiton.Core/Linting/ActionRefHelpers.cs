namespace Seiton.Core.Linting;

internal static class ActionRefHelpers
{
    internal static bool TryParseActionReference(string usesRef, out string owner, out string repo, out string reference)
    {
        owner = string.Empty;
        repo = string.Empty;
        reference = string.Empty;

        var at = usesRef.LastIndexOf('@');
        if (at <= 0 || at == usesRef.Length - 1)
        {
            return false;
        }

        var actionPath = usesRef[..at];
        reference = usesRef[(at + 1)..];

        var slash1 = actionPath.IndexOf('/');
        if (slash1 <= 0 || slash1 == actionPath.Length - 1)
        {
            return false;
        }

        var slash2 = actionPath.IndexOf('/', slash1 + 1);
        owner = actionPath[..slash1];
        repo = slash2 < 0 ? actionPath[(slash1 + 1)..] : actionPath.Substring(slash1 + 1, slash2 - (slash1 + 1));
        return owner.Length > 0 && repo.Length > 0 && reference.Length > 0;
    }

    internal static bool TryParseActionReference(ReadOnlySpan<byte> uses, out ReadOnlySpan<byte> actionPath, out ReadOnlySpan<byte> reference)
    {
        actionPath = [];
        reference = [];

        if (uses.IsEmpty || uses.StartsWith("./"u8) || uses.StartsWith("docker://"u8))
        {
            return false;
        }

        var at = uses.LastIndexOf((byte)'@');
        if (at <= 0 || at + 1 >= uses.Length)
        {
            return false;
        }

        actionPath = uses[..at];
        reference = uses[(at + 1)..];
        return true;
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

    static bool GlobMatchCore(
        string pattern,
        string path,
        int patternIndex,
        int pathIndex,
        Dictionary<(int PatternIndex, int PathIndex), bool> cache)
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
}
