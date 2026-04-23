namespace Seiton.Core.Parsing;

/// <summary>Source location in the YAML document (0-based offset, 1-based line and column).</summary>
public readonly record struct TextPosition(int Offset, int Line, int Column)
{
    /// <summary>Alias for <see cref="Offset"/>.</summary>
    public int Position => Offset;

    /// <summary>Alias for <see cref="Column"/>.</summary>
    public int Col => Column;
}
