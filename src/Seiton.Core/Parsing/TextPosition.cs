namespace Seiton.Core.Parsing;

public readonly record struct TextPosition(int Offset, int Line, int Column)
{
    public int Position => Offset;

    public int Col => Column;
}
