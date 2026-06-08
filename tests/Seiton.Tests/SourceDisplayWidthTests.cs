using Seiton.Output;

namespace Seiton.Tests;

public sealed class SourceDisplayWidthTests
{
    [Test]
    public async Task GetWidthBeforeColumn_Ascii_MatchesByteCount()
    {
        var line = "    runs-on: ubuntu-latest"u8.ToArray();
        var beforeFive = SourceDisplayWidth.GetWidthBeforeColumn(line, 5);
        var beforeFourteen = SourceDisplayWidth.GetWidthBeforeColumn(line, 14);
        await Assert.That(beforeFive).IsEqualTo(4);
        await Assert.That(beforeFourteen).IsEqualTo(13);
    }

    [Test]
    public async Task GetWidthBeforeColumn_Tab_UsesTabWidthFour()
    {
        var line = "\tfoo"u8.ToArray();
        await Assert.That(SourceDisplayWidth.GetWidthBeforeColumn(line, 1)).IsEqualTo(0);
        await Assert.That(SourceDisplayWidth.GetWidthBeforeColumn(line, 2)).IsEqualTo(4);
        await Assert.That(SourceDisplayWidth.GetWidthBeforeColumn(line, 5)).IsEqualTo(7);
    }

    [Test]
    public async Task GetWidthBetweenColumnsInclusive_Ascii_MatchesByteSpan()
    {
        var line = "  build:"u8.ToArray();
        await Assert.That(SourceDisplayWidth.GetWidthBetweenColumnsInclusive(line, 3, 7)).IsEqualTo(5);
    }

    [Test]
    public async Task GetWidthBetweenColumnsInclusive_WideCharacters_UsesDisplayWidth()
    {
        var line = Encoding.UTF8.GetBytes("# 日本");
        await Assert.That(SourceDisplayWidth.GetWidthBetweenColumnsInclusive(line, 3, 8)).IsEqualTo(4);
    }

    [Test]
    public async Task GetWidthBeforeColumn_ColumnBeyondLine_EndsAtLineWidth()
    {
        var line = "abc"u8.ToArray();
        await Assert.That(SourceDisplayWidth.GetWidthBeforeColumn(line, 10)).IsEqualTo(3);
    }

    [Test]
    public async Task GetWidthBeforeColumn_WideCharacterStart_ExcludesWideCharacterBytes()
    {
        var line = Encoding.UTF8.GetBytes("# 日本");
        await Assert.That(SourceDisplayWidth.GetWidthBeforeColumn(line, 3)).IsEqualTo(2);
    }
}
