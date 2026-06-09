namespace Seiton.Core.Linting;

/// <summary>Well-known diagnostic metadata keys for structure snippet rendering.</summary>
public static class DiagnosticStructurePathMetadata
{
    /// <summary>
    /// Optional YAML structure path (e.g. <c>jobs.'build'.steps[1].uses</c>).
    /// When present, structure snippets resolve from this path instead of parsing the message.
    /// </summary>
    public const string Key = "structure-path";
}
