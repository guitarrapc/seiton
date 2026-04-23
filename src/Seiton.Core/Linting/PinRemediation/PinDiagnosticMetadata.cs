using System.Collections.Concurrent;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.PinRemediation;

/// <summary>Structured keys for pin-related diagnostics (<c>unpinned-uses</c>, <c>unpinned-image</c>).</summary>
public static class PinDiagnosticMetadata
{
    public const string UsesRefKey = "uses-ref";

    public const string ImageRefKey = "image-ref";

    /// <summary>
    /// Dedupes metadata objects for repeated identical refs (common in workflows). Thread-safe; unbounded per process.
    /// </summary>
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> UsesRefCache =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> ImageRefCache =
        new(StringComparer.Ordinal);

    /// <summary>Creates or retrieves a cached metadata dictionary for the given <c>uses</c> reference string.</summary>
    public static IReadOnlyDictionary<string, string> ForUsesRef(string usesRef) =>
        UsesRefCache.GetOrAdd(usesRef, static ur => new PinSingleEntryReadOnlyDictionary(UsesRefKey, ur));

    /// <summary>Creates or retrieves a cached metadata dictionary for the given image reference string.</summary>
    public static IReadOnlyDictionary<string, string> ForImageRef(string imageRef) =>
        ImageRefCache.GetOrAdd(imageRef, static ir => new PinSingleEntryReadOnlyDictionary(ImageRefKey, ir));

    /// <summary>Extracts the <c>uses-ref</c> value from a diagnostic's metadata.</summary>
    public static bool TryGetUsesRef(in Diagnostic diagnostic, out string usesRef) =>
        TryGet(diagnostic.Metadata, UsesRefKey, out usesRef);

    /// <summary>Extracts the <c>image-ref</c> value from a diagnostic's metadata.</summary>
    public static bool TryGetImageRef(in Diagnostic diagnostic, out string imageRef) =>
        TryGet(diagnostic.Metadata, ImageRefKey, out imageRef);

    private static bool TryGet(IReadOnlyDictionary<string, string>? metadata, string key, out string value)
    {
        if (metadata is not null && metadata.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
        {
            value = v;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
