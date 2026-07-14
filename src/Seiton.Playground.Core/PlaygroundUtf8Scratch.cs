using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Playground;

/// <summary>
/// Shared UTF-8 encode buffer for playground WASM hot paths.
/// Returned arrays are reused; callers must serialize access and consume before the next encode.
/// </summary>
internal static class PlaygroundUtf8Scratch
{
    private static byte[] _buf = [];

    internal static void ResetForTests() => _buf = [];

    /// <summary>Encodes <paramref name="source"/> once and returns the buffer plus its XxHash64.</summary>
    internal static (byte[] Utf8, ulong Hash) EncodeAndHash(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var byteCount = Encoding.UTF8.GetByteCount(source);
        if (_buf.Length != byteCount)
        {
            _buf = new byte[byteCount];
        }

        Encoding.UTF8.GetBytes(source, _buf);
        return (_buf, XxHash64.Hash(_buf.AsSpan(0, byteCount)));
    }
}
