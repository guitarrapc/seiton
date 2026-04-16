using Seiton.Core.Linting.Fixing;
using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Core.Tests;

public sealed class FixEngineTests
{
    [Test]
    public async Task Apply_AppliesEditsInDescendingOffsetOrder()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var edits = new[]
        {
            new TextEdit(2, 2, "AB"),
            new TextEdit(7, 2, "YZ"),
        };

        var result = FixEngine.Apply(source, edits);

        await Assert.That(Encoding.UTF8.GetString(result)).IsEqualTo("01AB456YZ9");
    }

    [Test]
    public async Task Apply_RejectsOverlappingEdits()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var edits = new[]
        {
            new TextEdit(2, 4, "ABCD"),
            new TextEdit(5, 2, "YZ"),
        };

        await Assert.That(() => FixEngine.Apply(source, edits)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task DetectDominantLineEnding_PrefersCrLfWhenMajority()
    {
        var source = Encoding.UTF8.GetBytes("a\r\nb\r\nc\n");

        var lineEnding = FixFormatting.DetectDominantLineEnding(source);

        await Assert.That(lineEnding).IsEqualTo("\r\n");
    }

    [Test]
    public async Task InferIndentation_PrefersSiblingIndentation()
    {
        var source = "jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n";

        var indentation = FixFormatting.InferIndentation(source, siblingLineNumber: 3, parentLineNumber: 2);

        await Assert.That(indentation).IsEqualTo("    ");
    }

    [Test]
    public async Task InferIndentation_FallsBackToParentPlusIndentationUnit()
    {
        var source = "jobs:\n  build:\n";

        var indentation = FixFormatting.InferIndentation(source, siblingLineNumber: null, parentLineNumber: 2);

        await Assert.That(indentation).IsEqualTo("    ");
    }

    [Test]
    public async Task DetectQuoteStyle_UsesSourceBytesAroundRange()
    {
        var source = Encoding.UTF8.GetBytes("name: 'value'\n");

        var quoteStyle = FixFormatting.DetectQuoteStyle(
            source,
            new TextRange(7, 5, 1, 8, 1, 13),
            quoted: true);

        await Assert.That(quoteStyle).IsEqualTo(ScalarQuoteStyle.SingleQuoted);
    }
}
