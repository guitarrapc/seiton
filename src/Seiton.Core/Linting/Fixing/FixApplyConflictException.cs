namespace Seiton.Core.Linting.Fixing;

/// <summary>
/// Thrown when fix edits overlap or share the same offset. Carries structured context for CLI hints.
/// </summary>
public sealed class FixApplyConflictException : InvalidOperationException
{
    public FixApplyConflictException(
        int conflictOffset,
        int previousOffset,
        int previousLength,
        int currentOffset,
        int currentLength,
        int totalEditsInBatch,
        IReadOnlyList<string>? conflictingRuleIds = null,
        Exception? innerException = null)
        : base(BuildMessage(
            conflictOffset,
            previousOffset,
            previousLength,
            currentOffset,
            currentLength,
            totalEditsInBatch,
            conflictingRuleIds),
            innerException)
    {
        ConflictOffset = conflictOffset;
        PreviousOffset = previousOffset;
        PreviousLength = previousLength;
        CurrentOffset = currentOffset;
        CurrentLength = currentLength;
        TotalEditsInBatch = totalEditsInBatch;
        ConflictingRuleIds = conflictingRuleIds ?? [];
    }

    public int ConflictOffset { get; }

    public int PreviousOffset { get; }

    public int PreviousLength { get; }

    public int CurrentOffset { get; }

    public int CurrentLength { get; }

    public int TotalEditsInBatch { get; }

    public IReadOnlyList<string> ConflictingRuleIds { get; }

    private static string BuildMessage(
        int conflictOffset,
        int previousOffset,
        int previousLength,
        int currentOffset,
        int currentLength,
        int totalEditsInBatch,
        IReadOnlyList<string>? conflictingRuleIds)
    {
        var message =
            $"overlapping or conflicting edits detected at offset {conflictOffset} " +
            $"(previous edit at offset {previousOffset} with length {previousLength}, " +
            $"current edit at offset {currentOffset} with length {currentLength}; " +
            $"total {totalEditsInBatch} edits in batch)";

        if (conflictingRuleIds is { Count: > 0 })
        {
            message += $"; conflicting rule-id(s): {string.Join(", ", conflictingRuleIds)}";
        }

        return message;
    }
}
