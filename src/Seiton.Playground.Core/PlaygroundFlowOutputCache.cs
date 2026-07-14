using System.Buffers;
using System.Text;
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
            WorkflowFlowJson.Write(JsonBuffer, flow is null ? [] : [flow]);
            var jsonWritten = JsonBuffer.WrittenSpan;
            var json = _flowJson is not null && jsonWritten.SequenceEqual(_flowJson)
                ? _flowJson
                : jsonWritten.ToArray();

            MermaidBuffer.Clear();
            WorkflowFlowMermaid.Write(MermaidBuffer, flow is null ? [] : [flow]);
            var mermaidWritten = MermaidBuffer.WrittenSpan;
            var mermaid = _flowMermaid is not null && mermaidWritten.SequenceEqual(_flowMermaid)
                ? _flowMermaid
                : mermaidWritten.ToArray();

            _yamlHash = yamlHash;
            _filePath = filePath;
            _flowJson = json;
            _flowMermaid = mermaid;
        }
    }
}
