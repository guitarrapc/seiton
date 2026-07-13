using System.Buffers;
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
    /// <summary>Guards the shared buffers and the identity cache (WASM is single-threaded; desktop tests are not).</summary>
    private static readonly object FlowGate = new();

    /// <summary>Reusable buffer for JSON serialization. Guarded by <see cref="FlowGate"/>.</summary>
    private static readonly ArrayBufferWriter<byte> JsonBuffer = new(4096);

    /// <summary>Reusable buffer for UTF-8 encoding. Guarded by <see cref="FlowGate"/>.</summary>
    private static byte[]? _utf8Buf;

    // ─── Identity-based short circuit (mirrors PlaygroundLintRunner) ───
    private static string? _lastYamlSource;
    private static string? _lastFilePath;
    private static byte[]? _lastJsonOutput;

    /// <summary>Clears shared caches between playground tests.</summary>
    internal static void ResetSharedStateForTests()
    {
        lock (FlowGate)
        {
            _lastYamlSource = null;
            _lastFilePath = null;
            _lastJsonOutput = null;
        }
    }

    /// <summary>
    /// Parses <paramref name="yamlSource"/> and returns the flow-json document as UTF-8 bytes.
    /// Non-workflow documents (e.g. action.yml) produce an empty <c>workflows</c> array.
    /// </summary>
    public static byte[] RunFlowToJsonUtf8(string yamlSource, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        lock (FlowGate)
        {
            // Fast path: if source and filePath are identical to last call, return cached output
            if (ReferenceEquals(yamlSource, _lastYamlSource)
                && string.Equals(filePath, _lastFilePath, StringComparison.Ordinal)
                && _lastJsonOutput is not null)
            {
                return _lastJsonOutput;
            }

            var utf8Yaml = EncodeToReusableBuffer(yamlSource);

            byte[] result;
            using (var parseResult = WorkflowParser.Parse(utf8Yaml, filePath))
            {
                var flow = WorkflowFlowCollector.Collect(parseResult, filePath);
                JsonBuffer.Clear();
                WorkflowFlowJson.Write(JsonBuffer, flow is null ? [] : [flow]);
                var written = JsonBuffer.WrittenSpan;
                result = _lastJsonOutput is not null && written.SequenceEqual(_lastJsonOutput)
                    ? _lastJsonOutput
                    : written.ToArray();
            }

            _lastYamlSource = yamlSource;
            _lastFilePath = filePath;
            _lastJsonOutput = result;

            return result;
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
