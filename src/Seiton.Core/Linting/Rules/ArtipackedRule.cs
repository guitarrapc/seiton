using System.Runtime.CompilerServices;
using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags credential leakage risk when <c>actions/checkout</c> (without <c>persist-credentials: false</c>)
/// is combined with <c>actions/upload-artifact</c> that uploads a dangerous path (e.g. <c>.</c>, <c>..</c>).</summary>
public sealed class ArtipackedRule() : RuleBase(RuleId.Artipacked)
{
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

                if (IsV6OrLater(usesText))
                {
                    hasUnsafeV6PlusCheckout = true;
                    continue;
                }

                hasUnsafeLegacyCheckout = true;
                continue;
            }

            if (!hasUnsafeLegacyCheckout && !hasUnsafeV6PlusCheckout)
            {
                continue;
            }

            if (actionSpec.Id != PopularActions.ActionId.ActionsUploadArtifact
                || actionExec.Inputs is null
                || !actionExec.Inputs.Value.TryGetValue(utf8Yaml, "path"u8, out var pathNode)
                || !IsDangerousPath(Arena.GetStringValue(pathNode))
                || !MayIncludeHiddenFiles(actionExec, usesText, utf8Yaml))
            {
                continue;
            }

            var reportAsWarning = !hasUnsafeLegacyCheckout;
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
                if (patchPos == 0 || patchPos < patchText.Length)
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

    /// <summary>Checks whether the upload path covers the repository root (and thus .git/config).</summary>
    internal static bool IsDangerousPath(ReadOnlySpan<byte> path)
    {
        // Handle multiline paths: each line is a separate glob. If any line is dangerous, flag it.
        while (path.Length > 0)
        {
            // Find end of line
            var nlIndex = path.IndexOf((byte)'\n');
            var line = nlIndex >= 0 ? path.Slice(0, nlIndex) : path;

            // Trim \r and spaces
            line = TrimBytes(line);

            if (line.Length > 0 && IsDangerousLine(line))
            {
                return true;
            }

            if (nlIndex < 0)
            {
                break;
            }

            path = path.Slice(nlIndex + 1);
        }

        return false;
    }

    private static bool IsDangerousLine(ReadOnlySpan<byte> line)
    {
        if (IsCurrentDirectoryPath(line))
        {
            return true;
        }

        if (IsParentDirectoryPath(line))
        {
            return true;
        }

        // ${{ github.workspace }} with optional trailing separators or /. suffixes
        return IsGitHubWorkspaceExpression(line);
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

    private static bool TryClassifyRelativeDirectoryPath(ReadOnlySpan<byte> path, out int dotDotSegments)
    {
        dotDotSegments = 0;

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
                dotDotSegments++;
                continue;
            }

            // Glob metacharacters can match arbitrary root content, including hidden files under .git.
            if (ContainsGlobMetacharacters(segment))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsGlobMetacharacters(ReadOnlySpan<byte> segment)
    {
        for (var i = 0; i < segment.Length; i++)
        {
            var b = segment[i];
            if (b is (byte)'*' or (byte)'?' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGitHubWorkspaceExpression(ReadOnlySpan<byte> value)
    {
        // Match ${{ github.workspace }} with variable internal whitespace and optional trailing
        // separator, /., or /.. suffixes. All of these resolve to the workspace or its parent.
        var trimmed = value;

        while (true)
        {
            if (trimmed.Length > 0 && (trimmed[^1] == (byte)'/' || trimmed[^1] == (byte)'\\'))
            {
                trimmed = trimmed[..^1];
                continue;
            }

            if (trimmed.Length >= 2 && trimmed[^1] == (byte)'.' && (trimmed[^2] == (byte)'/' || trimmed[^2] == (byte)'\\'))
            {
                trimmed = trimmed[..^2];
                continue;
            }

            // Strip /.. or \.. (parent directory traversal)
            if (trimmed.Length >= 3 && trimmed[^1] == (byte)'.' && trimmed[^2] == (byte)'.' && (trimmed[^3] == (byte)'/' || trimmed[^3] == (byte)'\\'))
            {
                trimmed = trimmed[..^3];
                continue;
            }

            break;
        }

        // Must start with "${{" and end with "}}"
        if (trimmed.Length < 5)
        {
            return false;
        }

        if (trimmed[0] != (byte)'$' || trimmed[1] != (byte)'{' || trimmed[2] != (byte)'{')
        {
            return false;
        }

        if (trimmed[^1] != (byte)'}' || trimmed[^2] != (byte)'}')
        {
            return false;
        }

        // Extract inner content between ${{ and }}
        var inner = trimmed.Slice(3, trimmed.Length - 5);

        // Trim whitespace
        inner = TrimBytes(inner);

        // Should be "github.workspace"
        return inner.SequenceEqual("github.workspace"u8);
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

    /// <summary>Detects checkout v6+ from a strict semantic version ref (e.g. <c>actions/checkout@v6</c>, <c>@v6.1</c>).
    /// Arbitrary refs like <c>@v6-legacy</c> are not treated as v6+.</summary>
    internal static bool IsV6OrLater(ReadOnlySpan<byte> usesText)
    {
        return TryExtractMajorAndMinorVersion(usesText, out var major, out _, out _) && major >= 6;
    }

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
            return $"upload-artifact with path '{uploadPath}' may expose credentials; checkout v6+ stores credentials in $RUNNER_TEMP but persist-credentials: false is still recommended";
        }

        return $"upload-artifact with path '{uploadPath}' may expose credentials persisted by checkout in .git/config; set persist-credentials: false on the checkout step";
    }
}
