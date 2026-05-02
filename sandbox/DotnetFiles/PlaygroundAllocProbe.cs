#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Playground.Core
#:project ../../src/Seiton.Core
#:package VYaml

using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Playground;

// ─── Build Large benchmark YAML (same as PlaygroundLintBenchmark) ───
static string BuildWorkflowYaml(int jobCount, int stepsPerJob)
{
    var sb = new StringBuilder(capacity: 8_192);
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

var yamlSource = BuildWorkflowYaml(jobCount: 6, stepsPerJob: 8);
var filePath = ".github/workflows/bench.yml";
Console.WriteLine($"YAML size: {Encoding.UTF8.GetByteCount(yamlSource):N0} bytes");
Console.WriteLine();

// Warmup
PlaygroundLintRunner.RunToJson(yamlSource, filePath);
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

// ─── Measure single RunToJson call ───
var before = GC.GetTotalAllocatedBytes(precise: true);
var json = PlaygroundLintRunner.RunToJson(yamlSource, filePath);
var after = GC.GetTotalAllocatedBytes(precise: true);

Console.WriteLine("=== Single RunToJson (6 jobs × 8 steps) ===");
Console.WriteLine($"  Allocated: {after - before:N0} B ({(after - before) / 1024.0:N1} KB)");
Console.WriteLine($"  JSON length: {json.Length}");
Console.WriteLine();

// ─── Measure 10 calls to see per-call cost ───
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

before = GC.GetTotalAllocatedBytes(precise: true);
for (var i = 0; i < 10; i++)
{
    PlaygroundLintRunner.RunToJson(yamlSource, filePath);
}
after = GC.GetTotalAllocatedBytes(precise: true);

Console.WriteLine("=== 10x RunToJson (6 jobs × 8 steps) ===");
Console.WriteLine($"  Total Allocated: {after - before:N0} B ({(after - before) / 1024.0:N1} KB)");
Console.WriteLine($"  Per-call: {(after - before) / 10:N0} B ({(after - before) / 10240.0:N1} KB)");
Console.WriteLine();

// ─── Breakdown: Parse vs Lint ───
var yamlBytes = Encoding.UTF8.GetBytes(yamlSource);

// Parse only
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

before = GC.GetTotalAllocatedBytes(precise: true);
var pr = WorkflowParser.ParseClassified(yamlBytes, filePath);
after = GC.GetTotalAllocatedBytes(precise: true);
pr.ParseResult.Arena?.Dispose();

Console.WriteLine("=== Parse only (ParseClassified) ===");
Console.WriteLine($"  Allocated: {after - before:N0} B ({(after - before) / 1024.0:N1} KB)");
Console.WriteLine();

// Lint only (engine already warmed up)
var engine = new LintEngine();
var lintConfig = new LintConfig
{
    Fix = new FixConfig { Enabled = true },
    Network = new NetworkConfig(),
    Output = new OutputConfig(),
    SkipSuppressionSummary = true,
};

// Warmup lint engine
engine.Check(yamlBytes, filePath, lintConfig);
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

before = GC.GetTotalAllocatedBytes(precise: true);
var lintResult = engine.Check(yamlBytes, filePath, lintConfig);
after = GC.GetTotalAllocatedBytes(precise: true);
lintResult.ParseResult.Arena?.Dispose();

Console.WriteLine("=== Lint (engine.Check, FixEnabled=true) ===");
Console.WriteLine($"  Allocated: {after - before:N0} B ({(after - before) / 1024.0:N1} KB)");
Console.WriteLine($"  Diagnostics: {lintResult.Diagnostics.Length}");
Console.WriteLine();

// JSON serialization only (from pre-computed result)
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

before = GC.GetTotalAllocatedBytes(precise: true);
var jsonStr = PlaygroundLintRunner.RunToJson(yamlSource, filePath);
after = GC.GetTotalAllocatedBytes(precise: true);
Console.WriteLine("=== Full RunToJson after warmup ===");
Console.WriteLine($"  Allocated: {after - before:N0} B ({(after - before) / 1024.0:N1} KB)");

// ─── Expression parsing cost ───
Console.WriteLine();
Console.WriteLine("=== Expression Parsing Cost ===");
var uniqueExprs = new[] {
    "github.ref_name"u8.ToArray(),
    "github.ref"u8.ToArray(),
    "matrix.os"u8.ToArray(),
    "github.sha"u8.ToArray(),
    "startsWith(github.ref, 'refs/heads/') && success()"u8.ToArray(),
    "!cancelled() && github.event_name == 'push'"u8.ToArray(),
};

GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
before = GC.GetTotalAllocatedBytes(precise: true);
for (var i = 0; i < 6; i++)
{
    ExpressionParser.Parse(uniqueExprs[i]);
}
after = GC.GetTotalAllocatedBytes(precise: true);
Console.WriteLine($"  6 unique expressions: {after - before:N0} B ({(after - before) / 1024.0:N1} KB)");

// ─── Diagnostic messages from benchmark ───
Console.WriteLine();
Console.WriteLine("=== Diagnostics Breakdown ===");
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
before = GC.GetTotalAllocatedBytes(precise: true);
var diagResult = PlaygroundLintRunner.RunToJson(yamlSource, filePath);
after = GC.GetTotalAllocatedBytes(precise: true);

// Parse JSON to inspect diagnostics
var diagArr = System.Text.Json.JsonDocument.Parse(diagResult);
var diagCount = diagArr.RootElement.GetArrayLength();
Console.WriteLine($"  Diagnostic count: {diagCount}");
var ruleIds = new Dictionary<string, int>();
foreach (var item in diagArr.RootElement.EnumerateArray())
{
    var rid = item.GetProperty("ruleId").GetString() ?? "(none)";
    ruleIds.TryGetValue(rid, out var cnt);
    ruleIds[rid] = cnt + 1;
}
Console.WriteLine("  Per-rule breakdown:");
foreach (var kv in ruleIds.OrderByDescending(x => x.Value))
{
    Console.WriteLine($"    {kv.Key}: {kv.Value}");
}

// Measure just message strings
var messages = new List<string>();
foreach (var item in diagArr.RootElement.EnumerateArray())
{
    messages.Add(item.GetProperty("message").GetString()!);
}
var totalMessageBytes = messages.Sum(m => m.Length * 2 + 26); // approx object overhead on x64
Console.WriteLine($"  Total message string memory: ~{totalMessageBytes:N0} B ({totalMessageBytes / 1024.0:N1} KB)");

// ─── VYaml cost (2 adapters per call) ───
Console.WriteLine();
Console.WriteLine("=== VYaml parser creation cost ===");
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
before = GC.GetTotalAllocatedBytes(precise: true);
{
    var parser = VYaml.Parser.YamlParser.FromBytes(yamlBytes.AsMemory());
    while (parser.Read()) { }
}
after = GC.GetTotalAllocatedBytes(precise: true);
Console.WriteLine($"  1 YamlParser (full read): {after - before:N0} B ({(after - before) / 1024.0:N1} KB)");

// ─── String in Encoding.UTF8.GetString for JSON result ───
Console.WriteLine();
Console.WriteLine("=== Final JSON string allocation ===");
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
before = GC.GetTotalAllocatedBytes(precise: true);
var tempStr = Encoding.UTF8.GetString(new byte[23915]); // approximate JSON size
after = GC.GetTotalAllocatedBytes(precise: true);
Console.WriteLine($"  ~24KB JSON string: {after - before:N0} B ({(after - before) / 1024.0:N1} KB)");
