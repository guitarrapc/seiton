using Seiton.Core.Linting;
using Seiton.Core.Flow;
using Seiton.Core.Parsing;
using Seiton.Output;
using System.Text;

namespace Seiton.Benchmark;

/// <summary>
/// Baseline for CLI diagnostic/flow formatting across output formats (text, github-actions, sarif, json, flow-json, flow-mermaid).
/// Compare Mean and Allocated after output-path optimizations.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class DiagnosticOutputBenchmark
{
    public enum FileCount
    {
        F1 = 1,
        F10 = 10,
    }

    [Params(FileCount.F1, FileCount.F10)]
    public FileCount Count { get; set; }

    private Diagnostic[] _diagnostics = [];
    private WorkflowFlow[] _flows = [];
    private Dictionary<string, byte[]> _sourceMap = new(StringComparer.Ordinal);

    [GlobalSetup]
    public void Setup()
    {
        var n = (int)Count;
        var engine = new LintEngine();
        var list = new List<Diagnostic>(capacity: 256);
        var flows = new List<WorkflowFlow>(capacity: n);
        _sourceMap = new Dictionary<string, byte[]>(n, StringComparer.Ordinal);

        for (var i = 0; i < n; i++)
        {
            var yaml = WorkflowYamlBuilder.Build(jobCount: 6, stepsPerJob: 8, nameSuffix: $"-fmt{i}");
            var bytes = Encoding.UTF8.GetBytes(yaml);
            var relativePath = $".github/workflows/bench-fmt-{i}.yml";
            var path = Path.GetFullPath(relativePath);
            _sourceMap[path] = bytes;

            using var result = engine.Check(bytes, path, new LintConfig { Utf8Yaml = bytes, FilePath = path });
            if (result.Diagnostics.Length > 0)
            {
                list.AddRange(result.Diagnostics);
            }

            using var parseResult = WorkflowParser.Parse(bytes, path);
            if (WorkflowFlowCollector.Collect(parseResult, path) is { } flow)
            {
                flows.Add(flow);
            }
        }

        _diagnostics = [.. list];
        _flows = [.. flows];
    }

    [Benchmark(Baseline = true, Description = "DiagnosticFormatter text rich")]
    public int WriteTextRich()
    {
        using var buffer = new PooledByteBufferWriter(16_384);
        DiagnosticFormatter.Write(buffer, _diagnostics, OutputFormat.Text, oneline: false, color: false, _sourceMap);
        return buffer.WrittenSpan.Length;
    }

    [Benchmark(Description = "DiagnosticFormatter github-actions rich")]
    public int WriteGitHubActionsRich()
    {
        using var buffer = new PooledByteBufferWriter(16_384);
        DiagnosticFormatter.Write(buffer, _diagnostics, OutputFormat.GitHubActions, oneline: false, color: false, _sourceMap);
        return buffer.WrittenSpan.Length;
    }

    [Benchmark(Description = "DiagnosticFormatter github-actions oneline")]
    public int WriteGitHubActionsOneline()
    {
        using var buffer = new PooledByteBufferWriter(16_384);
        DiagnosticFormatter.Write(buffer, _diagnostics, OutputFormat.GitHubActions, oneline: true, color: false, _sourceMap);
        return buffer.WrittenSpan.Length;
    }

    [Benchmark(Description = "DiagnosticFormatter text oneline")]
    public int WriteTextOneline()
    {
        using var buffer = new PooledByteBufferWriter(16_384);
        DiagnosticFormatter.Write(buffer, _diagnostics, OutputFormat.Text, oneline: true, color: false, _sourceMap);
        return buffer.WrittenSpan.Length;
    }

    [Benchmark(Description = "DiagnosticFormatter sarif")]
    public int WriteSarif()
    {
        using var buffer = new PooledByteBufferWriter(16_384);
        DiagnosticFormatter.Write(buffer, _diagnostics, OutputFormat.Sarif, oneline: false, color: false, _sourceMap);
        return buffer.WrittenSpan.Length;
    }

    [Benchmark(Description = "DiagnosticFormatter json")]
    public int WriteJson()
    {
        using var buffer = new PooledByteBufferWriter(16_384);
        DiagnosticFormatter.Write(buffer, _diagnostics, OutputFormat.Json, oneline: false, color: false, _sourceMap);
        return buffer.WrittenSpan.Length;
    }

    [Benchmark(Description = "WorkflowFlowJson flow-json")]
    public int WriteFlowJson()
    {
        using var buffer = new PooledByteBufferWriter(16_384);
        WorkflowFlowJson.Write(buffer, _flows);
        return buffer.WrittenSpan.Length;
    }

    [Benchmark(Description = "WorkflowFlowMermaid flow-mermaid")]
    public int WriteFlowMermaid()
    {
        var output = WorkflowFlowMermaid.Serialize(_flows);
        return output.Length;
    }
}
