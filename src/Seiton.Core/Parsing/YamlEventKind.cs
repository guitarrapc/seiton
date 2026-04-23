namespace Seiton.Core.Parsing;

/// <summary>Pull-based YAML event types emitted by the stream reader.</summary>
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
