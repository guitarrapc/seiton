using System.Text;
using Seiton.Core.Linting;
using Seiton.Playground;

namespace Seiton.Benchmark;

/// <summary>
/// Measures per-call allocation in incremental editing scenarios.
/// Simulates the Playground pattern: repeated lint calls with small edits between each call.
/// This benchmark provides a baseline for D-5b (selective section skip) to compare against.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class IncrementalParseBenchmark
{
    public enum WorkflowSize
    {
        Small,
        Large,
    }

    [Params(WorkflowSize.Small, WorkflowSize.Large)]
    public WorkflowSize Size { get; set; }

    private byte[][] _editSequence = [];
    private byte[] _stableSource = [];
    private const string FilePath = ".github/workflows/bench.yml";
    private const int EditCount = 20;

    private LintEngine _engine = null!;
    private IncrementalParseContext _ctx = null!;

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
        var baseYaml = Size switch
        {
            WorkflowSize.Small => WorkflowYamlBuilder.Build(jobCount: 1, stepsPerJob: 3),
            WorkflowSize.Large => WorkflowYamlBuilder.Build(jobCount: 6, stepsPerJob: 8),
            _ => WorkflowYamlBuilder.Build(jobCount: 1, stepsPerJob: 3),
        };

        _stableSource = Encoding.UTF8.GetBytes(baseYaml);

        // Build a sequence of small edits simulating typing in the last step's run command.
        // Each edit appends a character to "echo ${{ matrix.os }}" → "echo ${{ matrix.os }}X", "echo ${{ matrix.os }}XY", etc.
        _editSequence = BuildEditSequence(baseYaml, EditCount);

        _engine = new LintEngine();
        _ctx = new IncrementalParseContext();

        // Warm up
        var result = _engine.CheckDirect(_stableSource, FilePath, BenchConfig, out var arena);
        arena?.Dispose();
        _ctx.UpdateAfterParse(_stableSource, FilePath);
    }

    /// <summary>
    /// Baseline: full parse+lint with SAME source every call (no edits).
    /// Shows the steady-state cost that incremental parsing aims to reduce.
    /// </summary>
    [Benchmark(Baseline = true, Description = "FullParseLint_SameSource_20x")]
    public int FullParseLint_SameSource()
    {
        var total = 0;
        for (var i = 0; i < EditCount; i++)
        {
            var result = _engine.CheckDirect(_stableSource, FilePath, BenchConfig, out var arena);
            total += result.Diagnostics.Length;
            arena?.Dispose();
        }
        return total;
    }

    /// <summary>
    /// Sequential edits: full parse+lint where source changes slightly each call.
    /// This is the real-world scenario that D-5b/c targets.
    /// </summary>
    [Benchmark(Description = "FullParseLint_SmallEdits_20x")]
    public int FullParseLint_SmallEdits()
    {
        var total = 0;
        for (var i = 0; i < EditCount; i++)
        {
            var source = _editSequence[i];
            var result = _engine.CheckDirect(source, FilePath, BenchConfig, out var arena);
            total += result.Diagnostics.Length;
            arena?.Dispose();
        }
        return total;
    }

    /// <summary>
    /// Measures the overhead of building the SectionRegistry after each parse.
    /// This is the cost added by D-5a that D-5b will use to skip sections.
    /// </summary>
    [Benchmark(Description = "RegistryBuild_SmallEdits_20x")]
    public int RegistryBuild_SmallEdits()
    {
        var total = 0;
        for (var i = 0; i < EditCount; i++)
        {
            var source = _editSequence[i];
            _ctx.UpdateAfterParse(source, FilePath);
            total += _ctx.Registry.JobCount;
        }
        return total;
    }

    /// <summary>
    /// Measures full pipeline: parse+lint + registry update + edit region detection.
    /// This is the total cost per-call that D-5b will reduce.
    /// </summary>
    [Benchmark(Description = "FullPipeline_SmallEdits_20x")]
    public int FullPipeline_SmallEdits()
    {
        var total = 0;
        for (var i = 0; i < EditCount; i++)
        {
            var source = _editSequence[i];

            // Detect edit region (will be used by D-5b to decide what to skip)
            var edit = _ctx.DetectEditRegion(source);
            total += edit.Start;

            // Full parse + lint (D-5b will selectively skip sections here)
            var result = _engine.CheckDirect(source, FilePath, BenchConfig, out var arena);
            total += result.Diagnostics.Length;
            arena?.Dispose();

            // Update registry for next iteration
            _ctx.UpdateAfterParse(source, FilePath);
        }
        return total;
    }

    /// <summary>
    /// Measures just the edit region detection cost (prefix/suffix matching).
    /// Should be negligible compared to parse+lint.
    /// </summary>
    [Benchmark(Description = "EditRegionDetect_SmallEdits_20x")]
    public int EditRegionDetect_SmallEdits()
    {
        var total = 0;
        // Setup: record first source
        _ctx.UpdateAfterParse(_editSequence[0], FilePath);
        for (var i = 1; i < EditCount; i++)
        {
            var edit = _ctx.DetectEditRegion(_editSequence[i]);
            total += edit.Start + edit.End;
            _ctx.UpdateAfterParse(_editSequence[i], FilePath);
        }
        return total;
    }

    /// <summary>
    /// Measures section unchanged detection for all root sections + jobs.
    /// This is what D-5b will call to decide whether to skip parsing a section.
    /// </summary>
    [Benchmark(Description = "SectionUnchangedCheck_SmallEdits_20x")]
    public int SectionUnchangedCheck_SmallEdits()
    {
        var total = 0;
        _ctx.UpdateAfterParse(_editSequence[0], FilePath);
        for (var i = 1; i < EditCount; i++)
        {
            var source = _editSequence[i];
            var registry = _ctx.Registry;

            // Check all root sections
            if (_ctx.IsSectionUnchanged(registry.GetRootSection(RootSectionKind.On), source)) total++;
            if (_ctx.IsSectionUnchanged(registry.GetRootSection(RootSectionKind.Env), source)) total++;
            if (_ctx.IsSectionUnchanged(registry.GetRootSection(RootSectionKind.Permissions), source)) total++;
            if (_ctx.IsSectionUnchanged(registry.GetRootSection(RootSectionKind.Defaults), source)) total++;
            if (_ctx.IsSectionUnchanged(registry.GetRootSection(RootSectionKind.Concurrency), source)) total++;

            // Check all jobs
            var jobEntries = registry.JobEntries;
            for (var j = 0; j < jobEntries.Length; j++)
            {
                if (_ctx.IsSectionUnchanged(jobEntries[j], source)) total++;
            }

            _ctx.UpdateAfterParse(source, FilePath);
        }
        return total;
    }

    private static byte[][] BuildEditSequence(string baseYaml, int count)
    {
        var result = new byte[count][];
        // Find the last "run: echo" in the YAML and append characters to simulate typing
        var insertionPoint = baseYaml.LastIndexOf("run: echo", StringComparison.Ordinal);
        if (insertionPoint < 0)
            insertionPoint = baseYaml.Length - 2;

        // Find end of that line
        var lineEnd = baseYaml.IndexOf('\n', insertionPoint);
        if (lineEnd < 0) lineEnd = baseYaml.Length;

        for (var i = 0; i < count; i++)
        {
            // Insert i+1 characters at the end of the target line
            var edited = baseYaml[..lineEnd] + new string((char)('a' + (i % 26)), i + 1) + baseYaml[lineEnd..];
            result[i] = Encoding.UTF8.GetBytes(edited);
        }
        return result;
    }
}
