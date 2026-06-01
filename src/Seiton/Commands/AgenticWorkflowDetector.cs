namespace Seiton.Commands;

internal static class AgenticWorkflowDetector
{
    private const int MaxScanLines = 10;
    private const int ReadBufferSize = 4096;

    /// <summary>Scans the first ~10 lines of a workflow file for the Agentic Workflow metadata marker.</summary>
    public static bool IsAgenticWorkflowFile(string filePath)
    {
        if (filePath == "-")
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        if (stream.Length == 0)
        {
            return false;
        }

        var bufferSize = (int)Math.Min(ReadBufferSize, stream.Length);
        var buffer = new byte[bufferSize];
        var read = stream.Read(buffer, 0, bufferSize);
        return HasMetadataInPrefix(buffer.AsSpan(0, read));
    }

    internal static bool HasMetadataInPrefix(ReadOnlySpan<byte> content)
    {
        var lineCount = 0;
        var lineStart = 0;
        for (var i = 0; i < content.Length && lineCount < MaxScanLines; i++)
        {
            if (content[i] is (byte)'\n' or (byte)'\r')
            {
                if (content[i] == (byte)'\r' && i + 1 < content.Length && content[i + 1] == (byte)'\n')
                {
                    i++;
                }

                if (LineContainsMarker(content.Slice(lineStart, i - lineStart)))
                {
                    return true;
                }

                lineCount++;
                lineStart = i + 1;
            }
        }

        if (lineCount < MaxScanLines && lineStart < content.Length
            && LineContainsMarker(content[lineStart..]))
        {
            return true;
        }

        return false;
    }

    private static bool LineContainsMarker(ReadOnlySpan<byte> line)
    {
        line = TrimAsciiWhiteSpace(line);
        return line.StartsWith("# gh-aw-metadata:"u8);
    }

    private static ReadOnlySpan<byte> TrimAsciiWhiteSpace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && IsAsciiWhiteSpace(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsAsciiWhiteSpace(value[end - 1]))
        {
            end--;
        }

        return value.Slice(start, end - start);
    }

    private static bool IsAsciiWhiteSpace(byte value)
        => value is (byte)' ' or (byte)'\t';
}
