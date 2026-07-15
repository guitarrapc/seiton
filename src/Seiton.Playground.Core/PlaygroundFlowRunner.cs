using Seiton.Core.Parsing;

namespace Seiton.Playground;

/// <summary>
/// Produces the flow-json document (same contract as <c>seiton check --format flow-json</c>)
/// for the playground flow tab. Kept separate from <see cref="PlaygroundLintRunner"/> so the
/// lint diagnostics API and the flow API stay independent contracts.
/// </summary>
public static class PlaygroundFlowRunner
{
    /// <summary>Guards flow-only parse path (WASM is single-threaded; desktop tests are not).</summary>
    private static readonly object FlowGate = new();

    /// <summary>Caller-owned UTF-8 source buffer. Guarded by <see cref="FlowGate"/>.</summary>
    private static byte[] _utf8Yaml = [];

    /// <summary>Clears shared caches between playground tests.</summary>
    internal static void ResetSharedStateForTests()
    {
        lock (FlowGate)
        {
            _utf8Yaml = [];
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

    private static (byte[] Json, byte[] Mermaid) EnsureOutputs(string yamlSource, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        lock (FlowGate)
        {
            var (utf8Yaml, yamlHash) = PlaygroundUtf8Scratch.EncodeAndHash(yamlSource, ref _utf8Yaml);
            if (PlaygroundFlowOutputCache.TryGet(yamlHash, filePath, out var cachedJson, out var cachedMermaid))
            {
                return (cachedJson, cachedMermaid);
            }

            using var parseResult = WorkflowParser.Parse(utf8Yaml, filePath);
            return PlaygroundFlowOutputCache.Store(yamlHash, filePath, parseResult.Workflow);
        }
    }
}
