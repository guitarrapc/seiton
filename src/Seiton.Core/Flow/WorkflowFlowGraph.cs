using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Flow;

internal static class WorkflowFlowGraph
{
    internal const int StackElementLimit = 128;

    internal static int GetWordCount(int jobCount) => (jobCount + 63) >> 6;

    internal static int GetAncestorLength(int jobCount, int wordCount)
        => checked(jobCount * wordCount);

    internal static void BuildAncestors(
        JobRefMap jobs,
        Span<ulong> ancestors,
        Span<byte> initialized,
        int wordCount)
    {
        ancestors.Clear();
        initialized.Clear();
        for (var i = 0; i < jobs.Count; i++)
        {
            BuildAncestorsOf(i, jobs, ancestors, initialized, wordCount);
        }
    }

    internal static bool IsRedundantNeed(
        JobRefMap jobs,
        StringRefList needs,
        int needIndex,
        ReadOnlySpan<ulong> ancestors,
        int wordCount)
    {
        if (needs.Count < 2)
        {
            return false;
        }

        var dependencyIndex = FindJobIndex(jobs, needs[needIndex].Value);
        if (dependencyIndex < 0)
        {
            return false;
        }

        for (var i = 0; i < needs.Count; i++)
        {
            var otherIndex = FindJobIndex(jobs, needs[i].Value);
            if (otherIndex == dependencyIndex || otherIndex < 0)
            {
                continue;
            }

            var row = ancestors.Slice(otherIndex * wordCount, wordCount);
            if ((row[dependencyIndex >> 6] & (1UL << (dependencyIndex & 63))) != 0)
            {
                return true;
            }
        }

        return false;
    }

    internal static int FindJobIndex(JobRefMap jobs, ReadOnlySpan<byte> id)
    {
        for (var i = 0; i < jobs.Count; i++)
        {
            if (SpanHelpers.EqualsAsciiIgnoreCase(jobs.GetAt(i).Key.Bytes, id))
            {
                return i;
            }
        }

        return -1;
    }

    private static void BuildAncestorsOf(
        int index,
        JobRefMap jobs,
        Span<ulong> ancestors,
        Span<byte> initialized,
        int wordCount)
    {
        if (initialized[index] != 0)
        {
            return;
        }

        initialized[index] = 1;
        var row = ancestors.Slice(index * wordCount, wordCount);
        var needs = jobs.GetAt(index).Value.Needs;
        for (var i = 0; i < needs.Count; i++)
        {
            var dependencyIndex = FindJobIndex(jobs, needs[i].Value);
            if (dependencyIndex < 0)
            {
                continue;
            }

            row[dependencyIndex >> 6] |= 1UL << (dependencyIndex & 63);
            BuildAncestorsOf(dependencyIndex, jobs, ancestors, initialized, wordCount);
            var dependencyRow = ancestors.Slice(dependencyIndex * wordCount, wordCount);
            for (var word = 0; word < wordCount; word++)
            {
                row[word] |= dependencyRow[word];
            }
        }
    }
}
