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
            if (!PopularActions.TryGet(usesText, out var actionSpec)
                || actionSpec.Id != PopularActions.ActionId.ActionsCheckout
                || HasPersistCredentialsFalse(actionExec, utf8Yaml))
            {
                continue;
            }

            if (IsV6OrLater(usesText))
            {
                hasUnsafeV6PlusCheckout = true;
                continue;
            }

            hasUnsafeLegacyCheckout = true;
            break;
        }

        if (!hasUnsafeLegacyCheckout && !hasUnsafeV6PlusCheckout)
        {
            return;
        }

        var reportAsWarning = !hasUnsafeLegacyCheckout && hasUnsafeV6PlusCheckout;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Exec is not ExecAction actionExec)
            {
                continue;
            }

            var usesText = Arena.GetStringValue(actionExec.Uses);
            if (!PopularActions.TryGet(usesText, out var actionSpec)
                || actionSpec.Id != PopularActions.ActionId.ActionsUploadArtifact
                || actionExec.Inputs is null
                || !actionExec.Inputs.Value.TryGetValue(utf8Yaml, "path"u8, out var pathNode)
                || !IsDangerousPath(Arena.GetStringValue(pathNode)))
            {
                continue;
            }

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
               && value.SequenceEqual("false"u8);
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

    private static bool IsCurrentDirectoryPath(ReadOnlySpan<byte> path)
    {
        var dotDotSegments = 0;
        return TryClassifyRelativeDirectoryPath(path, out dotDotSegments) && dotDotSegments == 0;
    }

    private static bool IsParentDirectoryPath(ReadOnlySpan<byte> path)
    {
        var dotDotSegments = 0;
        return TryClassifyRelativeDirectoryPath(path, out dotDotSegments) && dotDotSegments == 1;
    }

    private static bool TryClassifyRelativeDirectoryPath(ReadOnlySpan<byte> path, out int dotDotSegments)
    {
        dotDotSegments = 0;

        while (path.Length > 0)
        {
            var separatorIndex = path.IndexOf((byte)'/');
            var segment = separatorIndex >= 0 ? path[..separatorIndex] : path;
            path = separatorIndex >= 0 ? path[(separatorIndex + 1)..] : ReadOnlySpan<byte>.Empty;

            if (segment.Length == 0 || segment.SequenceEqual("."u8))
            {
                continue;
            }

            if (segment.SequenceEqual(".."u8))
            {
                dotDotSegments++;
                if (dotDotSegments > 1)
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsGitHubWorkspaceExpression(ReadOnlySpan<byte> value)
    {
        // Match ${{ github.workspace }} with variable internal whitespace and optional trailing / or /. segments.
        var trimmed = value;

        while (true)
        {
            if (trimmed.Length > 0 && trimmed[^1] == (byte)'/')
            {
                trimmed = trimmed[..^1];
                continue;
            }

            if (trimmed.Length >= 2 && trimmed[^1] == (byte)'.' && trimmed[^2] == (byte)'/')
            {
                trimmed = trimmed[..^2];
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

    /// <summary>Detects checkout v6+ from a semantic version ref (e.g. <c>actions/checkout@v6</c>).</summary>
    internal static bool IsV6OrLater(ReadOnlySpan<byte> usesText)
    {
        return OutdatedActionRunnerRule.TryExtractMajorVersion(usesText, out var major) && major >= 6;
    }

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

    private string BuildMessage(Utf8Slice pathSlice, bool isV6Plus)
    {
        var uploadPath = Decode(pathSlice);
        if (isV6Plus)
        {
            return $"upload-artifact with path '{uploadPath}' may expose credentials; checkout v6+ stores credentials in $RUNNER_TEMP but persist-credentials: false is still recommended";
        }

        return $"upload-artifact with path '{uploadPath}' may expose credentials persisted by checkout in .git/config; set persist-credentials: false on the checkout step";
    }
}
