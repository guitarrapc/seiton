using System.Buffers;
using Seiton.Core.Flow;

namespace Seiton.Playground;

/// <summary>
/// Shared flow-json / flow-mermaid output cache keyed by YAML content hash and file path.
/// Populated by <see cref="PlaygroundLintRunner"/> during workflow lint (single parse)
/// and by <see cref="PlaygroundFlowRunner"/> on flow-only requests.
/// </summary>
internal static class PlaygroundFlowOutputCache
{
    private static readonly object Gate = new();
    private static readonly ArrayBufferWriter<byte> JsonBuffer = new(4096);
    private static readonly ArrayBufferWriter<byte> MermaidBuffer = new(4096);

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
            JsonBuffer.Clear();
            MermaidBuffer.Clear();
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

    internal static void Store(ulong yamlHash, string filePath, WorkflowFlow? flow)
    {
        lock (Gate)
        {
            JsonBuffer.Clear();
            if (flow is null)
            {
                WorkflowFlowJson.WriteEmpty(JsonBuffer);
            }
            else
            {
                WorkflowFlowJson.Write(JsonBuffer, flow);
            }

            _flowJson = UpdateCacheBytes(_flowJson, JsonBuffer.WrittenSpan);

            MermaidBuffer.Clear();
            if (flow is null)
            {
                WorkflowFlowMermaid.WriteEmpty(MermaidBuffer);
            }
            else
            {
                WorkflowFlowMermaid.Write(MermaidBuffer, flow);
            }

            _flowMermaid = UpdateCacheBytes(_flowMermaid, MermaidBuffer.WrittenSpan);

            _yamlHash = yamlHash;
            _filePath = filePath;
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
}
