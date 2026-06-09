using BenchmarkDotNet.Order;
using Seiton.Cli;
using Seiton.Commands;
namespace Seiton.Benchmark;

/// <summary>
/// Benchmarks auto-discovery in <see cref="InputDiscovery"/> (CWD-scoped).
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class InputDiscoveryBenchmark
{
    private string _cwd = string.Empty;
    private string _nestedCwd = string.Empty;
    private readonly VerboseLogger _logger = VerboseLogger.Create(verbose: false, TextWriter.Null);

    [GlobalSetup]
    public void Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "seiton-bench-discovery", Guid.NewGuid().ToString("N"));
        _cwd = root;
        _nestedCwd = Path.Combine(root, "LogicLooper");

        Directory.CreateDirectory(Path.Combine(root, ".github", "actions", "parent-action"));
        File.WriteAllText(
            Path.Combine(root, ".github", "actions", "parent-action", "action.yaml"),
            "name: parent\nruns: { using: composite, steps: [] }");

        Directory.CreateDirectory(Path.Combine(_nestedCwd, ".github", "workflows"));
        for (var i = 0; i < 32; i++)
        {
            File.WriteAllText(
                Path.Combine(_nestedCwd, ".github", "workflows", $"workflow-{i:D2}.yaml"),
                $"on: push\njobs:\n  job{i}:\n    runs-on: ubuntu-24.04\n    steps:\n      - run: echo {i}\n");
        }
    }

    [Benchmark(Description = "ResolveFiles (cwd, workflows only)")]
    public int ResolveFiles_Cwd_WorkflowsOnly()
    {
        var files = InputDiscovery.ResolveFiles([], includeActions: false, _logger, startDirectory: _cwd);
        return files.Length;
    }

    [Benchmark(Description = "ResolveFiles (nested cwd, include-actions)")]
    public int ResolveFiles_NestedCwd_IncludeActions()
    {
        var files = InputDiscovery.ResolveFiles([], includeActions: true, _logger, startDirectory: _nestedCwd);
        return files.Length;
    }

    [Benchmark(Description = "ShouldSuggestIncludeActions (actions dir exists)")]
    public bool ShouldSuggestIncludeActions_ActionsDirExists()
    {
        return CheckCommand.ShouldSuggestIncludeActions(includeActions: false, discoveryStartDirectory: _cwd);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_cwd))
        {
            Directory.Delete(_cwd, recursive: true);
        }
    }
}
