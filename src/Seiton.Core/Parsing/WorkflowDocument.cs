namespace Seiton.Core.Parsing;

[Obsolete("Use Parsing.Ast.Workflow via ParseResult.Workflow instead.")]
public readonly record struct WorkflowDocument(
    bool HasName,
    Utf8Slice Name,
    bool HasRunName,
    Utf8Slice RunName,
    bool HasOn,
    bool HasJobs);
