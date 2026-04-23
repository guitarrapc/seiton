namespace Seiton.Core.Parsing;

/// <summary>A range within the YAML source text (byte offset + line/column).</summary>
public readonly record struct TextRange(
    int Start,
    int Length,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);
