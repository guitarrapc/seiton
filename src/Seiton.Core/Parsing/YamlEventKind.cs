namespace Seiton.Core.Parsing;

public enum YamlEventKind
{
    None,
    StreamStart,
    StreamEnd,
    DocumentStart,
    DocumentEnd,
    MappingStart,
    MappingEnd,
    SequenceStart,
    SequenceEnd,
    Scalar,
    Alias,
}
