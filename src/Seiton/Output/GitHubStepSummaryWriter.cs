using System.Text;

namespace Seiton.Output;

/// <summary>Appends Markdown blocks to <c>GITHUB_STEP_SUMMARY</c> for the github-actions output format.</summary>
internal static class GitHubStepSummaryWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static bool sHeadingWritten;

    internal static void Reset() => sHeadingWritten = false;

    internal static bool TryGetPath(out string? path)
    {
        path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        return !string.IsNullOrWhiteSpace(path);
    }

    /// <summary>
    /// When <paramref name="format"/> is <see cref="OutputFormat.GitHubActions"/> and the summary path is set,
    /// writes <paramref name="writeContent"/> to the job summary file. Returns false when content should go to stderr.
    /// </summary>
    internal static bool TryAppend(OutputFormat format, Action<TextWriter> writeContent)
    {
        if (format != OutputFormat.GitHubActions)
            return false;

        if (!TryGetPath(out var path) || path is null)
            return false;

        var buffer = new StringBuilder(capacity: 256);
        using (var writer = new StringWriter(buffer))
        {
            writeContent(writer);
        }

        if (buffer.Length == 0)
            return false;

        try
        {
            AppendToFile(path, buffer.ToString());
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void AppendToFile(string path, string content)
    {
        content = content.ReplaceLineEndings("\n");
        var needsLeadingBlank = File.Exists(path) && new FileInfo(path).Length > 0;

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, Utf8NoBom) { NewLine = "\n" };
        if (needsLeadingBlank)
            writer.WriteLine();

        if (!sHeadingWritten)
        {
            writer.WriteLine("## Seiton");
            sHeadingWritten = true;
        }

        writer.Write(content);
        if (content.Length == 0 || content[^1] is not ('\n' or '\r'))
            writer.WriteLine();

        writer.Flush();
    }
}
