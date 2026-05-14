#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core
using System.Reflection;
using System.Runtime;
using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

// Build workflow YAML matching benchmark (Large = 20 jobs × 12 steps)
var yaml = BuildWorkflowYaml(20, 12);
var bytes = Encoding.UTF8.GetBytes(yaml);
var filePath = "bench.yml";

// === Measure Arena reuse vs fresh ===
Console.WriteLine("=== Arena Reuse Analysis ===");

// No warmup (cold arena):
GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, true, true);
var before = GC.GetTotalAllocatedBytes(precise: true);
var coldResult = WorkflowParser.Parse(bytes, filePath);
var coldAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
Console.WriteLine($"  Parse (COLD arena, no reuse): {coldAlloc:N0} bytes");
// Don't dispose — simulates benchmark behavior

// Parse again (still no reuse since we didn't dispose):
GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, true, true);
before = GC.GetTotalAllocatedBytes(precise: true);
var coldResult2 = WorkflowParser.Parse(bytes, filePath);
var coldAlloc2 = GC.GetTotalAllocatedBytes(precise: true) - before;
Console.WriteLine($"  Parse (still COLD, prev not disposed): {coldAlloc2:N0} bytes");

// Now dispose and measure reuse:
coldResult.Arena?.Dispose();
coldResult2.Arena?.Dispose();
GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, true, true);
before = GC.GetTotalAllocatedBytes(precise: true);
var warmResult = WorkflowParser.Parse(bytes, filePath);
var warmAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
Console.WriteLine($"  Parse (WARM arena, reused): {warmAlloc:N0} bytes");
Console.WriteLine($"  => Arena overhead (cold - warm): {coldAlloc - warmAlloc:N0} bytes");
warmResult.Arena?.Dispose();

// === Full lint without arena disposal (simulating benchmark) ===
Console.WriteLine("\n=== Lint Without Arena Disposal (benchmark scenario) ===");
var engine = new LintEngine();
var lintConfig = new LintConfig
{
    Utf8Yaml = bytes,
    FilePath = filePath,
    Fix = new FixConfig { Enabled = false, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } }
};

// "Warm" the expression cache only (not the arena)
var warmLint = engine.Check(bytes, filePath, lintConfig);
// Don't dispose arena! This is what the benchmark does.

GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, true, true);
before = GC.GetTotalAllocatedBytes(precise: true);
var benchLint = engine.Check(bytes, filePath, lintConfig);
var benchAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
Console.WriteLine($"  Full lint (no arena reuse, warm cache): {benchAlloc:N0} bytes");
Console.WriteLine($"  Diagnostics: {benchLint.Diagnostics.Length}");

// Compare with arena reuse:
benchLint.ParseResult.Arena?.Dispose();
warmLint.ParseResult.Arena?.Dispose();
GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, true, true);
before = GC.GetTotalAllocatedBytes(precise: true);
var reuseLint = engine.Check(bytes, filePath, lintConfig);
var reuseAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
Console.WriteLine($"  Full lint (arena reused, warm cache): {reuseAlloc:N0} bytes");
Console.WriteLine($"  => Arena reuse saves: {benchAlloc - reuseAlloc:N0} bytes");
reuseLint.ParseResult.Arena?.Dispose();

// === Isolate ExprUndefinedVarRule allocations ===
Console.WriteLine("\n=== ExprUndefinedVarRule Deep Dive ===");
// Measure with warm arena + warm expression cache, 0 diagnostics
var defaultEngine = new LintEngine();
var rulesField = typeof(LintEngine).GetField("rules", BindingFlags.NonPublic | BindingFlags.Instance)!;
var allRules = (List<IRule>)rulesField.GetValue(defaultEngine)!;
var exprRule = allRules.Where(r => r.GetType().Name == "ExprUndefinedVarRule").ToList();
var exprEngine = new LintEngine(exprRule);

// Warm up (3 iterations, disposing each)
for (int i = 0; i < 3; i++)
{
    var r = exprEngine.Check(bytes, filePath, lintConfig);
    r.ParseResult.Arena?.Dispose();
}

// Measure
GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, true, true);
before = GC.GetTotalAllocatedBytes(precise: true);
var exprResult = exprEngine.Check(bytes, filePath, lintConfig);
var exprAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
Console.WriteLine($"  ExprUndefinedVarRule (arena reused, warm cache): {exprAlloc:N0} bytes");
Console.WriteLine($"  Diagnostics: {exprResult.Diagnostics.Length}");
exprResult.ParseResult.Arena?.Dispose();

// Measure JUST the parse (same warm conditions)
GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, true, true);
before = GC.GetTotalAllocatedBytes(precise: true);
var pureParseResult = WorkflowParser.Parse(bytes, filePath);
var pureParseAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
Console.WriteLine($"  Pure parse (arena reused): {pureParseAlloc:N0} bytes");
pureParseResult.Arena?.Dispose();
Console.WriteLine($"  => ExprUndefinedVarRule-only overhead: {exprAlloc - pureParseAlloc:N0} bytes");

// === Narrower breakdown: CheckEnv Decode allocations ===
Console.WriteLine("\n=== String Allocation Hotspots (estimated) ===");
// Count the Decode() + interpolation calls in ExprUndefinedVarRule
// From the fixture: 120 steps with env (even steps) + 120 steps with with-inputs (odd steps)
int envVarDecodeCount = 120; // 120 even steps × 1 env var each
int withDecodeCount = 120; // 120 odd steps × 1 with-input each
int reusableCallCount = 0; // no workflow_call inputs

// Estimate string costs:
// Decode("STEP_ENV") = 8 chars → 40 + 16 = 56 bytes (string obj = 40 base + chars)
// $"{sinkName}.{keyName}" e.g. "step.env.STEP_ENV" = 18 chars → 40 + 36 = 76 bytes
// Decode("fetch-depth") = 11 chars → 40 + 22 = 62 bytes
// $"step.with.{inputName}" e.g. "step.with.fetch-depth" = 21 chars → 40 + 42 = 82 bytes
long decodeEstimate = envVarDecodeCount * (56 + 76) + withDecodeCount * (62 + 82);
Console.WriteLine($"  Decode + interpolation strings: ~{decodeEstimate:N0} bytes");
Console.WriteLine($"    Env var names (120×): ~{envVarDecodeCount * 56:N0} bytes");
Console.WriteLine($"    Env sink names (120×): ~{envVarDecodeCount * 76:N0} bytes");
Console.WriteLine($"    With key names (120×): ~{withDecodeCount * 62:N0} bytes");
Console.WriteLine($"    With sink names (120×): ~{withDecodeCount * 82:N0} bytes");
Console.WriteLine($"  Total Decode+interp estimate: ~{decodeEstimate:N0} bytes");
Console.WriteLine($"  That's {100.0 * decodeEstimate / (exprAlloc - pureParseAlloc):F1}% of rule overhead");

// === Measure per-expression cost ===
Console.WriteLine("\n=== Per-expression validation cost ===");
Console.WriteLine($"  Total expression occurrences: 482");
Console.WriteLine($"  Unique expressions (cache): 6");
Console.WriteLine($"  Cache hits per lint run: {482 - 6} (only first 6 allocate)");
// Each cache miss: expression.ToArray() = expr bytes + array header
// Expressions: "github.ref_name"(15), "github.ref"(10), "startsWith(github.ref, 'refs/heads/') && success()"(49),
//              "matrix.os"(9), "github.sha"(10), "!cancelled() && github.event_name == 'push'"(42)
int[] exprLengths = { 15, 10, 49, 9, 10, 42 };
long cacheMissBytes = 0;
foreach (var len in exprLengths)
{
    // expression.ToArray() + ExpressionNode[] + int[] + Diagnostic[]
    // Rough estimate: expression bytes(16+len) + nodes(16+n*32) + args(16+a*4) + diags(16+0)
    cacheMissBytes += 16 + len + 16 + 8 * 32 + 16 + 4 * 4 + 16; // rough per-expression
}
Console.WriteLine($"  Expression cache miss cost (first run): ~{cacheMissBytes:N0} bytes");
Console.WriteLine($"  After cache warm: 0 bytes (all hits)");

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
