namespace Seiton.Core.Parsing;

public readonly record struct WorkflowDocument(
    bool HasName,
    Utf8Slice Name,
    bool HasRunName,
    Utf8Slice RunName,
    bool HasOn,
    bool HasJobs);
