namespace Seiton.Output;

using System.Text.Json.Serialization;

/// <summary>
/// Resolves internal file paths to user-facing relative display paths and SARIF artifact locations.
/// </summary>
internal sealed class PathDisplayResolver
{
    internal const string UnknownSarifFileUri = "file:///unknown";
    public const string SarifWorkingDirectoryBaseId = "%WORKING_DIR%";

    private readonly string _baseDirectory;
    private readonly string? _sarifBaseUri;
    private readonly Dictionary<string, string> _displayCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SarifArtifactLocation> _sarifCache = new(StringComparer.Ordinal);
    private bool _hasRelativeSarifArtifacts;
    private Dictionary<string, SarifArtifactLocation>? _originalUriBaseIdsCache;

    public PathDisplayResolver(string? baseDirectory = null)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory ?? Environment.CurrentDirectory);
        _sarifBaseUri = BuildSarifBaseUri(_baseDirectory);
    }

    public string? SarifBaseUri => _sarifBaseUri;

    public string GetDisplayPath(string? filePath)
    {
        if (filePath is null)
            return "<unknown>";

        if (IsPassthroughPath(filePath))
            return filePath;

        if (_displayCache.TryGetValue(filePath, out var cached))
            return cached;

        var display = ResolveDisplayPath(filePath);
        _displayCache[filePath] = display;
        return display;
    }

    public SarifArtifactLocation ResolveSarifArtifactLocation(string? filePath)
    {
        if (filePath is not null && _sarifCache.TryGetValue(filePath, out var cached))
            return cached;

        var resolved = ResolveSarifArtifactLocationCore(filePath);
        if (filePath is not null)
            _sarifCache[filePath] = resolved;

        return resolved;
    }

    private SarifArtifactLocation ResolveSarifArtifactLocationCore(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.Equals(filePath, "<unknown>", StringComparison.Ordinal))
            return new SarifArtifactLocation { Uri = UnknownSarifFileUri };

        if (IsPassthroughPath(filePath))
        {
            var passthroughUri = IsSafeRelativeUriPath(filePath)
                ? filePath
                : EncodeRelativePathForUri(filePath);
            return CreateRelativeSarifLocation(passthroughUri);
        }

        if (LooksLikeAbsoluteUri(filePath) && Uri.TryCreate(filePath, UriKind.Absolute, out var absoluteUri))
            return new SarifArtifactLocation { Uri = absoluteUri.AbsoluteUri };

        var fullPath = Path.GetFullPath(filePath);
        var relative = Path.GetRelativePath(_baseDirectory, fullPath);
        if (RequiresAbsoluteFallback(relative))
            return new SarifArtifactLocation { Uri = new Uri(fullPath, UriKind.Absolute).AbsoluteUri };

        var relativeUri = ToSarifRelativeUri(relative);
        return CreateRelativeSarifLocation(relativeUri);
    }

    public Dictionary<string, SarifArtifactLocation>? CreateOriginalUriBaseIds()
    {
        if (_sarifBaseUri is null || !_hasRelativeSarifArtifacts)
            return null;

        _originalUriBaseIdsCache ??= new Dictionary<string, SarifArtifactLocation>(1, StringComparer.Ordinal)
        {
            [SarifWorkingDirectoryBaseId] = new SarifArtifactLocation { Uri = _sarifBaseUri },
        };
        return _originalUriBaseIdsCache;
    }

    private SarifArtifactLocation CreateRelativeSarifLocation(string relativeUri)
    {
        if (_sarifBaseUri is null)
            return new SarifArtifactLocation { Uri = relativeUri };

        _hasRelativeSarifArtifacts = true;
        return new SarifArtifactLocation
        {
            Uri = relativeUri,
            UriBaseId = SarifWorkingDirectoryBaseId,
        };
    }

    private string ResolveDisplayPath(string filePath)
    {
        if (LooksLikeAbsoluteUri(filePath))
            return filePath;

        if (!Path.IsPathRooted(filePath) && !LooksLikeWindowsDrivePath(filePath))
            return NormalizeToForwardSlashes(filePath);

        var fullPath = Path.GetFullPath(filePath);
        var relative = Path.GetRelativePath(_baseDirectory, fullPath);
        if (RequiresAbsoluteFallback(relative))
            return NormalizeToForwardSlashes(fullPath);

        return NormalizeToForwardSlashes(relative);
    }

    private static bool IsPassthroughPath(string filePath) =>
        string.Equals(filePath, "<stdin>", StringComparison.Ordinal)
        || string.Equals(filePath, "-", StringComparison.Ordinal);

    private static bool RequiresAbsoluteFallback(string relativePath) =>
        Path.IsPathFullyQualified(relativePath) || LooksLikeWindowsDrivePath(relativePath);

    private static string ToSarifRelativeUri(string relativePath)
    {
        var normalized = NormalizeToForwardSlashes(relativePath);
        return IsSafeRelativeUriPath(normalized)
            ? normalized
            : EncodeRelativePathForUri(normalized);
    }

    private static string NormalizeToForwardSlashes(string path)
    {
        return path.AsSpan().IndexOf('\\') >= 0
            ? path.Replace('\\', '/')
            : path;
    }

    private static string? BuildSarifBaseUri(string baseDirectory)
    {
        var normalized = Path.GetFullPath(baseDirectory);
        if (!normalized.EndsWith(Path.DirectorySeparatorChar) && !normalized.EndsWith(Path.AltDirectorySeparatorChar))
            normalized += Path.DirectorySeparatorChar;

        return new Uri(normalized, UriKind.Absolute).AbsoluteUri;
    }

    internal static bool LooksLikeWindowsDrivePath(string path)
    {
        if (path.Length < 3)
            return false;

        return char.IsLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');
    }

    internal static string EncodeRelativePathForUri(string path)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0 || segment == "." || segment == "..")
                continue;

            segments[i] = Uri.EscapeDataString(segment);
        }

        return string.Join('/', segments);
    }

    internal static bool IsSafeRelativeUriPath(string path)
    {
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c is >= 'a' and <= 'z')
                continue;
            if (c is >= 'A' and <= 'Z')
                continue;
            if (c is >= '0' and <= '9')
                continue;

            switch (c)
            {
                case '/':
                case '.':
                case '-':
                case '_':
                case '~':
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    internal static bool LooksLikeAbsoluteUri(string path)
    {
        var colonIndex = path.IndexOf(':');
        if (colonIndex <= 1)
            return false;

        for (var i = 0; i < colonIndex; i++)
        {
            var c = path[i];
            if (c is >= 'a' and <= 'z')
                continue;
            if (c is >= 'A' and <= 'Z')
                continue;
            if (c is >= '0' and <= '9')
                continue;
            if (c is '+' or '-' or '.')
                continue;

            return false;
        }

        return true;
    }
}

internal sealed record SarifArtifactLocation
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("uriBaseId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UriBaseId { get; init; }
}
