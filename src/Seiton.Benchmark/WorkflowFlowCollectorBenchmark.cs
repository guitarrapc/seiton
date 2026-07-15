using System.Text;
using Seiton.Core.Flow;
using Seiton.Core.Parsing;

namespace Seiton.Benchmark;

/// <summary>Measures flow DTO materialization separately from parsing and serialization.</summary>
[MemoryDiagnoser]
[RankColumn]
public class WorkflowFlowCollectorBenchmark
{
    public enum WorkflowSize
    {
        Small,
        Large,
    }

    private const string FilePath = ".github/workflows/flow-bench.yml";

    [Params(WorkflowSize.Small, WorkflowSize.Large)]
    public WorkflowSize Size { get; set; }

    private ParseResult _parseResult = null!;

    [GlobalSetup]
    public void Setup()
    {
        var jobCount = Size == WorkflowSize.Small ? 3 : 20;
        var yaml = BuildWorkflow(jobCount);
        _parseResult = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), FilePath);
    }

    [GlobalCleanup]
    public void Cleanup() => _parseResult.Dispose();

    [Benchmark]
    public WorkflowFlow Collect() => WorkflowFlowCollector.Collect(_parseResult, FilePath)!;

    private static string BuildWorkflow(int jobCount)
    {
        var sb = new StringBuilder(jobCount * 1024);
        sb.AppendLine("name: Flow benchmark");
        sb.AppendLine("on: [push, workflow_dispatch]");
        sb.AppendLine("permissions:");
        sb.AppendLine("  contents: read");
        sb.AppendLine("jobs:");
        for (var job = 0; job < jobCount; job++)
        {
            sb.Append("  job").Append(job).AppendLine(":");
            if (job == 1)
            {
                sb.AppendLine("    needs: job0");
            }
            else if (job >= 2)
            {
                sb.Append("    needs: [job0, job").Append(job - 1).AppendLine("]");
            }

            sb.AppendLine("    runs-on: ubuntu-latest");
            sb.AppendLine("    permissions:");
            sb.AppendLine("      contents: read");
            sb.AppendLine("      pull-requests: write");
            sb.AppendLine("    strategy:");
            sb.AppendLine("      matrix:");
            sb.AppendLine("        os: [ubuntu-latest, windows-latest]");
            sb.AppendLine("        runtime: [net9.0, net10.0]");
            sb.AppendLine("    steps:");
            sb.AppendLine("      - uses: actions/checkout@v4");
            sb.AppendLine("        with:");
            sb.AppendLine("          fetch-depth: '0'");
            sb.AppendLine("      - name: Build");
            sb.AppendLine("        run: dotnet build");
            sb.AppendLine("      - name: Test");
            sb.AppendLine("        run: dotnet test");
        }

        return sb.ToString().Replace("\r\n", "\n");
    }
}
