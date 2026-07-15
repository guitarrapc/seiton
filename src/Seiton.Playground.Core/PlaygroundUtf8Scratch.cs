using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Playground;

/// <summary>
/// UTF-8 encode helper for playground hot paths.
/// The caller owns and serializes access to its reusable buffer.
/// </summary>
internal static class PlaygroundUtf8Scratch
{
    /// <summary>Encodes <paramref name="source"/> once and returns the buffer plus its XxHash64.</summary>
    internal static (byte[] Utf8, ulong Hash) EncodeAndHash(string source, ref byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(source);
        var byteCount = Encoding.UTF8.GetByteCount(source);
        if (buffer.Length != byteCount)
        {
            buffer = new byte[byteCount];
        }

        Encoding.UTF8.GetBytes(source, buffer);
        return (buffer, XxHash64.Hash(buffer.AsSpan(0, byteCount)));
    }
}
