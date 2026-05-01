namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// A name/ref wildcard pair that identifies GitHub Actions references to skip during SHA resolution.
/// Uses simple wildcard matching (<c>*</c> matches any sequence, <c>?</c> matches single char). No regex.
/// </summary>
public sealed record IgnoreActionEntry(
    /// <summary>Wildcard pattern matched against "owner/repo" or "owner/repo/.github/workflows/file.yml".</summary>
    string NamePattern,
    /// <summary>Wildcard pattern matched against the ref portion (tag, branch, or SHA).</summary>
    string RefPattern);
