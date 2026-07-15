using System.Buffers;
using Seiton.Core.Flow;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Playground;

/// <summary>
/// Shared flow-json / flow-mermaid output cache keyed by YAML content hash and file path.
/// Populated by <see cref="PlaygroundLintRunner"/> during workflow lint (single parse)
/// and by <see cref="PlaygroundFlowRunner"/> on flow-only requests.
/// </summary>
internal static class PlaygroundFlowOutputCache
{
    private const int MaxRetainedBufferBytes = 256 * 1024;

    private static readonly object Gate = new();
    private static ArrayBufferWriter<byte> JsonBuffer = new(4096);
    private static ArrayBufferWriter<byte> MermaidBuffer = new(4096);

    private static ulong _yamlHash;
    private static string? _filePath;
    private static byte[]? _flowJson;
    private static byte[]? _flowMermaid;

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _yamlHash = 0;
            _filePath = null;
            _flowJson = null;
            _flowMermaid = null;
            JsonBuffer = new ArrayBufferWriter<byte>(4096);
            MermaidBuffer = new ArrayBufferWriter<byte>(4096);
        }
    }

    internal static bool TryGet(ulong yamlHash, string filePath, out byte[] flowJson, out byte[] flowMermaid)
    {
        lock (Gate)
        {
            if (_flowJson is not null
                && _flowMermaid is not null
                && yamlHash == _yamlHash
                && string.Equals(filePath, _filePath, StringComparison.Ordinal))
            {
                flowJson = _flowJson;
                flowMermaid = _flowMermaid;
                return true;
            }
        }

        flowJson = null!;
        flowMermaid = null!;
        return false;
    }

    internal static (byte[] Json, byte[] Mermaid) Store(
        ulong yamlHash,
        string filePath,
        WorkflowRef workflow)
    {
        lock (Gate)
        {
            ResetIfOversized(ref JsonBuffer);
            JsonBuffer.Clear();
            WorkflowFlowJson.Write(JsonBuffer, workflow, filePath);
            var json = UpdateCacheBytes(_flowJson, JsonBuffer.WrittenSpan);

            ResetIfOversized(ref MermaidBuffer);
            MermaidBuffer.Clear();
            WorkflowFlowMermaid.Write(MermaidBuffer, workflow, filePath);
            var mermaid = UpdateCacheBytes(_flowMermaid, MermaidBuffer.WrittenSpan);

            if (json.Length <= MaxRetainedBufferBytes && mermaid.Length <= MaxRetainedBufferBytes)
            {
                _yamlHash = yamlHash;
                _filePath = filePath;
                _flowJson = json;
                _flowMermaid = mermaid;
            }
            else
            {
                _yamlHash = 0;
                _filePath = null;
                _flowJson = null;
                _flowMermaid = null;
            }

            return (json, mermaid);
        }
    }

    private static byte[] UpdateCacheBytes(byte[]? existing, ReadOnlySpan<byte> written)
    {
        if (existing is not null && existing.Length == written.Length && written.SequenceEqual(existing))
        {
            return existing;
        }

        if (existing is null || existing.Length != written.Length)
        {
            existing = new byte[written.Length];
        }

        written.CopyTo(existing);
        return existing;
    }

    private static void ResetIfOversized(ref ArrayBufferWriter<byte> buffer)
    {
        if (buffer.Capacity > MaxRetainedBufferBytes)
        {
            buffer = new ArrayBufferWriter<byte>(4096);
        }
    }
}
