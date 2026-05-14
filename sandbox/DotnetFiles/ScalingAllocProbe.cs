#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core
using System.Reflection;
using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

// Measure how allocation scales with job count to identify per-job overhead
Console.WriteLine("=== Scaling Analysis: ExprUndefinedVarRule ===");
Console.WriteLine($"{"Jobs",-6}{"Steps",-8}{"Total(KB)",-12}{"Parse(KB)",-12}{"Rule(KB)",-12}{"Per-Job(B)",-12}");

var defaultEngine = new LintEngine();
var rulesField = typeof(LintEngine).GetField("rules", BindingFlags.NonPublic | BindingFlags.Instance)!;
var allRules = (List<IRule>)rulesField.GetValue(defaultEngine)!;
var exprRule = allRules.Where(r => r.GetType().Name == "ExprUndefinedVarRule").ToList();

long prevRuleAlloc = 0;
int prevJobs = 0;

foreach (var jobCount in new[] { 0, 1, 2, 5, 10, 20 })
{
    var yaml = BuildWorkflowYaml(jobCount, 12);
    var bytes = Encoding.UTF8.GetBytes(yaml);
    var filePath = "bench.yml";

    var lintConfig = new LintConfig
    {
        Utf8Yaml = bytes,
        FilePath = filePath,
        Fix = new FixConfig { Enabled = false, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } }
    };

    var exprEngine = new LintEngine(exprRule);

    // Warm up (populate caches, arena)
    for (int i = 0; i < 5; i++)
    {
        var r = exprEngine.Check(bytes, filePath, lintConfig);
        r.ParseResult.Arena?.Dispose();
    }

    // Measure parse alone
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    var before = GC.GetTotalAllocatedBytes(precise: true);
    var parseResult = WorkflowParser.Parse(bytes, filePath);
    var parseAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
    parseResult.Arena?.Dispose();

    // Measure full check
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    before = GC.GetTotalAllocatedBytes(precise: true);
    var result = exprEngine.Check(bytes, filePath, lintConfig);
    var totalAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
    result.ParseResult.Arena?.Dispose();

    var ruleAlloc = totalAlloc - parseAlloc;
    var perJob = (jobCount > 0 && prevJobs > 0)
        ? (ruleAlloc - prevRuleAlloc) / (jobCount - prevJobs)
        : 0;

    Console.WriteLine($"{jobCount,-6}{jobCount * 12,-8}{totalAlloc / 1024.0,-12:F1}{parseAlloc / 1024.0,-12:F1}{ruleAlloc / 1024.0,-12:F1}{perJob,-12}");

    prevRuleAlloc = ruleAlloc;
    prevJobs = jobCount;
}

// === Now measure full lint with all rules scaling ===
Console.WriteLine("\n=== Scaling Analysis: Full Lint (all rules) ===");
Console.WriteLine($"{"Jobs",-6}{"Steps",-8}{"Total(KB)",-12}{"Parse(KB)",-12}{"Lint(KB)",-12}{"Diags",-8}{"Per-Job(B)",-12}");

prevRuleAlloc = 0;
prevJobs = 0;

foreach (var jobCount in new[] { 0, 1, 2, 5, 10, 20 })
{
    var yaml = BuildWorkflowYaml(jobCount, 12);
    var bytes = Encoding.UTF8.GetBytes(yaml);
    var filePath = "bench.yml";

    var lintConfig = new LintConfig
    {
        Utf8Yaml = bytes,
        FilePath = filePath,
        Fix = new FixConfig { Enabled = false, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } }
    };

    var engine = new LintEngine();

    // Warm up
    for (int i = 0; i < 5; i++)
    {
        var r = engine.Check(bytes, filePath, lintConfig);
        r.ParseResult.Arena?.Dispose();
    }

    // Measure parse alone
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    var before = GC.GetTotalAllocatedBytes(precise: true);
    var parseResult = WorkflowParser.Parse(bytes, filePath);
    var parseAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
    parseResult.Arena?.Dispose();

    // Measure full check
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    before = GC.GetTotalAllocatedBytes(precise: true);
    var result = engine.Check(bytes, filePath, lintConfig);
    var totalAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
    result.ParseResult.Arena?.Dispose();

    var ruleAlloc = totalAlloc - parseAlloc;
    var perJob = (jobCount > 0 && prevJobs > 0)
        ? (ruleAlloc - prevRuleAlloc) / (jobCount - prevJobs)
        : 0;

    Console.WriteLine($"{jobCount,-6}{jobCount * 12,-8}{totalAlloc / 1024.0,-12:F1}{parseAlloc / 1024.0,-12:F1}{ruleAlloc / 1024.0,-12:F1}{result.Diagnostics.Length,-8}{perJob,-12}");

    prevRuleAlloc = ruleAlloc;
    prevJobs = jobCount;
}

static string BuildWorkflowYaml(int jobCount, int stepsPerJob)
{
    var sb = new StringBuilder(8192);
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
    for (int j = 0; j < jobCount; j++)
    {
        sb.AppendLine($"  job{j}:");
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
        for (int s = 0; s < stepsPerJob; s++)
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
