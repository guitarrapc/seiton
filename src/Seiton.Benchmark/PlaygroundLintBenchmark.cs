using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Playground;

namespace Seiton.Benchmark;

/// <summary>
/// Measures per-call allocation of <see cref="PlaygroundLintRunner.RunToJson"/> (Utf8JsonWriter path)
/// vs the old List&lt;DTO&gt; + JsonSerializer.Serialize path.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public partial class PlaygroundLintBenchmark
{
    public enum WorkflowSize
    {
        Small,
        Large,
    }

    [Params(WorkflowSize.Small, WorkflowSize.Large)]
    public WorkflowSize Size { get; set; }

    private string _yamlSource = string.Empty;
    private byte[] _yamlBytes = [];
    private const string FilePath = ".github/workflows/bench.yml";

    private LintEngine _engine = null!;
    private static readonly LintConfig BenchConfig = new()
    {
        Fix = new FixConfig { Enabled = true },
        Network = new NetworkConfig(),
        Output = new OutputConfig(),
        SkipSuppressionSummary = true,
    };

    [GlobalSetup]
    public void Setup()
    {
        _yamlSource = Size switch
        {
            WorkflowSize.Small => WorkflowYamlBuilder.Build(jobCount: 1, stepsPerJob: 3),
            WorkflowSize.Large => WorkflowYamlBuilder.Build(jobCount: 6, stepsPerJob: 8),
            _ => WorkflowYamlBuilder.Build(jobCount: 1, stepsPerJob: 3),
        };
        _yamlBytes = Encoding.UTF8.GetBytes(_yamlSource);
        _engine = new LintEngine();

        // Warm up both paths
        PlaygroundLintRunner.RunToJson(_yamlSource, FilePath);
        RunToJsonOld(_engine, _yamlBytes, FilePath);
    }

    [Benchmark(Baseline = true, Description = "RunToJson NEW (Utf8JsonWriter)")]
    public int RunToJson_New_100()
    {
        var totalLength = 0;
        for (var i = 0; i < 100; i++)
        {
            totalLength += PlaygroundLintRunner.RunToJson(_yamlSource, FilePath).Length;
        }

        return totalLength;
    }

    [Benchmark(Description = "RunToJson OLD (List+DTO+Serialize)")]
    public int RunToJson_Old_100()
    {
        var totalLength = 0;
        for (var i = 0; i < 100; i++)
        {
            totalLength += RunToJsonOld(_engine, _yamlBytes, FilePath).Length;
        }

        return totalLength;
    }

    /// <summary>Replicates the old RunToJson path: List&lt;DTO&gt; + JsonSerializer.Serialize.</summary>
    private static string RunToJsonOld(LintEngine engine, byte[] utf8Yaml, string filePath)
    {
        var result = engine.Check(utf8Yaml, filePath, BenchConfig);

        var list = new List<OldDto>(result.Diagnostics.Length);
        for (var i = 0; i < result.Diagnostics.Length; i++)
        {
            var d = result.Diagnostics[i];
            var loc = d.Location;
            list.Add(new OldDto
            {
                Message = d.Message,
                Line = loc.StartLine,
                Column = loc.StartColumn,
                Severity = d.Severity.ToString(),
                RuleId = d.RuleId,
                Fixable = d.Fix is not null,
                FixDescription = d.Fix?.Description,
            });
        }

        result.ParseResult.Arena?.Dispose();
        return JsonSerializer.Serialize(list, OldJsonContext.Default.ListOldDto);
    }

    /// <summary>DTO matching the old PlaygroundDiagnosticDto shape.</summary>
    public sealed class OldDto
    {
        public required string Message { get; init; }
        public required int Line { get; init; }
        public required int Column { get; init; }
        public required string Severity { get; init; }
        public string? RuleId { get; init; }
        public bool Fixable { get; init; }
        public string? FixDescription { get; init; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(List<OldDto>))]
    internal partial class OldJsonContext : JsonSerializerContext;
}
