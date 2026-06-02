using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Output;
using System.Text;

namespace Seiton.Benchmark;

/// <summary>
/// Baseline for CLI diagnostic formatting across output formats (text, github-actions, sarif, json).
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
    private Dictionary<string, byte[]> _sourceMap = new(StringComparer.Ordinal);

    [GlobalSetup]
    public void Setup()
    {
        var n = (int)Count;
        var engine = new LintEngine();
        var list = new List<Diagnostic>(capacity: 256);
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
        }

        _diagnostics = [.. list];
    }

    [Benchmark(Baseline = true, Description = "DiagnosticFormatter text rich")]
    public int WriteTextRich()
    {
        var sb = new StringBuilder(capacity: 16_384);
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.Write(writer, _diagnostics, OutputFormat.Text, oneline: false, color: false, _sourceMap);
        return sb.Length;
    }

    [Benchmark(Description = "DiagnosticFormatter github-actions rich")]
    public int WriteGitHubActionsRich()
    {
        var sb = new StringBuilder(capacity: 16_384);
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.Write(writer, _diagnostics, OutputFormat.GitHubActions, oneline: false, color: false, _sourceMap);
        return sb.Length;
    }

    [Benchmark(Description = "DiagnosticFormatter github-actions oneline")]
    public int WriteGitHubActionsOneline()
    {
        var sb = new StringBuilder(capacity: 16_384);
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.Write(writer, _diagnostics, OutputFormat.GitHubActions, oneline: true, color: false, _sourceMap);
        return sb.Length;
    }

    [Benchmark(Description = "DiagnosticFormatter text oneline")]
    public int WriteTextOneline()
    {
        var sb = new StringBuilder(capacity: 16_384);
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.Write(writer, _diagnostics, OutputFormat.Text, oneline: true, color: false, _sourceMap);
        return sb.Length;
    }

    [Benchmark(Description = "DiagnosticFormatter sarif")]
    public int WriteSarif()
    {
        var sb = new StringBuilder(capacity: 16_384);
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.Write(writer, _diagnostics, OutputFormat.Sarif, oneline: false, color: false, _sourceMap);
        return sb.Length;
    }

    [Benchmark(Description = "DiagnosticFormatter json")]
    public int WriteJson()
    {
        var sb = new StringBuilder(capacity: 16_384);
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.Write(writer, _diagnostics, OutputFormat.Json, oneline: false, color: false, _sourceMap);
        return sb.Length;
    }
}
