using BenchmarkDotNet.Order;
using Seiton.Core.Linting;
using System.Text;

namespace Seiton.Benchmark;

/// <summary>
/// Benchmarks for lint-config YAML parsing and validation pipeline.
/// Covers: YAML parse only, parse + normalize (full Validate), and RuleCatalog lookups.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class LintConfigBenchmark
{
    public enum ConfigComplexity
    {
        Minimal,
        Typical,
        Heavy,
    }

    [Params(ConfigComplexity.Minimal, ConfigComplexity.Typical, ConfigComplexity.Heavy)]
    public ConfigComplexity Complexity { get; set; }

    private byte[] _configUtf8 = [];
    private string _configText = string.Empty;
    private const string FilePath = "bench-config.yml";

    [GlobalSetup]
    public void Setup()
    {
        _configText = Complexity switch
        {
            ConfigComplexity.Minimal => ConfigYamlBuilder.BuildMinimal(),
            ConfigComplexity.Typical => ConfigYamlBuilder.BuildTypical(),
            ConfigComplexity.Heavy => ConfigYamlBuilder.BuildHeavy(),
            _ => ConfigYamlBuilder.BuildMinimal(),
        };
        _configUtf8 = Encoding.UTF8.GetBytes(_configText);
    }

    [Benchmark(Baseline = true, Description = "LintConfigYamlParser.Parse (parse only)")]
    public int ParseOnly()
    {
        var result = LintConfigYamlParser.Parse(_configUtf8.AsMemory(), FilePath);
        return result.Rules!.Count + result.Diagnostics.Length;
    }

    [Benchmark(Description = "LintConfigLibrary.Validate (parse + normalize)")]
    public int Validate()
    {
        var result = LintConfigLibrary.Validate(_configText, FilePath);
        return (result.Config?.Rules?.Count ?? 0) + result.Diagnostics.Length;
    }
}

/// <summary>
/// Micro-benchmarks for RuleCatalog hot-path lookups.
/// These are called per-diagnostic during sorting and per-rule during normalization.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class RuleCatalogBenchmark
{
    private string _ruleIdString = null!;
    private RuleId _ruleId;

    [GlobalSetup]
    public void Setup()
    {
        _ruleId = RuleId.DangerousTriggers;
        _ruleIdString = _ruleId.ToId();
    }

    [Benchmark(Baseline = true, Description = "GetPriority(string)")]
    public int GetPriorityByString()
    {
        return RuleCatalog.GetPriority(_ruleIdString);
    }

    [Benchmark(Description = "TryResolveRuleId(string)")]
    public bool TryResolveRuleId()
    {
        return RuleCatalog.TryResolveRuleId(_ruleIdString, out _);
    }

    [Benchmark(Description = "IsNonDisableable(RuleId)")]
    public bool IsNonDisableable()
    {
        return RuleCatalog.IsNonDisableable(_ruleId);
    }

    [Benchmark(Description = "IsOptIn(string)")]
    public bool IsOptIn()
    {
        return RuleCatalog.IsOptIn(_ruleIdString);
    }

    [Benchmark(Description = "RuleId.ToId()")]
    public string ToId()
    {
        return _ruleId.ToId();
    }

    [Benchmark(Description = "RuleIdExtensions.TryParse(string)")]
    public bool TryParse()
    {
        return RuleIdExtensions.TryParse(_ruleIdString, out _);
    }
}
