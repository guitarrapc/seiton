using System.Text;
using Seiton.Core.Flow;
using Seiton.Core.Parsing;

namespace Seiton.Playground;

/// <summary>
/// Produces the flow-json document (same contract as <c>seiton check --format flow-json</c>)
/// for the playground flow tab. Kept separate from <see cref="PlaygroundLintRunner"/> so the
/// lint diagnostics API and the flow API stay independent contracts.
/// </summary>
public static class PlaygroundFlowRunner
{
    /// <summary>Guards UTF-8 encoding buffer (WASM is single-threaded; desktop tests are not).</summary>
    private static readonly object FlowGate = new();

    /// <summary>Reusable buffer for UTF-8 encoding. Guarded by <see cref="FlowGate"/>.</summary>
    private static byte[]? _utf8Buf;

    /// <summary>Clears shared caches between playground tests.</summary>
    internal static void ResetSharedStateForTests()
    {
        lock (FlowGate)
        {
            _utf8Buf = null;
        }

        PlaygroundFlowOutputCache.ResetForTests();
    }

    /// <summary>
    /// Parses <paramref name="yamlSource"/> and returns the flow-json document as UTF-8 bytes.
    /// Non-workflow documents (e.g. action.yml) produce an empty <c>workflows</c> array.
    /// </summary>
    public static byte[] RunFlowToJsonUtf8(string yamlSource, string filePath)
    {
        return EnsureOutputs(yamlSource, filePath).Json;
    }

    /// <summary>
    /// Parses <paramref name="yamlSource"/> and returns Mermaid flowchart text as UTF-8 bytes
    /// (same contract as <c>seiton check --format flow-mermaid</c>).
    /// Non-workflow documents produce a minimal <c>flowchart LR</c> diagram with no jobs.
    /// </summary>
    public static byte[] RunFlowToMermaidUtf8(string yamlSource, string filePath)
    {
        return EnsureOutputs(yamlSource, filePath).Mermaid;
    }

    /// <summary>
    /// Stores flow outputs produced during a combined lint+flow parse in
    /// <see cref="PlaygroundLintRunner"/>.
    /// </summary>
    internal static void StoreFlowFromLint(ulong yamlHash, string filePath, WorkflowFlow? flow)
    {
        PlaygroundFlowOutputCache.Store(yamlHash, filePath, flow);
    }

    private static (byte[] Json, byte[] Mermaid) EnsureOutputs(string yamlSource, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var yamlHash = PlaygroundYamlHash.Compute(yamlSource);
        if (PlaygroundFlowOutputCache.TryGet(yamlHash, filePath, out var cachedJson, out var cachedMermaid))
        {
            return (cachedJson, cachedMermaid);
        }

        lock (FlowGate)
        {
            if (PlaygroundFlowOutputCache.TryGet(yamlHash, filePath, out cachedJson, out cachedMermaid))
            {
                return (cachedJson, cachedMermaid);
            }

            var utf8Yaml = EncodeToReusableBuffer(yamlSource);
            using var parseResult = WorkflowParser.Parse(utf8Yaml, filePath);
            var flow = WorkflowFlowCollector.Collect(parseResult, filePath);
            PlaygroundFlowOutputCache.Store(yamlHash, filePath, flow);
            PlaygroundFlowOutputCache.TryGet(yamlHash, filePath, out cachedJson, out cachedMermaid);
            return (cachedJson, cachedMermaid);
        }
    }

    /// <summary>
    /// Encodes <paramref name="source"/> into the reusable buffer and returns it.
    /// Only allocates when the byte length changes from the last call.
    /// Must be called under <see cref="FlowGate"/>.
    /// </summary>
    private static byte[] EncodeToReusableBuffer(string source)
    {
        var byteCount = Encoding.UTF8.GetByteCount(source);
        if (_utf8Buf is null || _utf8Buf.Length != byteCount)
        {
            _utf8Buf = new byte[byteCount];
        }

        Encoding.UTF8.GetBytes(source, _utf8Buf);
        return _utf8Buf;
    }
}
