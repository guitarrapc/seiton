namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// A name/ref regex pair that identifies GitHub Actions references to skip during SHA resolution.
/// Equivalent to pinact's ignore_actions entries.
/// </summary>
public sealed record IgnoreActionEntry(
    /// <summary>Regex pattern matched against "owner/repo" or "owner/repo/.github/workflows/file.yml".</summary>
    string NamePattern,
    /// <summary>Regex pattern matched against the ref portion (tag, branch, or SHA).</summary>
    string RefPattern);
