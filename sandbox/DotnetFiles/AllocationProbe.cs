#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core
using System.Runtime;
using Seiton.Core.Parsing;

// Build a Large workflow YAML (20 jobs × 12 steps) matching benchmark
var yaml = BuildWorkflowYaml(20, 12);
var bytes = System.Text.Encoding.UTF8.GetBytes(yaml);
Console.WriteLine($"YAML size: {bytes.Length} bytes");

// Warm up: first parse to populate ThreadStatic cache
var warmup = WorkflowParser.Parse(bytes, "bench.yml");
warmup.Arena?.Dispose();

// Measure: second parse uses cached arena
GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
var before = GC.GetTotalAllocatedBytes(precise: true);

var result = WorkflowParser.Parse(bytes, "bench.yml");

var after = GC.GetTotalAllocatedBytes(precise: true);
var parseAlloc = after - before;
Console.WriteLine($"\n=== Parse-only allocations (with arena reuse): {parseAlloc:N0} bytes ===");

// Now measure without arena reuse (fresh arena)
GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
before = GC.GetTotalAllocatedBytes(precise: true);

var result2 = WorkflowParser.Parse(bytes, "bench.yml");

after = GC.GetTotalAllocatedBytes(precise: true);
var freshAlloc = after - before;
Console.WriteLine($"=== Parse-only allocations (fresh arena): {freshAlloc:N0} bytes ===");
Console.WriteLine($"=== Arena reuse saves: {freshAlloc - parseAlloc:N0} bytes ===");

// Count AST objects
var wf = result.Workflow!;
var arena = result.Arena!;
var jobCount = wf.Jobs.Count;
var stepCount = 0;
var execRunCount = 0;
var execActionCount = 0;
var eventCount = wf.On?.Count ?? 0;

foreach (var kv in wf.Jobs)
{
    var job = kv.Value;
    if (job.Steps is not null)
    {
        stepCount += job.Steps.Count;
        for (var i = 0; i < job.Steps.Count; i++)
        {
            var step = job.Steps[i];
            if (step.Exec is Seiton.Core.Parsing.Ast.ExecRun) execRunCount++;
            else if (step.Exec is Seiton.Core.Parsing.Ast.ExecAction) execActionCount++;
        }
    }
}

Console.WriteLine($"\n=== AST inventory ===");
Console.WriteLine($"Jobs: {jobCount}");
Console.WriteLine($"Steps: {stepCount}");
Console.WriteLine($"ExecRun: {execRunCount}, ExecAction: {execActionCount}");
Console.WriteLine($"Events: {eventCount}");
Console.WriteLine($"Diagnostics: {result.Diagnostics.Length}");

// Estimate class overhead per category
// Object header = 16 bytes (sync block + method table pointer on x64)
const int OBJ_HEADER = 16;
Console.WriteLine($"\n=== Estimated class object overhead ===");
Console.WriteLine($"Job objects: {jobCount} × ~{OBJ_HEADER + 8 * 18}B = ~{jobCount * (OBJ_HEADER + 8 * 18):N0}B");
Console.WriteLine($"Step objects: {stepCount} × ~{OBJ_HEADER + 8 * 8}B = ~{stepCount * (OBJ_HEADER + 8 * 8):N0}B");
Console.WriteLine($"StepExec objects: {stepCount} × ~{OBJ_HEADER + 8 * 5}B = ~{stepCount * (OBJ_HEADER + 8 * 5):N0}B");

result.Arena?.Dispose();
result2.Arena?.Dispose();

static string BuildWorkflowYaml(int jobCount, int stepsPerJob)
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
