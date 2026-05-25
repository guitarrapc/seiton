using Seiton.Commands;
using Seiton.Core.Parsing;

namespace Seiton.Tests;

public sealed class FixBatchSelectionTests
{
    private static Diagnostic MakeDiagnosticWithFix(TextEdit[] edits, string ruleId = "test-rule")
    {
        return new Diagnostic(
            DiagnosticSeverity.Warning,
            $"message for {ruleId}",
            new TextRange(0, 0, 1, 1, 1, 1),
            RuleId: ruleId,
            Fix: new DiagnosticFix("fix", edits));
    }

    [Test]
    public async Task SelectNonConflictingBatch_SingleDiagnostic_ReturnsAsIs()
    {
        var diagnostics = new[]
        {
            MakeDiagnosticWithFix([new TextEdit(0, 5, "AAAAA")]),
        };

        var result = FixCommand.SelectNonConflictingBatch(diagnostics);

        await Assert.That(result).Count().IsEqualTo(1);
    }

    [Test]
    public async Task SelectNonConflictingBatch_NonOverlapping_ReturnsAll()
    {
        var diagnostics = new[]
        {
            MakeDiagnosticWithFix([new TextEdit(0, 3, "AAA")], "rule-a"),
            MakeDiagnosticWithFix([new TextEdit(5, 3, "BBB")], "rule-b"),
            MakeDiagnosticWithFix([new TextEdit(10, 3, "CCC")], "rule-c"),
        };

        var result = FixCommand.SelectNonConflictingBatch(diagnostics);

        await Assert.That(result).Count().IsEqualTo(3);
    }

    [Test]
    public async Task SelectNonConflictingBatch_OverlappingRanges_SelectsFirstByOffset()
    {
        // Diag A: edits [2,6), Diag B: edits [4,8) — partial overlap
        // A has lower offset so A is selected, B is deferred.
        var diagnostics = new[]
        {
            MakeDiagnosticWithFix([new TextEdit(2, 4, "AAAA")], "rule-a"),
            MakeDiagnosticWithFix([new TextEdit(4, 4, "BBBB")], "rule-b"),
        };

        var result = FixCommand.SelectNonConflictingBatch(diagnostics);

        await Assert.That(result).Count().IsEqualTo(1);
        await Assert.That(result[0].RuleId).IsEqualTo("rule-a");
    }

    [Test]
    public async Task SelectNonConflictingBatch_SameOffsetInserts_SelectsFirstByIndex()
    {
        // Both insert at offset 5 — same offset conflicts.
        // First by original index wins.
        var diagnostics = new[]
        {
            MakeDiagnosticWithFix([new TextEdit(5, 0, "INSERT-A")], "rule-a"),
            MakeDiagnosticWithFix([new TextEdit(5, 0, "INSERT-B")], "rule-b"),
        };

        var result = FixCommand.SelectNonConflictingBatch(diagnostics);

        await Assert.That(result).Count().IsEqualTo(1);
        await Assert.That(result[0].RuleId).IsEqualTo("rule-a");
    }

    [Test]
    public async Task SelectNonConflictingBatch_PartialOverlap_DefersConflicting()
    {
        // Three diagnostics: A=[0,4), B=[3,7), C=[8,10)
        // A is selected first (lowest offset).
        // B overlaps with A → deferred.
        // C does not overlap with A → selected.
        var diagnostics = new[]
        {
            MakeDiagnosticWithFix([new TextEdit(0, 4, "AAAA")], "rule-a"),
            MakeDiagnosticWithFix([new TextEdit(3, 4, "BBBB")], "rule-b"),
            MakeDiagnosticWithFix([new TextEdit(8, 2, "CC")], "rule-c"),
        };

        var result = FixCommand.SelectNonConflictingBatch(diagnostics);

        await Assert.That(result).Count().IsEqualTo(2);
        await Assert.That(result[0].RuleId).IsEqualTo("rule-a");
        await Assert.That(result[1].RuleId).IsEqualTo("rule-c");
    }

    [Test]
    public async Task SelectNonConflictingBatch_InputOrderIndependent_LowestOffsetAlwaysWins()
    {
        // Diagnostics given in reverse offset order: B=[5,9), A=[0,6)
        // After sorting by min offset: A is first, B conflicts with A → deferred.
        var diagnostics = new[]
        {
            MakeDiagnosticWithFix([new TextEdit(5, 4, "BBBB")], "rule-b"),
            MakeDiagnosticWithFix([new TextEdit(0, 6, "AAAAAA")], "rule-a"),
        };

        var result = FixCommand.SelectNonConflictingBatch(diagnostics);

        await Assert.That(result).Count().IsEqualTo(1);
        // A has lower offset so it's selected regardless of input order
        await Assert.That(result[0].RuleId).IsEqualTo("rule-a");
    }

    [Test]
    public async Task SelectNonConflictingBatch_MultiEditDiagnostic_ConflictsOnAnyEdit()
    {
        // Diag A has 2 edits: [0,2) and [10,12)
        // Diag B has 1 edit: [1,3) — overlaps with A's first edit
        // A is selected (lower min offset), B is deferred.
        var diagnostics = new[]
        {
            MakeDiagnosticWithFix([new TextEdit(0, 2, "AA"), new TextEdit(10, 2, "BB")], "rule-a"),
            MakeDiagnosticWithFix([new TextEdit(1, 2, "CC")], "rule-b"),
        };

        var result = FixCommand.SelectNonConflictingBatch(diagnostics);

        await Assert.That(result).Count().IsEqualTo(1);
        await Assert.That(result[0].RuleId).IsEqualTo("rule-a");
    }

    [Test]
    public async Task SelectNonConflictingBatch_MultiEditDiagnostic_ConflictsOnSecondEdit()
    {
        // Diag A has 2 edits: [0,2) and [10,12)
        // Diag B has 1 edit: [11,1) — overlaps with A's second edit
        // A is selected (lower min offset), B is deferred.
        var diagnostics = new[]
        {
            MakeDiagnosticWithFix([new TextEdit(0, 2, "AA"), new TextEdit(10, 2, "BB")], "rule-a"),
            MakeDiagnosticWithFix([new TextEdit(11, 1, "C")], "rule-b"),
        };

        var result = FixCommand.SelectNonConflictingBatch(diagnostics);

        await Assert.That(result).Count().IsEqualTo(1);
        await Assert.That(result[0].RuleId).IsEqualTo("rule-a");
    }

    [Test]
    public async Task SelectNonConflictingBatch_AdjacentEdits_DoNotConflict()
    {
        // A=[0,3), B=[3,3) — B starts exactly where A ends. No overlap.
        var diagnostics = new[]
        {
            MakeDiagnosticWithFix([new TextEdit(0, 3, "AAA")], "rule-a"),
            MakeDiagnosticWithFix([new TextEdit(3, 3, "BBB")], "rule-b"),
        };

        var result = FixCommand.SelectNonConflictingBatch(diagnostics);

        await Assert.That(result).Count().IsEqualTo(2);
    }
}
