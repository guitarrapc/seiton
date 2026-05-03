#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Optimize=true
#:project ../../src/Seiton.Playground.Core/Seiton.Playground.Core.csproj
#:project ../../src/Seiton.Core/Seiton.Core.csproj
using System.Buffers;
using System.Text;
using System.Text.Json;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Playground;

var baseYaml = BuildYaml(null);
var variants = new string[10];
for (var i = 0; i < 10; i++)
    variants[i] = BuildYaml($"-edit{i}");

var config = new LintConfig
{
    Fix = new FixConfig { Enabled = true },
    Network = new NetworkConfig(),
    Output = new OutputConfig(),
    SkipSuppressionSummary = true,
};

// Pre-encode to UTF-8
var utf8Variants = new byte[10][];
for (var i = 0; i < 10; i++)
    utf8Variants[i] = Encoding.UTF8.GetBytes(variants[i]);

// === Measure Engine.Check directly (baseline parse+lint cost) ===
var engine = new LintEngine();
for (var warmup = 0; warmup < 20; warmup++)
    for (var i = 0; i < 10; i++)
    { var r = engine.Check(utf8Variants[i], ".github/workflows/bench.yml", config); r.ParseResult.Arena?.Dispose(); }

GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
var before = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 10; i++)
{ var r = engine.Check(utf8Variants[i], ".github/workflows/bench.yml", config); r.ParseResult.Arena?.Dispose(); }
var after = GC.GetAllocatedBytesForCurrentThread();
Console.WriteLine($"[A] Engine.Check+Dispose:     {(after - before),8} bytes ({(after - before) / 10,6} /call)");

// === Measure ParseClassified alone ===
for (var warmup = 0; warmup < 20; warmup++)
    for (var i = 0; i < 10; i++)
    { var r = WorkflowParser.ParseClassified(utf8Variants[i], ".github/workflows/bench.yml"); r.ParseResult.Arena?.Dispose(); }
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
before = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 10; i++)
{ var r = WorkflowParser.ParseClassified(utf8Variants[i], ".github/workflows/bench.yml"); r.ParseResult.Arena?.Dispose(); }
after = GC.GetAllocatedBytesForCurrentThread();
Console.WriteLine($"[B] ParseClassified+Dispose:  {(after - before),8} bytes ({(after - before) / 10,6} /call)");

// === Measure ParseIncrementally alone ===
var ctx = new IncrementalParseContext();
for (var warmup = 0; warmup < 20; warmup++)
{
    ctx.ParseIncrementally(Encoding.UTF8.GetBytes(baseYaml), ".github/workflows/bench.yml");
    for (var i = 0; i < 10; i++)
        ctx.ParseIncrementally(utf8Variants[i], ".github/workflows/bench.yml");
}
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
before = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 10; i++)
    ctx.ParseIncrementally(utf8Variants[i], ".github/workflows/bench.yml");
after = GC.GetAllocatedBytesForCurrentThread();
Console.WriteLine($"[C] ParseIncrementally:       {(after - before),8} bytes ({(after - before) / 10,6} /call)");

// === Full PlaygroundLintRunner ===
for (var warmup = 0; warmup < 20; warmup++)
{
    PlaygroundLintRunner.RunToJsonUtf8(baseYaml, ".github/workflows/bench.yml");
    for (var i = 0; i < 10; i++)
        PlaygroundLintRunner.RunToJsonUtf8(variants[i], ".github/workflows/bench.yml");
}
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
before = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 10; i++)
    PlaygroundLintRunner.RunToJsonUtf8(variants[i], ".github/workflows/bench.yml");
after = GC.GetAllocatedBytesForCurrentThread();
var output = PlaygroundLintRunner.RunToJsonUtf8(variants[0], ".github/workflows/bench.yml");
Console.WriteLine($"[E] PlaygroundLintRunner:      {(after - before),8} bytes ({(after - before) / 10,6} /call) [json={output.Length}B]");
Console.WriteLine($"JSON: {Encoding.UTF8.GetString(output)[..Math.Min(500, output.Length)]}");
Console.WriteLine();
Console.WriteLine($"Parse cost: A-B gives lint-only baseline = {101840 - ((int)(GC.GetAllocatedBytesForCurrentThread() - GC.GetAllocatedBytesForCurrentThread()))} (check manually)");;

static string BuildYaml(string? firstJobStepSuffix)
{
    var sb = new StringBuilder(2048);
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
    sb.AppendLine("  job0:");
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
    sb.Append("      - name: Run").AppendLine(firstJobStepSuffix ?? "");
    sb.AppendLine("        if: ${{ startsWith(github.ref, 'refs/heads/') && success() }}");
    sb.AppendLine("        run: echo ${{ matrix.os }}");
    sb.AppendLine("        env:");
    sb.AppendLine("          STEP_ENV: ${{ github.sha }}");
    sb.AppendLine("      - name: Action");
    sb.AppendLine("        uses: actions/checkout@v4");
    sb.AppendLine("        with:");
    sb.AppendLine("          fetch-depth: '0'");
    sb.AppendLine("        if: ${{ !cancelled() && github.event_name == 'push' }}");
    sb.AppendLine("      - name: Run2");
    sb.AppendLine("        if: ${{ startsWith(github.ref, 'refs/heads/') && success() }}");
    sb.AppendLine("        run: echo ${{ matrix.os }}");
    sb.AppendLine("        env:");
    sb.AppendLine("          STEP_ENV: ${{ github.sha }}");
    return sb.ToString().Replace("\r\n", "\n");
}

static string BuildFullChangeYaml(int variant)
{
    var sb = new StringBuilder(2048);
    sb.Append("name: bench-variant").AppendLine(variant.ToString());
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
    sb.AppendLine("  job0:");
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
    sb.AppendLine("      - name: Run");
    sb.AppendLine("        if: ${{ startsWith(github.ref, 'refs/heads/') && success() }}");
    sb.AppendLine("        run: echo ${{ matrix.os }}");
    sb.AppendLine("        env:");
    sb.AppendLine("          STEP_ENV: ${{ github.sha }}");
    sb.AppendLine("      - name: Action");
    sb.AppendLine("        uses: actions/checkout@v4");
    sb.AppendLine("        with:");
    sb.AppendLine("          fetch-depth: '0'");
    sb.AppendLine("        if: ${{ !cancelled() && github.event_name == 'push' }}");
    sb.AppendLine("      - name: Run2");
    sb.AppendLine("        if: ${{ startsWith(github.ref, 'refs/heads/') && success() }}");
    sb.AppendLine("        run: echo ${{ matrix.os }}");
    sb.AppendLine("        env:");
    sb.AppendLine("          STEP_ENV: ${{ github.sha }}");
    return sb.ToString().Replace("\r\n", "\n");
}
