using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Playground;

/// <summary>Content hash for playground YAML caches (independent of string instance identity).</summary>
internal static class PlaygroundYamlHash
{
    internal static ulong Compute(string yamlSource)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        var byteCount = Encoding.UTF8.GetByteCount(yamlSource);
        byte[]? rented = null;
        Span<byte> utf8Span = byteCount <= 1024
            ? stackalloc byte[byteCount]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        try
        {
            Encoding.UTF8.GetBytes(yamlSource, utf8Span);
            return XxHash64.Hash(utf8Span);
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    internal static ulong Compute(ReadOnlySpan<byte> utf8Yaml) => XxHash64.Hash(utf8Yaml);
}
