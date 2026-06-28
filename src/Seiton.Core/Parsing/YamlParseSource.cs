namespace Seiton.Core.Parsing;

/// <summary>
/// Prepares UTF-8 YAML bytes for VYaml without mutating the caller's buffer.
/// </summary>
internal static class YamlParseSource
{
    [ThreadStatic]
    private static byte[]? s_paddingBuffer;

    /// <summary>
    /// Returns memory suitable for VYaml parsing. When the source would trigger VYaml EOF hangs
    /// (no trailing <c>\n</c> and the last non-whitespace byte is <c>:</c>), appends a virtual
    /// trailing <c>\n</c> on a parse-only copy.
    /// </summary>
    internal static Memory<byte> GetVYamlMemory(byte[] utf8Yaml)
    {
        if (!NeedsVirtualTrailingNewline(utf8Yaml))
        {
            return utf8Yaml.AsMemory();
        }

        var paddedLength = utf8Yaml.Length + 1;
        var buffer = s_paddingBuffer;
        if (buffer is null || buffer.Length < paddedLength)
        {
            buffer = new byte[Math.Max(paddedLength, 256)];
            s_paddingBuffer = buffer;
        }

        if (utf8Yaml.Length > 0)
        {
            utf8Yaml.AsSpan().CopyTo(buffer);
        }

        buffer[utf8Yaml.Length] = (byte)'\n';
        return buffer.AsMemory(0, paddedLength);
    }

    internal static bool NeedsVirtualTrailingNewline(ReadOnlySpan<byte> utf8Yaml)
    {
        if (utf8Yaml.Length == 0 || utf8Yaml[^1] == (byte)'\n')
        {
            return false;
        }

        var end = utf8Yaml.Length;
        while (end > 0 && utf8Yaml[end - 1] is (byte)' ' or (byte)'\t')
        {
            end--;
        }

        return end > 0 && utf8Yaml[end - 1] == (byte)':';
    }
}
