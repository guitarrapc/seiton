using BenchmarkDotNet.Order;
using Seiton.Core.Linting;
using System.Text;

namespace Seiton.Benchmark;

/// <summary>
/// Benchmarks cross-file exclusion job-id validation used by <c>seiton validate-config</c>.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class ExclusionJobIdValidatorBenchmark
{
    private string _configYaml = null!;
    private string[] _workflowPaths = null!;
    private const string ConfigPath = "bench-seiton.yaml";

    [GlobalSetup]
    public void Setup()
    {
        _configYaml = """
            exclusions:
              - file: .github/workflows/ci-*.yml
                jobs:
                  - build
                  - deploy
                rules:
                  - deny-inherit-secrets
              - file: .github/workflows/release.yml
                jobs:
                  - publish
                rules:
                  - unpinned-uses
            """;

        var validation = LintConfigLibrary.Validate(_configYaml, ConfigPath);
        if (!validation.IsValid || validation.Config is null)
        {
            throw new InvalidOperationException("Benchmark config is invalid");
        }

        var root = Path.Combine(Path.GetTempPath(), "seiton-bench-jobid", Guid.NewGuid().ToString("N"));
        _workflowPaths =
        [
            WriteWorkflow(root, "ci-a.yml", "build"),
            WriteWorkflow(root, "ci-b.yml", "build", "deploy"),
            WriteWorkflow(root, "release.yml", "publish"),
            WriteWorkflow(root, "nightly.yml", "scan"),
        ];
    }

    [Benchmark(Baseline = true, Description = "LintConfigLibrary.Validate only")]
    public int ValidateConfigOnly()
    {
        var result = LintConfigLibrary.Validate(_configYaml, ConfigPath);
        return result.Diagnostics.Length;
    }

    [Benchmark(Description = "Validate + ExclusionJobIdValidator (4 workflows)")]
    public int ValidateWithJobIdCrossCheck()
    {
        var result = LintConfigLibrary.Validate(_configYaml, ConfigPath);
        var jobIdDiags = ExclusionJobIdValidator.Validate(
            result.Config,
            _workflowPaths,
            ConfigPath,
            out _);
        return result.Diagnostics.Length + jobIdDiags.Length;
    }

    private static string WriteWorkflow(string repositoryRoot, string fileName, params string[] jobIds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("on: push");
        sb.AppendLine("jobs:");
        for (var i = 0; i < jobIds.Length; i++)
        {
            sb.AppendLine($"  {jobIds[i]}:");
            sb.AppendLine("    runs-on: ubuntu-latest");
            sb.AppendLine("    steps:");
            sb.AppendLine("      - run: echo ok");
        }

        var path = Path.Combine(repositoryRoot, ".github", "workflows", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
