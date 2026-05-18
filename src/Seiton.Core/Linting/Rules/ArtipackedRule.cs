using System.Runtime.CompilerServices;
using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags credential leakage risk when <c>actions/checkout</c> (without <c>persist-credentials: false</c>)
/// is combined with <c>actions/upload-artifact</c> that uploads a dangerous path (e.g. <c>.</c>, <c>..</c>).</summary>
public sealed class ArtipackedRule() : RuleBase(RuleId.Artipacked)
{
    private const int MaxNormalizedPathSegments = 128;

    private Utf8Slice _lastPathSlice;
    private bool _lastMessageIsV6Plus;
    private string? _lastMessage;

    public override string Name => "Artipacked Rule";

    public override bool SupportsDocumentKind(DocumentKind documentKind) => documentKind == DocumentKind.Workflow;

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        _lastPathSlice = default;
        _lastMessageIsV6Plus = false;
        _lastMessage = null;
    }

    public override void VisitJobPost(Job job)
    {
        if (job.Steps is not { Count: > 0 } steps || Config.Utf8Yaml is null)
        {
            return;
        }

        var utf8Yaml = Config.Utf8Yaml;
        var hasUnsafeLegacyCheckout = false;
        var hasUnsafeV6PlusCheckout = false;

        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Exec is not ExecAction actionExec)
            {
                continue;
            }

            var usesText = Arena.GetStringValue(actionExec.Uses);
            if (!PopularActions.TryGet(usesText, out var actionSpec))
            {
                continue;
            }

            if (actionSpec.Id == PopularActions.ActionId.ActionsCheckout)
            {
                if (HasPersistCredentialsFalse(actionExec, utf8Yaml))
                {
                    continue;
                }

                if (TryExtractMajorAndMinorVersion(usesText, out var checkoutMajor, out _, out _))
                {
                    if (checkoutMajor >= 6)
                    {
                        hasUnsafeV6PlusCheckout = true;
                    }
                    else
                    {
                        hasUnsafeLegacyCheckout = true;
                    }
                }
                else
                {
                    // Cannot determine version (SHA/branch ref) — conservatively assume both risks
                    hasUnsafeLegacyCheckout = true;
                    hasUnsafeV6PlusCheckout = true;
                }

                continue;
            }

            if (!hasUnsafeLegacyCheckout && !hasUnsafeV6PlusCheckout)
            {
                continue;
            }

            if (actionSpec.Id != PopularActions.ActionId.ActionsUploadArtifact
                || actionExec.Inputs is null
                || !actionExec.Inputs.Value.TryGetValue(utf8Yaml, "path"u8, out var pathNode))
            {
                continue;
            }

            var pathValue = Arena.GetStringValue(pathNode);
            if (!TryClassifyDangerousPath(pathValue, out var exposesParentDirectory, out var excludesLegacyCredentialPath))
            {
                continue;
            }

            var mayIncludeHiddenFiles = MayIncludeHiddenFiles(actionExec, usesText, utf8Yaml);
            var legacyCredentialsExcluded = excludesLegacyCredentialPath
                                            && ArePrecedingLegacyCheckoutsExcludedByUploadPath(steps, i, pathValue, utf8Yaml);
            var mayExposeLegacyCredentials = hasUnsafeLegacyCheckout
                                             && mayIncludeHiddenFiles
                                             && !legacyCredentialsExcluded;
            var mayExposeV6PlusCredentials = hasUnsafeV6PlusCheckout && exposesParentDirectory;
            if (!mayExposeLegacyCredentials && !mayExposeV6PlusCredentials)
            {
                continue;
            }

            // Error when legacy checkout credentials (.git/config) are actually exposed
            // (hidden files included); warning otherwise (only v6+ $RUNNER_TEMP concern).
            var reportAsWarning = !mayExposeLegacyCredentials;
            var message = GetCachedMessage(Arena.GetStringSlice(pathNode), reportAsWarning, utf8Yaml);
            if (reportAsWarning)
            {
                AddStepWarning(steps[i], message, GetRange(pathNode));
            }
            else
            {
                AddStepError(steps[i], message, GetRange(pathNode));
            }
        }
    }

    private bool HasPersistCredentialsFalse(ExecAction actionExec, byte[] utf8Yaml)
    {
        if (actionExec.Inputs is null
            || !actionExec.Inputs.Value.TryGetValue(utf8Yaml, "persist-credentials"u8, out var persistCredentialsNode))
        {
            return false;
        }

        var value = Arena.GetStringValue(persistCredentialsNode);
        return !ExpressionScanHelpers.ContainsExpressionMarker(persistCredentialsNode, Arena)
               && IsBooleanFalse(value);
    }

    private bool ArePrecedingLegacyCheckoutsExcludedByUploadPath(IReadOnlyList<Step> steps, int uploadStepIndex, ReadOnlySpan<byte> uploadPath, byte[] utf8Yaml)
    {
        for (var i = 0; i < uploadStepIndex; i++)
        {
            if (steps[i].Exec is not ExecAction actionExec)
            {
                continue;
            }

            var usesText = Arena.GetStringValue(actionExec.Uses);
            if (!PopularActions.TryGet(usesText, out var actionSpec)
                || actionSpec.Id != PopularActions.ActionId.ActionsCheckout
                || HasPersistCredentialsFalse(actionExec, utf8Yaml))
            {
                continue;
            }

            if (TryExtractMajorAndMinorVersion(usesText, out var checkoutMajor, out _, out _) && checkoutMajor >= 6)
            {
                continue;
            }

            if (!IsLegacyCheckoutPathExcludedByUploadPath(uploadPath, actionExec, utf8Yaml))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsLegacyCheckoutPathExcludedByUploadPath(ReadOnlySpan<byte> uploadPath, ExecAction checkoutAction, byte[] utf8Yaml)
    {
        ReadOnlySpan<byte> checkoutPath = ReadOnlySpan<byte>.Empty;
        if (checkoutAction.Inputs is not null
            && checkoutAction.Inputs.Value.TryGetValue(utf8Yaml, "path"u8, out var checkoutPathNode))
        {
            checkoutPath = Arena.GetStringValue(checkoutPathNode);
        }

        while (uploadPath.Length > 0)
        {
            var nlIndex = uploadPath.IndexOf((byte)'\n');
            var line = nlIndex >= 0 ? uploadPath[..nlIndex] : uploadPath;
            line = TrimBytes(line);

            if (line.Length > 1 && line[0] == (byte)'!' && ExcludesLegacyCredentialPath(TrimBytes(line[1..]), checkoutPath))
            {
                return true;
            }

            if (nlIndex < 0)
            {
                break;
            }

            uploadPath = uploadPath[(nlIndex + 1)..];
        }

        return false;
    }

    private bool MayIncludeHiddenFiles(ExecAction actionExec, ReadOnlySpan<byte> usesText, byte[] utf8Yaml)
    {
        if (!TryExtractMajorAndMinorVersion(usesText, out var majorVersion, out var minorVersion, out var hasMinorVersion))
        {
            return true;
        }

        if (majorVersion < 4)
        {
            return true;
        }

        // Hidden-file defaults are only modeled for upload-artifact v4.
        // Newer major versions are treated conservatively as unknown.
        if (majorVersion > 4)
        {
            return true;
        }

        // upload-artifact v4.0-v4.3 included hidden files by default (include-hidden-files input did not exist).
        // v4.4+ excludes hidden files by default. @v4 (no minor) is a floating tag pointing to latest v4.x (safe).
        if (majorVersion == 4 && hasMinorVersion && minorVersion < 4)
        {
            return true;
        }

        if (actionExec.Inputs is null
            || !actionExec.Inputs.Value.TryGetValue(utf8Yaml, "include-hidden-files"u8, out var includeHiddenFilesNode))
        {
            return false;
        }

        if (ExpressionScanHelpers.ContainsExpressionMarker(includeHiddenFilesNode, Arena))
        {
            return true;
        }

        var value = Arena.GetStringValue(includeHiddenFilesNode);
        return IsBooleanTrue(value);
    }

    /// <summary>Case-insensitive YAML boolean false check (false, False, FALSE).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBooleanFalse(ReadOnlySpan<byte> value)
    {
        return value.Length == 5
               && (value[0] | 0x20) == (byte)'f'
               && (value[1] | 0x20) == (byte)'a'
               && (value[2] | 0x20) == (byte)'l'
               && (value[3] | 0x20) == (byte)'s'
               && (value[4] | 0x20) == (byte)'e';
    }

    /// <summary>Case-insensitive YAML boolean true check (true, True, TRUE).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBooleanTrue(ReadOnlySpan<byte> value)
    {
        return value.Length == 4
               && (value[0] | 0x20) == (byte)'t'
               && (value[1] | 0x20) == (byte)'r'
               && (value[2] | 0x20) == (byte)'u'
               && (value[3] | 0x20) == (byte)'e';
    }

    /// <summary>Extracts major and optionally minor version from a uses reference like <c>actions/upload-artifact@v4.4</c>.</summary>
    private static bool TryExtractMajorAndMinorVersion(ReadOnlySpan<byte> usesText, out int majorVersion, out int minorVersion, out bool hasMinorVersion)
    {
        majorVersion = 0;
        minorVersion = 0;
        hasMinorVersion = false;

        if (!OutdatedActionRunnerRule.TryExtractMajorVersion(usesText, out majorVersion))
        {
            return false;
        }

        // Look for .minor after the major version
        var atIndex = usesText.IndexOf((byte)'@');
        if (atIndex < 0)
        {
            return true;
        }

        var versionText = usesText[(atIndex + 1)..];
        if (versionText.Length > 0 && (versionText[0] == (byte)'v' || versionText[0] == (byte)'V'))
        {
            versionText = versionText[1..];
        }

        // Skip major digits
        var pos = 0;
        while (pos < versionText.Length && versionText[pos] >= (byte)'0' && versionText[pos] <= (byte)'9')
        {
            pos++;
        }

        if (HasLeadingZeroNumericIdentifier(versionText, pos))
        {
            return false;
        }

        // Exact major-only ref (e.g. @v4) is treated as floating tag pointing to latest
        if (pos >= versionText.Length)
        {
            return true;
        }

        // Any non-dot suffix after the major digits is an arbitrary ref/tag/branch
        // (e.g. @v4-legacy) — treat conservatively as unknown version
        if (versionText[pos] != (byte)'.')
        {
            return false;
        }

        // Parse minor version
        var minorText = versionText[(pos + 1)..];
        var minorPos = 0;
        while (minorPos < minorText.Length && minorText[minorPos] >= (byte)'0' && minorText[minorPos] <= (byte)'9')
        {
            minorVersion = (minorVersion * 10) + (minorText[minorPos] - (byte)'0');
            minorPos++;
        }

        // No minor digits after dot (e.g. @v4. or @v4.x) — unknown ref
        if (minorPos == 0)
        {
            return false;
        }

        if (HasLeadingZeroNumericIdentifier(minorText, minorPos))
        {
            return false;
        }

        // Trailing content after minor digits
        if (minorPos < minorText.Length)
        {
            // Accept optional numeric patch segment (e.g. @v4.6.2)
            if (minorText[minorPos] == (byte)'.')
            {
                var patchText = minorText[(minorPos + 1)..];
                var patchPos = 0;
                while (patchPos < patchText.Length && patchText[patchPos] >= (byte)'0' && patchText[patchPos] <= (byte)'9')
                {
                    patchPos++;
                }

                // No patch digits after dot (e.g. @v4.6.) or trailing suffix (e.g. @v4.6.2-legacy) — unknown ref
                if (patchPos == 0 || patchPos < patchText.Length || HasLeadingZeroNumericIdentifier(patchText, patchPos))
                {
                    return false;
                }
            }
            else
            {
                // Non-dot suffix after minor digits (e.g. @v4.4-legacy) — unknown ref
                return false;
            }
        }

        hasMinorVersion = true;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasLeadingZeroNumericIdentifier(ReadOnlySpan<byte> text, int digitCount)
    {
        return digitCount > 1 && text[0] == (byte)'0';
    }

    /// <summary>Checks whether the upload path covers the repository root or parent directories.</summary>
    internal static bool TryClassifyDangerousPath(ReadOnlySpan<byte> path, out bool exposesParentDirectory, out bool excludesLegacyCredentialPath)
    {
        exposesParentDirectory = false;
        excludesLegacyCredentialPath = false;
        var hasDangerousLine = false;

        // Handle multiline paths: each line is a separate glob. Scan all lines
        // to accumulate exposure and explicit .git exclusions across the entire path value.
        while (path.Length > 0)
        {
            // Find end of line
            var nlIndex = path.IndexOf((byte)'\n');
            var line = nlIndex >= 0 ? path.Slice(0, nlIndex) : path;

            // Trim \r and spaces
            line = TrimBytes(line);

            if (line.Length > 0)
            {
                if (TryClassifyLegacyCredentialExclusion(line))
                {
                    excludesLegacyCredentialPath = true;
                }
                else if (TryClassifyDangerousLine(line, out var lineExposesParentDirectory))
                {
                    hasDangerousLine = true;
                    exposesParentDirectory |= lineExposesParentDirectory;
                }
            }

            if (nlIndex < 0)
            {
                break;
            }

            path = path.Slice(nlIndex + 1);
        }

        return hasDangerousLine;
    }

    private static bool TryClassifyLegacyCredentialExclusion(ReadOnlySpan<byte> line)
    {
        if (line.Length == 0 || line[0] != (byte)'!')
        {
            return false;
        }

        return CouldExcludeLegacyCredentialPath(TrimBytes(line[1..]));
    }

    private static bool CouldExcludeLegacyCredentialPath(ReadOnlySpan<byte> pattern)
    {
        pattern = SkipCurrentDirectoryPrefixes(pattern);

        if (MatchesNormalizedLegacyCredentialExclusionPattern(pattern))
        {
            return true;
        }

        if (TryStripGitHubWorkspacePrefix(pattern, out var workspaceRelativePattern))
        {
            workspaceRelativePattern = SkipCurrentDirectoryPrefixes(workspaceRelativePattern);
            if (MatchesNormalizedLegacyCredentialExclusionPattern(workspaceRelativePattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ExcludesLegacyCredentialPath(ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> checkoutPath)
    {
        pattern = SkipCurrentDirectoryPrefixes(pattern);

        if (MatchesNormalizedLegacyCredentialExclusion(pattern, checkoutPath))
        {
            return true;
        }

        if (TryStripGitHubWorkspacePrefix(pattern, out var workspaceRelativePattern))
        {
            workspaceRelativePattern = SkipCurrentDirectoryPrefixes(workspaceRelativePattern);
            if (MatchesNormalizedLegacyCredentialExclusion(workspaceRelativePattern, checkoutPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesNormalizedLegacyCredentialExclusionPattern(ReadOnlySpan<byte> pattern)
    {
        Span<int> offsets = stackalloc int[MaxNormalizedPathSegments];
        Span<int> lengths = stackalloc int[MaxNormalizedPathSegments];
        if (!TryNormalizeRelativePathSegments(pattern, offsets, lengths, out var segmentCount, allowRecursiveWildcards: true))
        {
            return false;
        }

        if (segmentCount == 0)
        {
            return false;
        }

        if (IsPatternSegment(pattern, offsets[segmentCount - 1], lengths[segmentCount - 1], ".git"u8))
        {
            return true;
        }

        if (segmentCount < 2 || !IsPatternSegment(pattern, offsets[segmentCount - 2], lengths[segmentCount - 2], ".git"u8))
        {
            return false;
        }

        return IsPatternSegment(pattern, offsets[segmentCount - 1], lengths[segmentCount - 1], "**"u8)
               || IsPatternSegment(pattern, offsets[segmentCount - 1], lengths[segmentCount - 1], "config"u8);
    }

    private static bool MatchesNormalizedLegacyCredentialExclusion(ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> checkoutPath)
    {
            Span<int> patternOffsets = stackalloc int[MaxNormalizedPathSegments];
            Span<int> patternLengths = stackalloc int[MaxNormalizedPathSegments];
        if (!TryNormalizeRelativePathSegments(pattern, patternOffsets, patternLengths, out var patternSegmentCount, allowRecursiveWildcards: true))
        {
            return false;
        }

            Span<int> checkoutOffsets = stackalloc int[MaxNormalizedPathSegments];
            Span<int> checkoutLengths = stackalloc int[MaxNormalizedPathSegments];
        if (!TryNormalizeRelativePathSegments(checkoutPath, checkoutOffsets, checkoutLengths, out var checkoutSegmentCount, allowRecursiveWildcards: false))
        {
            return false;
        }

        var start = 0;
        while (start < patternSegmentCount && IsPatternSegment(pattern, patternOffsets[start], patternLengths[start], "**"u8))
        {
            start++;
        }

        if (patternSegmentCount - start < checkoutSegmentCount + 1)
        {
            return false;
        }

        for (var i = 0; i < checkoutSegmentCount; i++)
        {
            if (!pattern.Slice(patternOffsets[start + i], patternLengths[start + i])
                    .SequenceEqual(checkoutPath.Slice(checkoutOffsets[i], checkoutLengths[i])))
            {
                return false;
            }
        }

        return MatchesLegacyCredentialExclusionTail(pattern, patternOffsets, patternLengths, patternSegmentCount, start + checkoutSegmentCount);
    }

    private static bool MatchesLegacyCredentialExclusionTail(ReadOnlySpan<byte> pattern, Span<int> offsets, Span<int> lengths, int segmentCount, int start)
    {
        if (start >= segmentCount || !IsPatternSegment(pattern, offsets[start], lengths[start], ".git"u8))
        {
            return false;
        }

        var remaining = segmentCount - start;
        if (remaining == 1)
        {
            return true;
        }

        if (remaining != 2)
        {
            return false;
        }

        return IsPatternSegment(pattern, offsets[start + 1], lengths[start + 1], "**"u8)
               || IsPatternSegment(pattern, offsets[start + 1], lengths[start + 1], "config"u8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> SkipCurrentDirectoryPrefixes(ReadOnlySpan<byte> pattern)
    {
        while (pattern.Length >= 2 && pattern[0] == (byte)'.' && (pattern[1] == (byte)'/' || pattern[1] == (byte)'\\'))
        {
            pattern = pattern[2..];
        }

        return pattern;
    }

    private static bool TryStripGitHubWorkspacePrefix(ReadOnlySpan<byte> value, out ReadOnlySpan<byte> suffix)
    {
        suffix = default;

        var trimmed = TrimBytes(value);
        if (trimmed.Length < 5 || !trimmed.StartsWith("${{"u8))
        {
            return false;
        }

        var closeIndex = trimmed.IndexOf("}}"u8);
        if (closeIndex < 0)
        {
            return false;
        }

        var inner = TrimBytes(trimmed.Slice(3, closeIndex - 3));
        if (!IsGitHubWorkspaceReference(inner))
        {
            return false;
        }

        suffix = TrimBytes(trimmed[(closeIndex + 2)..]);
        while (suffix.Length > 0 && (suffix[0] == (byte)'/' || suffix[0] == (byte)'\\'))
        {
            suffix = suffix[1..];
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryConsumeGitDirectory(ref ReadOnlySpan<byte> pattern)
    {
        if (!pattern.StartsWith(".git"u8))
        {
            return false;
        }

        pattern = pattern[4..];
        return true;
    }

    private static bool TryClassifyDangerousLine(ReadOnlySpan<byte> line, out bool exposesParentDirectory)
    {
        exposesParentDirectory = false;

        if (IsCurrentDirectoryPath(line))
        {
            return true;
        }

        if (IsParentDirectoryPath(line))
        {
            exposesParentDirectory = true;
            return true;
        }

        if (TryClassifyGitHubWorkspaceExpression(line, out exposesParentDirectory))
        {
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCurrentDirectoryPath(ReadOnlySpan<byte> path)
    {
        return TryClassifyRelativeDirectoryPath(path, out var dotDotSegments) && dotDotSegments == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsParentDirectoryPath(ReadOnlySpan<byte> path)
    {
        return TryClassifyRelativeDirectoryPath(path, out var dotDotSegments) && dotDotSegments >= 1;
    }

    private static bool TryNormalizeRelativePathSegments(ReadOnlySpan<byte> path, Span<int> offsets, Span<int> lengths, out int count, bool allowRecursiveWildcards)
    {
        count = 0;
        var cursor = 0;

        while (cursor < path.Length)
        {
            var separatorIndex = FindPathSeparator(path[cursor..]);
            var rawSegmentStart = cursor;
            var rawSegmentLength = separatorIndex >= 0 ? separatorIndex : path.Length - cursor;
            cursor += rawSegmentLength;
            if (separatorIndex >= 0)
            {
                cursor++;
            }

            var segmentStart = rawSegmentStart;
            var segmentLength = rawSegmentLength;
            while (segmentLength > 0 && IsPathTrimByte(path[segmentStart]))
            {
                segmentStart++;
                segmentLength--;
            }

            while (segmentLength > 0 && IsPathTrimByte(path[segmentStart + segmentLength - 1]))
            {
                segmentLength--;
            }

            if (segmentLength == 0)
            {
                continue;
            }

            var segment = path.Slice(segmentStart, segmentLength);

            if (segment.SequenceEqual("."u8))
            {
                continue;
            }

            if (segment.SequenceEqual(".."u8))
            {
                if (count == 0)
                {
                    return false;
                }

                count--;
                continue;
            }

            if (ContainsExpressionMarker(segment) || IsSingleWildcardSegment(segment))
            {
                return false;
            }

            if (IsRecursiveWildcardSegment(segment) && !allowRecursiveWildcards)
            {
                return false;
            }

            if (count == offsets.Length)
            {
                return false;
            }

            offsets[count] = segmentStart;
            lengths[count] = segmentLength;
            count++;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPatternSegment(ReadOnlySpan<byte> pattern, int offset, int length, ReadOnlySpan<byte> expected)
    {
        return pattern.Slice(offset, length).SequenceEqual(expected);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPathTrimByte(byte value)
    {
        return value is (byte)' ' or (byte)'\t' or (byte)'\r';
    }

    private static bool TryClassifyRelativeDirectoryPath(ReadOnlySpan<byte> path, out int dotDotSegments)
    {
        dotDotSegments = 0;
        var namedSegments = 0;
        var hasRootRecursiveWildcard = false;

        while (path.Length > 0)
        {
            var separatorIndex = FindPathSeparator(path);
            var segment = separatorIndex >= 0 ? path[..separatorIndex] : path;
            path = separatorIndex >= 0 ? path[(separatorIndex + 1)..] : ReadOnlySpan<byte>.Empty;

            if (segment.Length == 0 || segment.SequenceEqual("."u8))
            {
                continue;
            }

            if (segment.SequenceEqual(".."u8))
            {
                if (namedSegments > 0)
                {
                    namedSegments--;
                }
                else
                {
                    dotDotSegments++;
                }
                continue;
            }

            // Expression segments (${{ ... }}) are not glob patterns — they resolve at runtime
            // and their danger cannot be determined statically. Only ${{ github.workspace }}
            // is handled separately by TryClassifyGitHubWorkspaceExpression.
            if (ContainsExpressionMarker(segment))
            {
                return false;
            }

            // Only recursive root globs (`**`) are treated as equivalent to uploading the
            // current/parent directory. Narrow file globs like `*.txt` are not.
            if (IsRecursiveWildcardSegment(segment))
            {
                if (namedSegments > 0)
                {
                    return false;
                }

                hasRootRecursiveWildcard = true;
                continue;
            }

            // Treat `**/*` and `./**/*` as equivalent to root-recursive uploads.
            // This intentionally stays narrow: named prefixes like `src/**/*` are not
            // root-like and remain safe.
            if (hasRootRecursiveWildcard && namedSegments == 0 && IsSingleWildcardSegment(segment))
            {
                continue;
            }

            namedSegments++;
        }

        return namedSegments == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsExpressionMarker(ReadOnlySpan<byte> segment)
    {
        return segment.IndexOf("${{"u8) >= 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsRecursiveWildcardSegment(ReadOnlySpan<byte> segment) => segment.SequenceEqual("**"u8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSingleWildcardSegment(ReadOnlySpan<byte> segment) => segment.SequenceEqual("*"u8);

    private static bool TryClassifyGitHubWorkspaceExpression(ReadOnlySpan<byte> value, out bool exposesParentDirectory)
    {
        exposesParentDirectory = false;

        var trimmed = TrimBytes(value);
        if (trimmed.Length < 5 || !trimmed.StartsWith("${{"u8))
        {
            return false;
        }

        var closeIndex = trimmed.IndexOf("}}"u8);
        if (closeIndex < 0)
        {
            return false;
        }

        var inner = TrimBytes(trimmed.Slice(3, closeIndex - 3));
        if (!IsGitHubWorkspaceReference(inner))
        {
            return false;
        }

        var suffix = TrimBytes(trimmed[(closeIndex + 2)..]);
        while (suffix.Length > 0 && (suffix[0] == (byte)'/' || suffix[0] == (byte)'\\'))
        {
            suffix = suffix[1..];
        }

        if (suffix.Length == 0)
        {
            return true;
        }

        if (!TryClassifyRelativeDirectoryPath(suffix, out var suffixDotDotSegments))
        {
            return false;
        }

        exposesParentDirectory = suffixDotDotSegments >= 1;
        return true;
    }

    private static bool IsGitHubWorkspaceReference(ReadOnlySpan<byte> expression)
    {
        if (!StartsWithAsciiIgnoreCase(expression, "github"u8))
        {
            return false;
        }

        expression = TrimBytes(expression[6..]);
        if (expression.Length == 0)
        {
            return false;
        }

        if (expression[0] == (byte)'.')
        {
            return SequenceEqualAsciiIgnoreCase(TrimBytes(expression[1..]), "workspace"u8);
        }

        if (expression[0] != (byte)'[')
        {
            return false;
        }

        expression = TrimBytes(expression[1..]);
        if (expression.Length < 3)
        {
            return false;
        }

        var quote = expression[0];
        if (quote != (byte)'\'' && quote != (byte)'"')
        {
            return false;
        }

        expression = expression[1..];
        if (!StartsWithAsciiIgnoreCase(expression, "workspace"u8))
        {
            return false;
        }

        expression = expression[9..];
        if (expression.Length == 0 || expression[0] != quote)
        {
            return false;
        }

        expression = TrimBytes(expression[1..]);
        return expression.Length == 1 && expression[0] == (byte)']';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
    {
        return value.Length >= prefix.Length && SequenceEqualAsciiIgnoreCase(value[..prefix.Length], prefix);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SequenceEqualAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var leftByte = left[i];
            var rightByte = right[i];
            if (leftByte == rightByte)
            {
                continue;
            }

            if ((leftByte | 0x20) != (rightByte | 0x20))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Finds the first path separator (/ or \) in the span, or -1 if not found.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindPathSeparator(ReadOnlySpan<byte> span)
    {
        var fwd = span.IndexOf((byte)'/');
        var bck = span.IndexOf((byte)'\\');
        if (fwd < 0)
        {
            return bck;
        }

        if (bck < 0)
        {
            return fwd;
        }

        return fwd < bck ? fwd : bck;
    }

    /// <summary>Trims leading and trailing space, tab, and carriage return bytes from the span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> TrimBytes(ReadOnlySpan<byte> span)
    {
        var start = 0;
        while (start < span.Length && (span[start] == (byte)' ' || span[start] == (byte)'\t' || span[start] == (byte)'\r'))
        {
            start++;
        }

        var end = span.Length - 1;
        while (end >= start && (span[end] == (byte)' ' || span[end] == (byte)'\t' || span[end] == (byte)'\r'))
        {
            end--;
        }

        return start > end ? ReadOnlySpan<byte>.Empty : span.Slice(start, end - start + 1);
    }

    private string GetCachedMessage(Utf8Slice pathSlice, bool isV6Plus, byte[] utf8Yaml)
    {
        if (_lastMessage is not null
            && isV6Plus == _lastMessageIsV6Plus
            && pathSlice.Length == _lastPathSlice.Length
            && pathSlice.AsSpan(utf8Yaml).SequenceEqual(_lastPathSlice.AsSpan(utf8Yaml)))
        {
            _lastPathSlice = pathSlice;
            return _lastMessage;
        }

        var message = BuildMessage(pathSlice, isV6Plus);
        _lastPathSlice = pathSlice;
        _lastMessageIsV6Plus = isV6Plus;
        _lastMessage = message;
        return message;
    }

    private static string FormatPathForMessage(string path)
    {
        const int maxLength = 120;
        var formatted = path.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return formatted.Length <= maxLength ? formatted : string.Concat(formatted.AsSpan(0, maxLength), "...");
    }

    private string BuildMessage(Utf8Slice pathSlice, bool isV6Plus)
    {
        var uploadPath = FormatPathForMessage(Decode(pathSlice));
        if (isV6Plus)
        {
            return $"upload-artifact with path '{uploadPath}' may expose credentials; checkout v6+ or an unknown checkout ref may store credentials in $RUNNER_TEMP, so persist-credentials: false is still recommended";
        }

        return $"upload-artifact with path '{uploadPath}' may expose credentials persisted by checkout in .git/config; set persist-credentials: false on the checkout step";
    }
}
