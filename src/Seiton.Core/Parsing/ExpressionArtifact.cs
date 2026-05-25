namespace Seiton.Core.Parsing;

/// <summary>
/// A pre-parsed expression artifact produced by the parser during YAML parsing.
/// Pairs an expression occurrence location with its cached parse result so downstream
/// consumers (linter, custom rules) can avoid re-parsing.
/// </summary>
internal readonly record struct ExpressionArtifact(
    long ContentHash,
    TextRange Location,
    ExpressionValidationContext Context,
    ExpressionParseResult ParseResult);

/// <summary>
/// Stores pre-parsed expression artifacts produced by the parser.
/// Keyed by content hash (xxHash64) with the same algorithm used by <see cref="Linting.LintConfig"/>.
/// </summary>
/// <remarks>
/// This store is opt-in: the parser only populates it when expression artifacts are requested.
/// When not populated, linter falls back to its existing content-hash cache.
/// The store is immutable after parsing completes and safe to share across rules.
/// </remarks>
internal sealed class ExpressionArtifactStore
{
    private readonly Dictionary<long, ExpressionArtifact> _artifacts;

    internal ExpressionArtifactStore(int capacity)
    {
        _artifacts = new Dictionary<long, ExpressionArtifact>(capacity);
    }

    internal int Count => _artifacts.Count;

    internal void Add(long contentHash, ExpressionArtifact artifact)
    {
        // First occurrence wins (same expression body may appear multiple times)
        _artifacts.TryAdd(contentHash, artifact);
    }

    /// <summary>
    /// Attempts to retrieve a pre-parsed expression result by content hash.
    /// Returns <c>true</c> if found and the stored bytes match <paramref name="expression"/>.
    /// </summary>
    internal bool TryGet(long contentHash, ReadOnlySpan<byte> expression, byte[] source, out ExpressionParseResult result)
    {
        if (_artifacts.TryGetValue(contentHash, out var artifact))
        {
            // Bounds guard: verify the stored location is within source
            var start = artifact.Location.Start;
            var length = artifact.Location.Length;
            if ((uint)start + (uint)length > (uint)source.Length)
            {
                result = default;
                return false;
            }

            // Collision guard: verify the expression bytes match
            var storedSpan = source.AsSpan(start, length);
            if (expression.SequenceEqual(storedSpan))
            {
                result = artifact.ParseResult;
                return true;
            }
        }

        result = default;
        return false;
    }
}
