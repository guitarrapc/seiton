namespace Seiton.Output;

public enum OutputFormat
{
    Text,
    Json,
    Sarif,
    GitHubActions,
    FlowJson,
    FlowMermaid,
}

public static class OutputFormatExtensions
{
    /// <summary>Rich multi-line diagnostics with optional source snippets (text and github-actions).</summary>
    public static bool UsesRichTextOutput(this OutputFormat format)
        => format is OutputFormat.Text or OutputFormat.GitHubActions;
}
