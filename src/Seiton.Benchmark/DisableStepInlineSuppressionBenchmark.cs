using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;
using System.Text;

namespace Seiton.Benchmark;

[MemoryDiagnoser]
[RankColumn]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class DisableStepInlineSuppressionBenchmark
{
    public enum WorkflowSize
    {
        Small,
        Medium,
        Large,
    }

    [Params(WorkflowSize.Small, WorkflowSize.Medium, WorkflowSize.Large)]
    public WorkflowSize Size { get; set; }

    private byte[] _yamlBytes = [];
    private string _filePath = string.Empty;
    private LintEngine _engine = null!;

    [GlobalSetup]
    public void Setup()
    {
        var (jobCount, stepsPerJob) = Size switch
        {
            WorkflowSize.Small => (1, 3),
            WorkflowSize.Medium => (6, 8),
            WorkflowSize.Large => (20, 12),
            _ => (1, 3),
        };

        var yaml = BuildWorkflowWithDisableStep(jobCount, stepsPerJob);
        _yamlBytes = Encoding.UTF8.GetBytes(yaml);
        _filePath = $"bench-disable-step-{Size.ToString().ToLowerInvariant()}.yml";
        _engine = new LintEngine([new UnredactedSecretsRule()]);
    }

    [Benchmark(Description = "LintEngine.Check disable-step (parse + lint)")]
    public int CheckWorkflow()
    {
        using var result = _engine.Check(_yamlBytes, _filePath);
        return result.SuppressionSummary.TotalSuppressed;
    }

    private static string BuildWorkflowWithDisableStep(int jobCount, int stepsPerJob)
    {
        var sb = new StringBuilder(capacity: 16_384);
        sb.AppendLine("name: bench-disable-step");
        sb.AppendLine("on: push");
        sb.AppendLine("jobs:");

        for (var job = 0; job < jobCount; job++)
        {
            sb.Append("  job").Append(job).AppendLine(":");
            sb.AppendLine("    runs-on: ubuntu-24.04");
            sb.AppendLine("    timeout-minutes: 10");
            sb.AppendLine("    permissions: {}");
            sb.AppendLine("    env:");
            sb.Append("      TOKEN_").Append(job).Append(": ${{ secrets.TOKEN_").Append(job).AppendLine(" }}");
            sb.AppendLine("    steps:");

            for (var step = 0; step < stepsPerJob; step++)
            {
                sb.AppendLine("      # seiton: disable-step unredacted-secrets");
                sb.Append("      - name: Step ").Append(step).AppendLine();
                sb.AppendLine("        run: |");
                sb.Append("          echo \"${TOKEN_").Append(job).AppendLine("}\" > secret.txt");
                sb.AppendLine("          chmod 600 secret.txt");
            }
        }

        return sb.ToString().Replace("\r\n", "\n");
    }
}
