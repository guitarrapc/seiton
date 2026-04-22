#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core

using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

// ─── Build Large benchmark YAML (same as WorkflowYamlBuilder) ───
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

var yaml = BuildWorkflowYaml(jobCount: 20, stepsPerJob: 12);
var yamlBytes = Encoding.UTF8.GetBytes(yaml);
var filePath = "bench-lint-large.yml";

// ─── Warmup ───
var engine = new LintEngine();
var lintConfig = new LintConfig
{
    Utf8Yaml = yamlBytes,
    FilePath = filePath,
    Fix = new FixConfig { Enabled = false, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } }
};

// Warmup run
engine.Check(yamlBytes, filePath, lintConfig);
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

// ─── Measure Parse only ───
var before = GC.GetTotalAllocatedBytes(precise: true);
var parseResult = WorkflowParser.Parse(yamlBytes, filePath);
var afterParse = GC.GetTotalAllocatedBytes(precise: true);
parseResult.Arena?.Dispose();

Console.WriteLine("=== Parse Only (Large: 20 jobs × 12 steps) ===");
Console.WriteLine($"  Allocated: {afterParse - before:N0} B");
Console.WriteLine($"  Jobs: {parseResult.Workflow?.Jobs.Count ?? 0}");
Console.WriteLine($"  Diagnostics: {parseResult.Diagnostics.Length}");

// ─── Measure Lint (FixEnabled=false) ───
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

before = GC.GetTotalAllocatedBytes(precise: true);
var lintResult = engine.Check(yamlBytes, filePath, lintConfig);
var afterLint = GC.GetTotalAllocatedBytes(precise: true);
var lintTotal = afterLint - before;

Console.WriteLine();
Console.WriteLine("=== Lint FixEnabled=false (Large) ===");
Console.WriteLine($"  Total Allocated: {lintTotal:N0} B ({lintTotal / 1024.0:N1} KB)");
Console.WriteLine($"  Diagnostics: {lintResult.Diagnostics.Length}");

// ─── Measure Lint (FixEnabled=true) ───
var lintConfigFix = new LintConfig
{
    Utf8Yaml = yamlBytes,
    FilePath = filePath,
    Fix = new FixConfig { Enabled = true, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } }
};

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

before = GC.GetTotalAllocatedBytes(precise: true);
var lintResultFix = engine.Check(yamlBytes, filePath, lintConfigFix);
var afterLintFix = GC.GetTotalAllocatedBytes(precise: true);
var lintFixTotal = afterLintFix - before;

Console.WriteLine();
Console.WriteLine("=== Lint FixEnabled=true (Large) ===");
Console.WriteLine($"  Total Allocated: {lintFixTotal:N0} B ({lintFixTotal / 1024.0:N1} KB)");
Console.WriteLine($"  Diagnostics: {lintResultFix.Diagnostics.Length}");
Console.WriteLine($"  Fix diff: {lintFixTotal - lintTotal:N0} B");

// ─── Breakdown: Parse vs Rules ───
Console.WriteLine();
Console.WriteLine("=== Breakdown: Parse inside Lint (FixEnabled=false) ===");

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

// Measure just the parse portion
before = GC.GetTotalAllocatedBytes(precise: true);
var pr2 = WorkflowParser.ParseClassified(yamlBytes, filePath);
var afterParse2 = GC.GetTotalAllocatedBytes(precise: true);
pr2.ParseResult.Arena?.Dispose();
Console.WriteLine($"  ParseClassified:  {afterParse2 - before:N0} B");

// Measure expression cache cost
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

before = GC.GetTotalAllocatedBytes(precise: true);
var pr3 = WorkflowParser.Parse(yamlBytes, filePath);
// Simulate expression cache building like lint does
var tmpConfig = new LintConfig { Utf8Yaml = yamlBytes, Arena = pr3.Arena, FilePath = filePath };
var workflow = pr3.Workflow!;
var expressionCount = 0;
if (workflow is not null)
{
    foreach (var jobPair in workflow.Jobs)
    {
        var job = jobPair.Value;
        var steps = job.Steps;
        if (steps is null) continue;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            // Parse expressions from if/run/env nodes
            if (step.If.HasValue)
            {
                var val = pr3.Arena!.GetStringValue(step.If);
                if (val.Length > 0) { tmpConfig.ParseExpression(val); expressionCount++; }
            }
            if (step.Exec is Seiton.Core.Parsing.Ast.ExecRun run && run.Run.HasValue)
            {
                var val = pr3.Arena!.GetStringValue(run.Run);
                if (val.Length > 0) { tmpConfig.ParseExpression(val); expressionCount++; }
            }
        }
    }
}
var afterExprCache = GC.GetTotalAllocatedBytes(precise: true);
pr3.Arena?.Dispose();
Console.WriteLine($"  Expression Cache ({expressionCount} parses): {afterExprCache - before:N0} B");

// ─── Breakdown: DynamicContextTypeBuilder ───
// (DynamicContextTypeBuilder is internal — skip direct measurement, estimate from lint diff)
Console.WriteLine();
Console.WriteLine("=== DynamicContextTypeBuilder (estimated from lint - parse diff) ===");
Console.WriteLine("  (Cannot measure directly — internal class)");
Console.WriteLine("  Estimate: lint_total - parse_total - expression_cache - diagnostic_arrays");

// ─── Diagnostic summary ───
Console.WriteLine();
Console.WriteLine("=== Diagnostic summary ===");
Console.WriteLine($"  Lint diagnostics (nofix): {lintResult.Diagnostics.Length}");
for (var i = 0; i < Math.Min(10, lintResult.Diagnostics.Length); i++)
{
    var d = lintResult.Diagnostics[i];
    Console.WriteLine($"    [{d.RuleId}] {d.Severity}: {d.Message[..Math.Min(80, d.Message.Length)]}");
}
if (lintResult.Diagnostics.Length > 10)
    Console.WriteLine($"    ... and {lintResult.Diagnostics.Length - 10} more");

Console.WriteLine();
Console.WriteLine("=== Diagnostic counts by rule ===");
var byRule = new Dictionary<string, int>(StringComparer.Ordinal);
for (var i = 0; i < lintResult.Diagnostics.Length; i++)
{
    var rid = lintResult.Diagnostics[i].RuleId ?? "(parser)";
    byRule.TryGetValue(rid, out var cnt);
    byRule[rid] = cnt + 1;
}
foreach (var kv in byRule.OrderByDescending(x => x.Value))
    Console.WriteLine($"    {kv.Key}: {kv.Value}");
