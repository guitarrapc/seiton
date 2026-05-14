using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Order;
using Seiton.Core.Parsing;
using VYaml.Parser;

namespace Seiton.Benchmark;

[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class CoreParsingBenchmark
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
        using var result = WorkflowParser.Parse(_yamlBytes, _filePath);
        var count = (result.Workflow?.Jobs.Count ?? 0) + result.Diagnostics.Length;
        return count;
    }

    [Benchmark(Description = "ExpressionExtractor.ExtractParseAndValidate")]
    public int ParseExpressionPipeline()
    {
        var result = ExpressionExtractor.ExtractParseAndValidate(_yamlBytes, ExpressionValidationContext.StepRun);
        return result.Occurrences.Length + result.Diagnostics.Length;
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
        => WorkflowYamlBuilder.Build(jobCount, stepsPerJob);
}
