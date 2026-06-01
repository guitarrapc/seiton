namespace Seiton.Output;

/// <summary>Parses user-facing format names (CLI and <c>SEITON_FORMAT</c>).</summary>
public static class OutputFormatParser
{
    public static bool TryParse(string? value, out OutputFormat format)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            format = OutputFormat.Text;
            return true;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "text" => Set(out format, OutputFormat.Text),
            "json" => Set(out format, OutputFormat.Json),
            "sarif" => Set(out format, OutputFormat.Sarif),
            "github-actions" => Set(out format, OutputFormat.GitHubActions),
            _ => Fail(out format),
        };
    }

    private static bool Set(out OutputFormat format, OutputFormat value)
    {
        format = value;
        return true;
    }

    private static bool Fail(out OutputFormat format)
    {
        format = OutputFormat.Text;
        return false;
    }
}
