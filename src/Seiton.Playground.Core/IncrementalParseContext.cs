using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

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

    /// <summary>Resets counts while preserving the allocated <c>_jobEntries</c> buffer for reuse.</summary>
    internal void Reset()
    {
        _rootSections = default;
        _rootCount = 0;
        _jobCount = 0;
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
/// after each parse, and provides incremental parsing that skips unchanged root sections (D-5b).
/// </summary>
public sealed class IncrementalParseContext
{
    private byte[]? _previousSource;
    private int _previousSourceLength;
    private SectionRegistry _registry;
    private SectionRegistry _scanRegistry;

    // D-5b: stored previous Workflow and Arena for section reuse
    private Workflow? _previousWorkflow;
    private AstArena? _previousArena;

    // D-5c: arenas retained because reused jobs reference their pooled objects.
    // Disposed only on full parse (when all jobs are freshly allocated).
    private List<AstArena>? _retainedArenas;

    // Base entry counts from the last full parse (cap for BulkImport to prevent growth)
    private int _baseStringCount;
    private int _baseBoolCount;
    private int _baseIntCount;
    private int _baseFloatCount;

    // Tracks whether the previous incremental parse had no job reuse (root-skip-only).
    // Used to detect the transition to job-reuse, which requires a full parse
    // to reset base counts and prevent stale index crashes.
    private bool _previousHadNoJobReuse;

    // D-5c: reusable buffer for job skip entries (avoids per-call allocation)
    private JobSkipEntry[]? _jobSkipEntriesBuf;

    // D-5d: per-job diagnostic cache from previous lint run
    private Diagnostic[]?[]? _cachedJobDiagnostics;
    private bool[]? _lastReusedJobs;

    /// <summary>Whether a previous parse result has been recorded.</summary>
    public bool HasPrevious => _previousSource is not null;

    /// <summary>The current section registry (valid only when <see cref="HasPrevious"/> is true).</summary>
    public ref readonly SectionRegistry Registry => ref _registry;

    /// <summary>
    /// Parses the given YAML incrementally, skipping unchanged root sections (D-5b) and
    /// unchanged individual jobs (D-5c), reusing previous AST nodes for them.
    /// On first call (no previous data), performs a full parse.
    /// The returned <see cref="ParseResult"/> is owned by this context — callers must NOT
    /// dispose the Arena (the context manages arena lifecycle).
    /// </summary>
    public ParseResult ParseIncrementally(byte[] utf8Yaml, string filePath)
    {
        if (_previousSource is null || _previousWorkflow is null || _previousArena is null)
        {
            // First call: full parse, store results
            return FullParseAndStore(utf8Yaml, filePath);
        }

        // Fast path: if source bytes are identical, return the previous parse result directly.
        // This avoids re-parsing, arena allocation, and VYaml tokenization entirely.
        if (IsSourceIdentical(utf8Yaml))
        {
            return new ParseResult(_previousWorkflow, null, default, HasFatalError: false, _previousArena);
        }

        // Scan new source for section boundaries
        _scanRegistry.Reset();
        ScanRootSections(utf8Yaml, ref _scanRegistry);
        var newJobsEntry = _scanRegistry.GetRootSection(RootSectionKind.Jobs);
        if (newJobsEntry.IsValid)
        {
            ScanJobSections(utf8Yaml, newJobsEntry.StartOffset, newJobsEntry.EndOffset, ref _scanRegistry);
        }

        // Determine which root sections are unchanged (D-5b).
        // Returns 0 if ANY existing root section changed (forces full parse for root sections
        // to avoid arena entry growth from partial imports).
        var skipMask = ComputeSkipMask(utf8Yaml, ref _scanRegistry);

        // D-5c: Compute job skip entries (independent of root skip mask).
        // Even if root sections changed, individual jobs that are byte-identical can be reused.
        var jobSkipEntries = ComputeJobSkipEntries(utf8Yaml, ref _scanRegistry);

        if (skipMask == 0 && jobSkipEntries is null)
        {
            // Nothing to skip — full parse (resets base counts)
            _lastReusedJobs = null;
            _previousHadNoJobReuse = false;
            return FullParseAndStore(utf8Yaml, filePath);
        }

        // If previous iteration had no job reuse (all jobs parsed fresh with indices > base)
        // and THIS iteration wants to reuse jobs, fall back to full parse. Those reused jobs
        // reference string indices that BulkImportFrom (capped at _baseStringCount) won't cover.
        if (_previousHadNoJobReuse && jobSkipEntries is not null)
        {
            _lastReusedJobs = null;
            _previousHadNoJobReuse = false;
            return FullParseAndStore(utf8Yaml, filePath);
        }

        // Incremental parse: import base entries only (capped to prevent growth),
        // parse with skip, patch results
        var arena = AstArena.Rent(utf8Yaml);
        arena.BulkImportFrom(_previousArena, _baseStringCount, _baseBoolCount, _baseIntCount, _baseFloatCount);

        var parseResult = WorkflowParser.ParseIncremental(utf8Yaml, filePath, arena, skipMask, jobSkipEntries);

        if (parseResult.HasFatalError || parseResult.Workflow is null)
        {
            // Incremental parse failed — discard and do full parse
            arena.Dispose();
            return FullParseAndStore(utf8Yaml, filePath);
        }

        // Patch skipped root sections from previous Workflow
        if (skipMask != 0)
        {
            PatchSkippedSections(parseResult.Workflow, skipMask);
        }

        // Update stored state (base counts stay the same — they only reset on full parse)
        var oldArena = _previousArena;
        _previousSource = utf8Yaml;
        _previousSourceLength = utf8Yaml.Length;
        _previousWorkflow = parseResult.Workflow;
        _previousArena = arena;
        (_registry, _scanRegistry) = (_scanRegistry, _registry);

        // Mark root sections that produced parse diagnostics so they won't be skipped next time
        MarkRootSectionsWithParseDiagnostics(parseResult.Diagnostics);

        if (jobSkipEntries is not null)
        {
            _previousHadNoJobReuse = false;

            // Release buffers no longer needed: diagnostics already consumed,
            // scalar data already copied via BulkImportFrom.
            oldArena?.ReleaseDiagnosticsBuffer();
            oldArena?.ReleaseLintDiagnosticsBuffer();
            oldArena?.ReleaseScalarBuffers();

            // Retain old arena — reused jobs reference its pooled Job/Step objects
            // and SliceMap Entry[] arrays. Will be disposed on next full parse.
            (_retainedArenas ??= new(2)).Add(oldArena!);

            // D-5d: record which jobs were reused for lint cache
            var jobCount = parseResult.Workflow!.Jobs.Count;
            if (_lastReusedJobs is null || _lastReusedJobs.Length < jobCount)
                _lastReusedJobs = new bool[jobCount];
            else
                Array.Clear(_lastReusedJobs, 0, _lastReusedJobs.Length);
            for (var i = 0; i < jobSkipEntries.Length && i < jobCount; i++)
                _lastReusedJobs[i] = jobSkipEntries[i].Job is not null;
        }
        else
        {
            _lastReusedJobs = null;
            _previousHadNoJobReuse = true;
            // No job reuse — safe to dispose old arena immediately
            oldArena?.Dispose();
        }

        return parseResult;
    }

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
        _registry.Reset();

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

    /// <summary>Builds registry from source bytes only (no parse needed). Used after incremental parse.</summary>
    private void BuildRegistryFromSource(byte[] source)
    {
        _registry.Reset();
        ScanRootSections(source, ref _registry);
        var jobsEntry = _registry.GetRootSection(RootSectionKind.Jobs);
        if (jobsEntry.IsValid)
        {
            ScanJobSections(source, jobsEntry.StartOffset, jobsEntry.EndOffset, ref _registry);
        }
    }

    /// <summary>Performs a full parse and stores the result for future incremental use.</summary>
    private ParseResult FullParseAndStore(byte[] utf8Yaml, string filePath)
    {
        // Full parse creates all-new objects — safe to dispose retained arenas FIRST
        // so their arrays return to pools and Rent() can reuse the ThreadStatic cache.
        if (_retainedArenas is { Count: > 0 })
        {
            foreach (var retained in _retainedArenas)
                retained.Dispose();
            _retainedArenas.Clear();
        }

        // Dispose old arena BEFORE ParseClassified so AstArena.Rent() can reuse the cache.
        _previousArena?.Dispose();

        var classifiedResult = WorkflowParser.ParseClassified(utf8Yaml, filePath);
        var parseResult = classifiedResult.ParseResult;

        _previousSource = utf8Yaml;
        _previousSourceLength = utf8Yaml.Length;
        _previousWorkflow = parseResult.Workflow;
        _previousArena = parseResult.Arena;
        BuildRegistryFromSource(utf8Yaml);

        // Mark root sections that produced parse diagnostics so they won't be skipped next time
        MarkRootSectionsWithParseDiagnostics(parseResult.Diagnostics);

        // Record base entry counts (the full parse's arena defines the import cap)
        if (parseResult.Arena is not null)
        {
            _baseStringCount = parseResult.Arena.StringCount;
            _baseBoolCount = parseResult.Arena.BoolCount;
            _baseIntCount = parseResult.Arena.IntCount;
            _baseFloatCount = parseResult.Arena.FloatCount;
        }

        // Full parse invalidates job cache — all diagnostics will be fresh
        _lastReusedJobs = null;

        return parseResult;
    }

    /// <summary>
    /// Computes a bitmask of root sections that can be skipped.
    /// Returns 0 if ANY existing root section has changed OR if the document structure
    /// changed (new sections appeared or disappeared), to prevent cross-document contamination.
    /// Jobs are never skipped (D-5b scope: root sections only).
    /// </summary>
    private byte ComputeSkipMask(byte[] newSource, ref SectionRegistry newRegistry)
    {
        byte mask = 0;
        var anyChanged = false;

        TryAddToMask(ref mask, ref anyChanged, RootSectionKind.On, ref newRegistry);
        TryAddToMask(ref mask, ref anyChanged, RootSectionKind.Env, ref newRegistry);
        TryAddToMask(ref mask, ref anyChanged, RootSectionKind.Permissions, ref newRegistry);
        TryAddToMask(ref mask, ref anyChanged, RootSectionKind.Defaults, ref newRegistry);
        TryAddToMask(ref mask, ref anyChanged, RootSectionKind.Concurrency, ref newRegistry);

        // If any existing root section changed, fall back to full parse
        // to avoid arena entry growth from partial imports
        return anyChanged ? (byte)0 : mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TryAddToMask(ref byte mask, ref bool anyChanged, RootSectionKind kind, ref SectionRegistry newRegistry)
    {
        var oldEntry = _registry.GetRootSection(kind);
        var newEntry = newRegistry.GetRootSection(kind);

        // Structural change: section exists in one but not the other
        if (oldEntry.IsValid != newEntry.IsValid)
        {
            anyChanged = true;
            return;
        }

        if (!oldEntry.IsValid) return; // section doesn't exist in either source

        // Compare offsets + hash directly (newRegistry already computed hashes from new source)
        if (oldEntry.StartOffset == newEntry.StartOffset &&
            oldEntry.EndOffset == newEntry.EndOffset &&
            oldEntry.ContentHash == newEntry.ContentHash &&
            !oldEntry.HasDiagnostics)
        {
            mask |= (byte)(1 << (int)kind);
        }
        else
        {
            anyChanged = true;
        }
    }

    /// <summary>
    /// Patches the skipped root section fields in the Workflow from the previous parse result.
    /// </summary>
    private void PatchSkippedSections(Workflow workflow, byte skipMask)
    {
        var prev = _previousWorkflow!;

        if ((skipMask & (1 << (int)RootSectionKind.On)) != 0)
        {
            workflow.On = prev.On;
        }

        if ((skipMask & (1 << (int)RootSectionKind.Env)) != 0)
        {
            workflow.Env = prev.Env;
        }

        if ((skipMask & (1 << (int)RootSectionKind.Permissions)) != 0)
        {
            workflow.Permissions = prev.Permissions;
        }

        if ((skipMask & (1 << (int)RootSectionKind.Defaults)) != 0)
        {
            workflow.Defaults = prev.Defaults;
        }

        if ((skipMask & (1 << (int)RootSectionKind.Concurrency)) != 0)
        {
            workflow.Concurrency = prev.Concurrency;
        }
    }

    /// <summary>
    /// Computes job skip entries for D-5c incremental parsing.
    /// Compares each job's byte range in the new source against the previous registry.
    /// Returns null if no jobs can be skipped (job count changed, or no previous jobs).
    /// </summary>
    private JobSkipEntry[]? ComputeJobSkipEntries(byte[] newSource, ref SectionRegistry newRegistry)
    {
        var prevJobCount = _registry.JobCount;
        var newJobCount = newRegistry.JobCount;

        // If job count changed, can't match by position — skip nothing
        if (prevJobCount == 0 || newJobCount == 0 || prevJobCount != newJobCount)
            return null;

        var prevWorkflow = _previousWorkflow!;
        var prevJobs = prevWorkflow.Jobs.Entries;

        // If previous workflow has different job count than registry, skip
        if (prevJobs.Length != prevJobCount)
            return null;

        // Reuse field-level buffer, grow only when needed
        if (_jobSkipEntriesBuf is null || _jobSkipEntriesBuf.Length < newJobCount)
            _jobSkipEntriesBuf = new JobSkipEntry[newJobCount];
        else
            Array.Clear(_jobSkipEntriesBuf, 0, newJobCount);

        var anySkippable = false;

        for (var i = 0; i < newJobCount; i++)
        {
            var prevEntry = _registry.GetJobEntry(i);
            var newEntry = newRegistry.GetJobEntry(i);

            if (!prevEntry.IsValid || !newEntry.IsValid || prevEntry.HasDiagnostics)
                continue;

            // Check if bytes are at the same offset with same content
            if (prevEntry.StartOffset == newEntry.StartOffset &&
                prevEntry.EndOffset == newEntry.EndOffset &&
                prevEntry.ContentHash == newEntry.ContentHash)
            {
                // This job is unchanged — mark for skip
                _jobSkipEntriesBuf[i] = new JobSkipEntry(prevJobs[i].Key, prevJobs[i].Value);
                anySkippable = true;
            }
        }

        return anySkippable ? _jobSkipEntriesBuf : null;
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

    /// <summary>
    /// Checks whether the new source is byte-identical to the previous source.
    /// Uses reference equality first (common case: same buffer), then content comparison.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsSourceIdentical(byte[] newSource)
    {
        if (_previousSource is null) return false;
        if (ReferenceEquals(_previousSource, newSource)) return true;
        if (_previousSourceLength != newSource.Length) return false;
        return newSource.AsSpan().SequenceEqual(_previousSource.AsSpan(0, _previousSourceLength));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeHash(ReadOnlySpan<byte> data)
    {
        return (long)XxHash64.Hash(data);
    }

    // ──────────────────────────────────────────────────────────────────────
    // D-5d: Lint result cache — per-job diagnostic caching
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>Shared lint engine for <see cref="LintIncrementally"/>. Guarded by external lock.</summary>
    private LintEngine? _lintEngine;

    /// <summary>Lint config for incremental lint.</summary>
    private static readonly LintConfig LintConfig = new()
    {
        Fix = new FixConfig { Enabled = true },
        Network = new NetworkConfig(),
        Output = new OutputConfig(),
        SkipSuppressionSummary = true,
    };

    // Reusable buffers for LintIncrementally (avoids per-call allocations)
    private bool[]? _skipJobsBuf;
    private List<Diagnostic>? _mergedDiagnostics;
    private ArrayBufferWriter<byte>? _jsonBuffer;

    /// <summary>
    /// Parses and lints incrementally. Unchanged jobs reuse cached diagnostics from
    /// the previous lint run (D-5d). Returns parsed JSON diagnostic elements.
    /// </summary>
    public JsonElement[] LintIncrementally(byte[] utf8Yaml, string filePath)
    {
        _lintEngine ??= new LintEngine();

        // Parse incrementally (D-5b/5c)
        var parseResult = ParseIncrementally(utf8Yaml, filePath);

        // Determine which jobs to skip linting (reused and have cached diagnostics)
        bool[]? skipJobs = null;
        if (_lastReusedJobs is not null && _cachedJobDiagnostics is not null)
        {
            var jobCount = parseResult.Workflow?.Jobs.Count ?? 0;
            if (jobCount > 0)
            {
                // Reuse buffer, grow only when needed
                if (_skipJobsBuf is null || _skipJobsBuf.Length < jobCount)
                    _skipJobsBuf = new bool[jobCount];
                else
                    Array.Clear(_skipJobsBuf, 0, jobCount);

                var anySkippable = false;
                for (var i = 0; i < jobCount && i < _lastReusedJobs.Length; i++)
                {
                    if (_lastReusedJobs[i] && i < _cachedJobDiagnostics.Length && _cachedJobDiagnostics[i] is not null)
                    {
                        _skipJobsBuf[i] = true;
                        anySkippable = true;
                    }
                }

                if (anySkippable)
                    skipJobs = _skipJobsBuf;
            }
        }

        // Lint with optional job skipping
        var lintResult = _lintEngine.CheckWithParseResult(utf8Yaml, filePath, LintConfig, parseResult, skipJobs);

        // Merge cached diagnostics for skipped jobs
        DiagnosticList finalDiagnostics;
        if (skipJobs is not null)
        {
            var merged = _mergedDiagnostics ??= new(32);
            merged.Clear();

            // Add fresh diagnostics from the linter
            var lintDiags = lintResult.Diagnostics;
            for (var i = 0; i < lintDiags.Length; i++)
                merged.Add(lintDiags[i]);

            // Add cached diagnostics for skipped jobs
            for (var i = 0; i < skipJobs.Length; i++)
            {
                if (skipJobs[i] && _cachedJobDiagnostics![i] is { } cached)
                {
                    for (var c = 0; c < cached.Length; c++)
                        merged.Add(cached[c]);
                }
            }

            // Sort by offset for consistent output
            merged.Sort(static (a, b) =>
            {
                var cmp = a.Location.Start.CompareTo(b.Location.Start);
                return cmp != 0 ? cmp : string.Compare(a.Message, b.Message, StringComparison.Ordinal);
            });
            finalDiagnostics = merged.ToArray();
        }
        else
        {
            finalDiagnostics = lintResult.Diagnostics;
        }

        // Cache per-job diagnostics for next call
        CacheJobDiagnostics(finalDiagnostics);

        // Mark root sections that contain diagnostics so they won't be skipped next time
        MarkRootSectionsWithDiagnostics(finalDiagnostics);

        // Serialize to JSON and parse into elements (matching PlaygroundLintRunner format)
        return SerializeDiagnosticsToJson(finalDiagnostics);
    }

    /// <summary>
    /// Partitions diagnostics by job byte range and stores them in the per-job cache.
    /// Diagnostics not within any job's range (workflow-level) are not cached.
    /// </summary>
    private void CacheJobDiagnostics(DiagnosticList diagnostics)
    {
        var jobCount = _registry.JobCount;
        if (jobCount == 0)
        {
            _cachedJobDiagnostics = null;
            return;
        }

        if (_cachedJobDiagnostics is null || _cachedJobDiagnostics.Length < jobCount)
            _cachedJobDiagnostics = new Diagnostic[jobCount][];

        // Clear existing cache entries
        for (var i = 0; i < jobCount; i++)
            _cachedJobDiagnostics[i] = null;

        // Count diagnostics per job first (avoids List<> per job)
        Span<int> counts = jobCount <= 64 ? stackalloc int[jobCount] : new int[jobCount];
        counts.Clear();

        for (var d = 0; d < diagnostics.Length; d++)
        {
            var offset = diagnostics[d].Location.Start;
            for (var j = 0; j < jobCount; j++)
            {
                var entry = _registry.GetJobEntry(j);
                if (entry.IsValid && offset >= entry.StartOffset && offset < entry.EndOffset)
                {
                    counts[j]++;
                    break;
                }
            }
        }

        // Allocate per-job arrays based on counted sizes
        for (var j = 0; j < jobCount; j++)
        {
            if (counts[j] > 0)
                _cachedJobDiagnostics[j] = new Diagnostic[counts[j]];
        }

        // Fill arrays (reset counts as write indices)
        counts.Clear();
        for (var d = 0; d < diagnostics.Length; d++)
        {
            var diag = diagnostics[d];
            var offset = diag.Location.Start;
            for (var j = 0; j < jobCount; j++)
            {
                var entry = _registry.GetJobEntry(j);
                if (entry.IsValid && offset >= entry.StartOffset && offset < entry.EndOffset)
                {
                    _cachedJobDiagnostics[j]![counts[j]++] = diag;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Marks root section entries as HasDiagnostics if any parse diagnostic falls within their byte range.
    /// Called after building/updating the registry to ensure sections with diagnostics are never skipped.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkRootSectionsWithParseDiagnostics(DiagnosticList diagnostics)
        => MarkRootSectionsWithDiagnostics(diagnostics);

    /// <summary>
    /// Marks root section entries as HasDiagnostics if any diagnostic falls within their byte range.
    /// This prevents the section from being skipped in the next incremental parse.
    /// </summary>
    private void MarkRootSectionsWithDiagnostics(DiagnosticList diagnostics)
    {
        if (diagnostics.Length == 0)
            return;

        // Check each skippable root section kind
        ReadOnlySpan<RootSectionKind> kinds =
        [
            RootSectionKind.On,
            RootSectionKind.Env,
            RootSectionKind.Permissions,
            RootSectionKind.Defaults,
            RootSectionKind.Concurrency,
        ];

        foreach (var kind in kinds)
        {
            var entry = _registry.GetRootSection(kind);
            if (!entry.IsValid || entry.HasDiagnostics)
                continue;

            for (var d = 0; d < diagnostics.Length; d++)
            {
                var offset = diagnostics[d].Location.Start;
                if (offset >= entry.StartOffset && offset < entry.EndOffset)
                {
                    // Re-record with HasDiagnostics = true
                    _registry.SetRootSection(kind, new SectionEntry(
                        entry.StartOffset, entry.EndOffset, entry.ContentHash, hasDiagnostics: true));
                    break;
                }
            }
        }
    }

    private JsonElement[] SerializeDiagnosticsToJson(DiagnosticList diagnostics)
    {
        var buffer = _jsonBuffer ??= new ArrayBufferWriter<byte>(4096);
        buffer.Clear();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var i = 0; i < diagnostics.Length; i++)
            {
                var d = diagnostics[i];
                writer.WriteStartObject();
                writer.WriteString("message", d.Message);
                writer.WriteNumber("line", d.Location.StartLine);
                writer.WriteNumber("column", d.Location.StartColumn);
                writer.WriteString("severity", d.Severity switch
                {
                    DiagnosticSeverity.Error => "Error",
                    DiagnosticSeverity.Warning => "Warning",
                    _ => "Info"
                });
                writer.WriteString("ruleId", d.RuleId);
                writer.WriteBoolean("fixable", d.Fix is not null);
                writer.WriteString("fixDescription", d.Fix?.Description);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        var arr = new JsonElement[diagnostics.Length];
        var idx = 0;
        foreach (var elem in doc.RootElement.EnumerateArray())
            arr[idx++] = elem.Clone();
        return arr;
    }
}
