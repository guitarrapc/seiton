using Seiton.Core.Linting;
using System.Text;

namespace Seiton.Benchmark;

[MemoryDiagnoser]
[RankColumn]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class CoreLintBenchmark
{
    public enum WorkflowSize
    {
        Small,
        Medium,
        Large,
    }

    [Params(WorkflowSize.Small, WorkflowSize.Medium, WorkflowSize.Large)]
    public WorkflowSize Size { get; set; }

    [Params(false, true)]
    public bool FixEnabled { get; set; }

    private byte[] _yamlBytes = [];
    private string _filePath = string.Empty;
    private LintEngine _engine = null!;
    private LintConfig _lintConfig = null!;

    [GlobalSetup]
    public void Setup()
    {
        var yaml = Size switch
        {
            WorkflowSize.Small => WorkflowYamlBuilder.Build(jobCount: 1, stepsPerJob: 3),
            WorkflowSize.Medium => WorkflowYamlBuilder.Build(jobCount: 6, stepsPerJob: 8),
            WorkflowSize.Large => WorkflowYamlBuilder.Build(jobCount: 20, stepsPerJob: 12),
            _ => WorkflowYamlBuilder.Build(jobCount: 1, stepsPerJob: 3),
        };

        _yamlBytes = Encoding.UTF8.GetBytes(yaml);
        _filePath = $"bench-lint-{Size.ToString().ToLowerInvariant()}.yml";
        _engine = new LintEngine();
        _lintConfig = new LintConfig
        {
            Utf8Yaml = _yamlBytes,
            FilePath = _filePath,
            Fix = new FixConfig
            {
                Enabled = FixEnabled,
                Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 }
            }
        };
    }

    [Benchmark(Baseline = true, Description = "LintEngine.Check (parse + lint)")]
    public int CheckWorkflow()
    {
        var result = _engine.Check(_yamlBytes, _filePath, _lintConfig);
        return result.Diagnostics.Length;
    }
}
