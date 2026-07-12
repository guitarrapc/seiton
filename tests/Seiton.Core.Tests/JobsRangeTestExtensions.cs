using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

/// <summary>
/// Test helper extensions over the workflow jobs map (a <see cref="NodeRange"/> over
/// key-embedded <see cref="JobEntryData"/> rows). Mirrors the old SliceMap-based
/// Get/ContainsKey/Values access patterns with an explicit arena argument.
/// </summary>
internal static class JobsRangeTestExtensions
{
    /// <summary>Gets a job row by UTF-8 key (case-insensitive). Throws KeyNotFoundException if not found.</summary>
    public static JobData Get(this NodeRange jobs, AstArena arena, ReadOnlySpan<byte> key)
    {
        if (TryGet(jobs, arena, key, out var job))
            return job;
        throw new KeyNotFoundException($"Job '{Encoding.UTF8.GetString(key)}' not found in jobs map");
    }

    /// <summary>Checks whether a job with the given UTF-8 key exists (case-insensitive).</summary>
    public static bool ContainsKey(this NodeRange jobs, AstArena arena, ReadOnlySpan<byte> key)
        => TryGet(jobs, arena, key, out _);

    /// <summary>Enumerates all job rows in document order.</summary>
    public static IEnumerable<JobData> Values(this NodeRange jobs, AstArena arena)
    {
        for (var i = 0; i < jobs.Count; i++)
        {
            yield return arena.GetJob(arena.GetJobEntryAt(jobs, i).Job);
        }
    }

    private static bool TryGet(NodeRange jobs, AstArena arena, ReadOnlySpan<byte> key, out JobData job)
    {
        for (var i = 0; i < jobs.Count; i++)
        {
            ref readonly var entry = ref arena.GetJobEntryAt(jobs, i);
            if (SliceMap<int>.AsciiEqualsIgnoreCase(entry.Key.AsSpan(arena.Source), key))
            {
                job = arena.GetJob(entry.Job);
                return true;
            }
        }

        job = default;
        return false;
    }
}
