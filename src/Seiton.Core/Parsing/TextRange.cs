namespace Seiton.Core.Parsing;

public readonly record struct TextRange(
    int Start,
    int Length,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);
