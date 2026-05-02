using System.Runtime.CompilerServices;
using Seiton.Core.Parsing;

namespace Seiton.Playground;

/// <summary>Identifies a root-level workflow section for incremental parsing.</summary>
public enum RootSectionKind
{
    Name = 0,
    RunName = 1,
    On = 2,
    Jobs = 3,
    Env = 4,
    Permissions = 5,
    Defaults = 6,
    Concurrency = 7,
}

/// <summary>
/// A recorded byte range and content hash for a YAML section.
/// Used by <see cref="IncrementalParseContext"/> to detect unchanged sections between edits.
/// </summary>
public readonly struct SectionEntry
{
    /// <summary>Byte offset where this section's content starts (value start, after key + colon + space).</summary>
    public readonly int StartOffset;

    /// <summary>Byte offset just past this section's content end (exclusive).</summary>
    public readonly int EndOffset;

    /// <summary>XXH64 hash of <c>source[StartOffset..EndOffset]</c>.</summary>
    public readonly long ContentHash;

    /// <summary>Whether the parser produced diagnostics for this section (always re-parse if true).</summary>
    public readonly bool HasDiagnostics;

    public SectionEntry(int startOffset, int endOffset, long contentHash, bool hasDiagnostics = false)
    {
        StartOffset = startOffset;
        EndOffset = endOffset;
        ContentHash = contentHash;
        HasDiagnostics = hasDiagnostics;
    }

    /// <summary>Whether this entry was recorded (offset != 0 or hash != 0 indicates a real entry).</summary>
    public bool IsValid => EndOffset > StartOffset;
}

/// <summary>
/// Records byte ranges and content hashes for all root sections and per-job sections.
/// All fields are value-type; no heap allocation beyond the job entries array (reused across calls).
/// </summary>
public struct SectionRegistry
{
    // Root sections indexed by RootSectionKind ordinal (inline for zero-alloc)
    private RootSectionBuffer _rootSections;
    private int _rootCount;

    // Per-job entries (allocated once, grown only when job count increases)
    private SectionEntry[]? _jobEntries;
    private int _jobCount;

    /// <summary>Number of jobs recorded.</summary>
    public readonly int JobCount => _jobCount;

    /// <summary>Gets the root section entry for the given kind. Returns default if not recorded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly SectionEntry GetRootSection(RootSectionKind kind)
    {
        var index = (int)kind;
        return (uint)index < 8 ? _rootSections[index] : default;
    }

    /// <summary>Gets the job entry at the given index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly SectionEntry GetJobEntry(int index)
    {
        if (_jobEntries is null || (uint)index >= (uint)_jobCount)
            return default;
        return _jobEntries[index];
    }

    /// <summary>Gets all job entries as a span.</summary>
    public readonly ReadOnlySpan<SectionEntry> JobEntries =>
        _jobEntries is null ? [] : _jobEntries.AsSpan(0, _jobCount);

    internal void SetRootSection(RootSectionKind kind, SectionEntry entry)
    {
        _rootSections[(int)kind] = entry;
        _rootCount = Math.Max(_rootCount, (int)kind + 1);
    }

    internal void SetJobEntries(int count)
    {
        if (_jobEntries is null || _jobEntries.Length < count)
        {
            _jobEntries = new SectionEntry[Math.Max(count, 8)];
        }
        _jobCount = count;
    }

    internal void SetJobEntry(int index, SectionEntry entry)
    {
        _jobEntries![index] = entry;
    }

    [InlineArray(8)]
    private struct RootSectionBuffer
    {
        private SectionEntry _element;
    }
}

/// <summary>
/// Represents an edit region between two source byte arrays.
/// Computed by prefix/suffix matching to identify the minimal changed range.
/// </summary>
public readonly struct EditRegion
{
    /// <summary>Byte offset where the edit starts (common prefix length).</summary>
    public readonly int Start;

    /// <summary>Byte offset where the edit ends in the NEW source (exclusive).</summary>
    public readonly int End;

    /// <summary>Length difference: newSource.Length - oldSource.Length.</summary>
    public readonly int Delta;

    public EditRegion(int start, int end, int delta)
    {
        Start = start;
        End = end;
        Delta = delta;
    }
}

/// <summary>
/// Playground-specific incremental parse context. Records section byte ranges and hashes
/// after each parse to enable future incremental parsing (D-5b/c).
/// <para>
/// In D-5a this is infrastructure only: records the registry and provides edit-region
/// detection, but does not yet skip any parsing.
/// </para>
/// </summary>
public sealed class IncrementalParseContext
{
    private byte[]? _previousSource;
    private SectionRegistry _registry;

    /// <summary>Whether a previous parse result has been recorded.</summary>
    public bool HasPrevious => _previousSource is not null;

    /// <summary>The current section registry (valid only when <see cref="HasPrevious"/> is true).</summary>
    public ref readonly SectionRegistry Registry => ref _registry;

    /// <summary>
    /// Records section byte ranges and hashes from the given parsed source.
    /// Call this after each successful <see cref="Core.Linting.LintEngine.Check"/> call.
    /// </summary>
    public void UpdateAfterParse(byte[] utf8Yaml, string filePath)
    {
        var result = Core.Parsing.WorkflowParser.ParseClassified(utf8Yaml, filePath);
        try
        {
            BuildRegistry(utf8Yaml, result.ParseResult);
            _previousSource = utf8Yaml;
        }
        finally
        {
            result.ParseResult.Arena?.Dispose();
        }
    }

    /// <summary>
    /// Detects the minimal edit region between the previous source and <paramref name="newSource"/>.
    /// Returns a full-range edit if no previous source exists.
    /// </summary>
    public EditRegion DetectEditRegion(byte[] newSource)
    {
        if (_previousSource is null)
        {
            return new EditRegion(0, newSource.Length, newSource.Length);
        }

        return ComputeEditRegion(_previousSource, newSource);
    }

    /// <summary>
    /// Checks whether a given section entry's bytes are unchanged in the new source.
    /// Compares the XXH64 hash of the corresponding byte range in <paramref name="newSource"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSectionUnchanged(SectionEntry entry, byte[] newSource)
    {
        if (!entry.IsValid || entry.HasDiagnostics)
            return false;

        // If the section extends beyond new source bounds, it's definitely changed
        if (entry.EndOffset > newSource.Length)
            return false;

        var span = newSource.AsSpan(entry.StartOffset, entry.EndOffset - entry.StartOffset);
        var newHash = ComputeHash(span);
        return newHash == entry.ContentHash;
    }

    private void BuildRegistry(byte[] source, ParseResult parseResult)
    {
        _registry = default;

        if (parseResult.Workflow is null)
            return;

        // Use a lightweight root-key scan to find section boundaries
        // This avoids modifying the parser: we scan for indent-0 keys in the YAML source
        ScanRootSections(source, ref _registry);

        // Record per-job entries by scanning for indent-2 keys within the jobs section
        var jobsEntry = _registry.GetRootSection(RootSectionKind.Jobs);
        if (jobsEntry.IsValid)
        {
            ScanJobSections(source, jobsEntry.StartOffset, jobsEntry.EndOffset, ref _registry);
        }
    }

    /// <summary>
    /// Scans source bytes to find root-level YAML mapping keys and their value ranges.
    /// A root key is a line at indent 0 matching a known workflow key followed by ':'.
    /// This is a fast O(n) byte scan — no YAML tokenization.
    /// </summary>
    private static void ScanRootSections(byte[] source, ref SectionRegistry registry)
    {
        var span = source.AsSpan();
        var length = span.Length;

        // Collect all root key positions (key start, value start) in parse order
        Span<(RootSectionKind Kind, int KeyOffset, int ValueStart)> found = stackalloc (RootSectionKind, int, int)[8];
        var foundCount = 0;

        var i = 0;
        while (i < length && foundCount < 8)
        {
            // Skip lines that start with whitespace or comments (not root level)
            if (i > 0 && span[i - 1] != (byte)'\n')
            {
                // Not at line start — advance to next line
                i = NextLineStart(span, i);
                continue;
            }

            // At line start (or document start for i==0)
            if (span[i] is (byte)' ' or (byte)'\t' or (byte)'#' or (byte)'\n' or (byte)'\r')
            {
                i = NextLineStart(span, i);
                continue;
            }

            // Try to match a known root key
            var keyStart = i;
            var matched = TryMatchRootKey(span, i, out var kind, out var colonEnd);
            if (matched)
            {
                // Value starts after colon + optional space
                var valueStart = colonEnd;
                if (valueStart < length && span[valueStart] == (byte)' ')
                    valueStart++;
                found[foundCount++] = (kind, keyStart, valueStart);
            }

            i = NextLineStart(span, i);
        }

        // Compute end offsets: each section ends where the next root key starts (or EOF)
        for (var j = 0; j < foundCount; j++)
        {
            var (kind, _, valueStart) = found[j];
            var endOffset = (j + 1 < foundCount) ? found[j + 1].KeyOffset : length;
            var sectionSpan = source.AsSpan(valueStart, endOffset - valueStart);
            var hash = ComputeHash(sectionSpan);
            registry.SetRootSection(kind, new SectionEntry(valueStart, endOffset, hash));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NextLineStart(ReadOnlySpan<byte> span, int offset)
    {
        var idx = span[offset..].IndexOf((byte)'\n');
        return idx < 0 ? span.Length : offset + idx + 1;
    }

    /// <summary>
    /// Scans the jobs section for indent-2 job ID keys to determine per-job byte ranges.
    /// A job key is a line starting with exactly 2 spaces followed by an identifier and ':'.
    /// </summary>
    private static void ScanJobSections(byte[] source, int jobsSectionStart, int jobsSectionEnd, ref SectionRegistry registry)
    {
        var span = source.AsSpan();
        // Collect job key offsets (line start positions of indent-2 keys)
        Span<int> jobKeyOffsets = stackalloc int[64]; // max 64 jobs
        var jobCount = 0;

        var i = jobsSectionStart;
        while (i < jobsSectionEnd && jobCount < 64)
        {
            // Find next line start
            if (i > jobsSectionStart && span[i - 1] != (byte)'\n')
            {
                i = NextLineStart(span, i);
                if (i >= jobsSectionEnd) break;
                continue;
            }

            // Check for exactly 2 spaces followed by a non-space, non-# character (job key)
            if (i + 2 < jobsSectionEnd &&
                span[i] == (byte)' ' &&
                span[i + 1] == (byte)' ' &&
                span[i + 2] is not ((byte)' ' or (byte)'#' or (byte)'\n' or (byte)'\r' or (byte)'-'))
            {
                // Verify it contains a colon (it's a mapping key, not a continuation)
                var lineEnd = NextLineStart(span, i);
                var lineSpan = span[i..Math.Min(lineEnd, jobsSectionEnd)];
                if (lineSpan.Contains((byte)':'))
                {
                    jobKeyOffsets[jobCount++] = i;
                }
            }

            i = NextLineStart(span, i);
        }

        registry.SetJobEntries(jobCount);
        for (var j = 0; j < jobCount; j++)
        {
            var keyOffset = jobKeyOffsets[j];
            var endOffset = (j + 1 < jobCount) ? jobKeyOffsets[j + 1] : jobsSectionEnd;
            var sectionSpan = source.AsSpan(keyOffset, endOffset - keyOffset);
            var hash = ComputeHash(sectionSpan);
            registry.SetJobEntry(j, new SectionEntry(keyOffset, endOffset, hash));
        }
    }

    private static bool TryMatchRootKey(ReadOnlySpan<byte> span, int offset, out RootSectionKind kind, out int colonEnd)
    {
        kind = default;
        colonEnd = 0;

        var remaining = span[offset..];

        // Try each known root key (ordered by frequency/likelihood)
        if (TryMatchKey(remaining, "on:"u8, RootSectionKind.On, out kind, out var keyLen) ||
            TryMatchKey(remaining, "jobs:"u8, RootSectionKind.Jobs, out kind, out keyLen) ||
            TryMatchKey(remaining, "name:"u8, RootSectionKind.Name, out kind, out keyLen) ||
            TryMatchKey(remaining, "run-name:"u8, RootSectionKind.RunName, out kind, out keyLen) ||
            TryMatchKey(remaining, "env:"u8, RootSectionKind.Env, out kind, out keyLen) ||
            TryMatchKey(remaining, "permissions:"u8, RootSectionKind.Permissions, out kind, out keyLen) ||
            TryMatchKey(remaining, "defaults:"u8, RootSectionKind.Defaults, out kind, out keyLen) ||
            TryMatchKey(remaining, "concurrency:"u8, RootSectionKind.Concurrency, out kind, out keyLen))
        {
            colonEnd = offset + keyLen;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryMatchKey(ReadOnlySpan<byte> line, ReadOnlySpan<byte> keyWithColon, RootSectionKind expectedKind, out RootSectionKind kind, out int keyLen)
    {
        if (line.Length >= keyWithColon.Length && line[..keyWithColon.Length].SequenceEqual(keyWithColon))
        {
            kind = expectedKind;
            keyLen = keyWithColon.Length;
            return true;
        }
        kind = default;
        keyLen = 0;
        return false;
    }

    private static EditRegion ComputeEditRegion(byte[] oldSource, byte[] newSource)
    {
        var oldSpan = oldSource.AsSpan();
        var newSpan = newSource.AsSpan();
        var oldLen = oldSpan.Length;
        var newLen = newSpan.Length;

        // Find common prefix
        var minLen = Math.Min(oldLen, newLen);
        var prefixLen = 0;
        for (var i = 0; i < minLen; i++)
        {
            if (oldSpan[i] != newSpan[i]) break;
            prefixLen++;
        }

        // Find common suffix (not overlapping with prefix)
        var suffixLen = 0;
        var maxSuffix = minLen - prefixLen;
        for (var i = 0; i < maxSuffix; i++)
        {
            if (oldSpan[oldLen - 1 - i] != newSpan[newLen - 1 - i]) break;
            suffixLen++;
        }

        var editStart = prefixLen;
        var editEnd = newLen - suffixLen;
        var delta = newLen - oldLen;

        return new EditRegion(editStart, editEnd, delta);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeHash(ReadOnlySpan<byte> data)
    {
        return (long)XxHash64.Hash(data);
    }
}
