using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Order;
using Seiton.Core.Parsing;
using VYaml.Parser;

namespace Seiton.Benchmark;

[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class ParsingBenchmark
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

    [GlobalSetup]
    public void Setup()
    {
        var yaml = Size switch
        {
            WorkflowSize.Small => BuildWorkflowYaml(jobCount: 1, stepsPerJob: 3),
            WorkflowSize.Medium => BuildWorkflowYaml(jobCount: 6, stepsPerJob: 8),
            WorkflowSize.Large => BuildWorkflowYaml(jobCount: 20, stepsPerJob: 12),
            _ => BuildWorkflowYaml(jobCount: 1, stepsPerJob: 3),
        };

        _yamlBytes = System.Text.Encoding.UTF8.GetBytes(yaml);
        _filePath = $"bench-{Size.ToString().ToLowerInvariant()}.yml";
    }

    [Benchmark(Baseline = true, Description = "WorkflowParser.Parse (AST + rules)")]
    public int ParseWorkflowFull()
    {
        var result = WorkflowParser.Parse(_yamlBytes, _filePath);
        return (result.Workflow?.Jobs.Count ?? 0) + result.Diagnostics.Length;
    }

    [Benchmark(Description = "ExpressionExtractor.ExtractParseAndValidate")]
    public int ParseExpressionPipeline()
    {
        var result = ExpressionExtractor.ExtractParseAndValidate(_yamlBytes, ExpressionValidationContext.Step);
        return result.Occurrences.Length + result.Diagnostics.Length;
    }

    [Benchmark(Description = "VYaml raw event scan")]
    public int ScanWithVYamlRaw()
    {
        var parser = YamlParser.FromBytes(_yamlBytes.AsMemory());
        var eventCount = 0;
        while (parser.Read())
        {
            eventCount++;
        }

        return eventCount;
    }

    [Benchmark(Description = "VYaml scan + adapter-like mapping")]
    public int ScanWithVYamlMapped()
    {
        var parser = YamlParser.FromBytes(_yamlBytes.AsMemory());
        var mappedCount = 0;
        while (parser.Read())
        {
            _ = MapEventKind(parser.CurrentEventType);
            mappedCount++;
        }

        return mappedCount;
    }

    private static int MapEventKind(ParseEventType eventType)
    {
        return eventType switch
        {
            ParseEventType.StreamStart => 1,
            ParseEventType.StreamEnd => 2,
            ParseEventType.DocumentStart => 3,
            ParseEventType.DocumentEnd => 4,
            ParseEventType.MappingStart => 5,
            ParseEventType.MappingEnd => 6,
            ParseEventType.SequenceStart => 7,
            ParseEventType.SequenceEnd => 8,
            ParseEventType.Scalar => 9,
            ParseEventType.Alias => 10,
            _ => 0,
        };
    }

    private static string BuildWorkflowYaml(int jobCount, int stepsPerJob)
    {
        var sb = new System.Text.StringBuilder(capacity: 8_192);
        sb.AppendLine("name: bench");
        sb.AppendLine("run-name: Bench ${{ github.ref_name }}");
        sb.AppendLine("on:");
        sb.AppendLine("  push:");
        sb.AppendLine("    branches: [main, release/**]");
        sb.AppendLine("  workflow_dispatch:");
        sb.AppendLine("    inputs:");
        sb.AppendLine("      target:");
        sb.AppendLine("        type: choice");
        sb.AppendLine("        options: [dev, prod]");
        sb.AppendLine("        default: dev");
        sb.AppendLine("permissions:");
        sb.AppendLine("  contents: read");
        sb.AppendLine("env:");
        sb.AppendLine("  GLOBAL: value");
        sb.AppendLine("defaults:");
        sb.AppendLine("  run:");
        sb.AppendLine("    shell: bash");
        sb.AppendLine("concurrency:");
        sb.AppendLine("  group: bench-${{ github.ref }}");
        sb.AppendLine("  cancel-in-progress: true");
        sb.AppendLine("jobs:");

        for (var j = 0; j < jobCount; j++)
        {
            sb.Append("  job").Append(j).AppendLine(":");
            sb.AppendLine("    name: Build");
            sb.AppendLine("    runs-on: ubuntu-latest");
            sb.AppendLine("    timeout-minutes: 30");
            sb.AppendLine("    continue-on-error: false");
            sb.AppendLine("    strategy:");
            sb.AppendLine("      fail-fast: true");
            sb.AppendLine("      max-parallel: 2");
            sb.AppendLine("      matrix:");
            sb.AppendLine("        os: [ubuntu-latest, windows-latest]");
            sb.AppendLine("    steps:");

            for (var s = 0; s < stepsPerJob; s++)
            {
                if ((s & 1) == 0)
                {
                    sb.AppendLine("      - name: Run");
                    sb.AppendLine("        if: ${{ startsWith(github.ref, 'refs/heads/') && success() }}");
                    sb.AppendLine("        run: echo ${{ matrix.os }}");
                    sb.AppendLine("        env:");
                    sb.AppendLine("          STEP_ENV: ${{ github.sha }}");
                }
                else
                {
                    sb.AppendLine("      - name: Action");
                    sb.AppendLine("        uses: actions/checkout@v4");
                    sb.AppendLine("        with:");
                    sb.AppendLine("          fetch-depth: '0'");
                    sb.AppendLine("        if: ${{ !cancelled() && github.event_name == 'push' }}");
                }
            }
        }

        return sb.ToString().Replace("\r\n", "\n");
    }
}
