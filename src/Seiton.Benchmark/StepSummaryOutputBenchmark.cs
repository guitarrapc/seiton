using Seiton.Commands;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Output;
using System.Text;

namespace Seiton.Benchmark;

/// <summary>
/// Step summary append path (GITHUB_STEP_SUMMARY) vs stderr summary for phase 3 comparison.
/// </summary>
[MemoryDiagnoser]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class StepSummaryOutputBenchmark
{
    private List<Diagnostic> _diagnostics = [];
    private string _summaryPath = "";

    [GlobalSetup]
    public void Setup()
    {
        var engine = new LintEngine();
        var yaml = WorkflowYamlBuilder.Build(jobCount: 6, stepsPerJob: 8, nameSuffix: "-summary");
        var bytes = Encoding.UTF8.GetBytes(yaml);
        var path = ".github/workflows/bench-summary.yml";
        using var result = engine.Check(bytes, path, new LintConfig { Utf8Yaml = bytes, FilePath = path });
        _diagnostics = result.Diagnostics.Length > 0 ? [.. result.Diagnostics] : [];

        _summaryPath = Path.Combine(Path.GetTempPath(), $"seiton-step-summary-{Guid.NewGuid():N}.md");
        Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", _summaryPath);
        GitHubStepSummaryWriter.Reset();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", null);
        if (File.Exists(_summaryPath))
            File.Delete(_summaryPath);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        GitHubStepSummaryWriter.Reset();
        if (File.Exists(_summaryPath))
            File.Delete(_summaryPath);
    }

    [Benchmark(Baseline = true, Description = "WriteSummary stderr (text)")]
    public int WriteSummaryStderr()
    {
        var sb = new StringBuilder(capacity: 512);
        using var writer = new StringWriter(sb);
        CheckCommand.WriteSummary(writer, _diagnostics, fileCount: 1, OutputFormat.Text, showPerFile: true);
        return sb.Length;
    }

    [Benchmark(Description = "WriteSummary step summary (github-actions)")]
    public int WriteSummaryStepSummary()
    {
        var stderr = new StringBuilder(capacity: 64);
        using var writer = new StringWriter(stderr);
        CheckCommand.WriteSummary(writer, _diagnostics, fileCount: 1, OutputFormat.GitHubActions, showPerFile: true);
        return File.Exists(_summaryPath) ? (int)new FileInfo(_summaryPath).Length : 0;
    }
}
