#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core
using System.Runtime;
using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

// Build workflow YAML matching benchmark (Large = 20 jobs × 12 steps)
var yaml = BuildWorkflowYaml(20, 12);
var bytes = Encoding.UTF8.GetBytes(yaml);
var filePath = "bench.yml";

Console.WriteLine($"YAML size: {bytes.Length:N0} bytes, {yaml.Split('\n').Length} lines");
Console.WriteLine();

// === Phase 1: Measure VYaml via VYaml.Parser directly ===
// Warm up
for (int i = 0; i < 3; i++)
{
    var r = VYaml.Parser.YamlParser.FromBytes(bytes.AsMemory());
    while (r.Read()) { }
}

GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, true, true);
var before = GC.GetTotalAllocatedBytes(precise: true);
{
    var r = VYaml.Parser.YamlParser.FromBytes(bytes.AsMemory());
    while (r.Read()) { }
}
var vyamlAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
Console.WriteLine($"[1] VYaml raw read-all: {vyamlAlloc:N0} bytes");

// === Phase 2: Parse only (arena reused from warm-up) ===
// Warm up parser (populates ThreadStatic arena)
var warmup = WorkflowParser.Parse(bytes, filePath);
warmup.Arena?.Dispose();

GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, true, true);
before = GC.GetTotalAllocatedBytes(precise: true);
var parseResult = WorkflowParser.Parse(bytes, filePath);
var parseAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
Console.WriteLine($"[2] WorkflowParser.Parse (arena reused): {parseAlloc:N0} bytes");
parseResult.Arena?.Dispose();

// === Phase 3: Full lint (engine reused, config reused, arena reused from prior Dispose) ===
var engine = new LintEngine();
var lintConfig = new LintConfig
{
    Utf8Yaml = bytes,
    FilePath = filePath,
    Fix = new FixConfig { Enabled = false, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } }
};

// Warm up lint
for (int i = 0; i < 3; i++)
{
    var r = engine.Check(bytes, filePath, lintConfig);
    r.ParseResult.Arena?.Dispose();
}

GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, true, true);
before = GC.GetTotalAllocatedBytes(precise: true);
var lintResult = engine.Check(bytes, filePath, lintConfig);
var lintAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
Console.WriteLine($"[3] LintEngine.Check (full parse+lint): {lintAlloc:N0} bytes");
Console.WriteLine($"    Diagnostics count: {lintResult.Diagnostics.Length}");
lintResult.ParseResult.Arena?.Dispose();

// === Phase 4: Lint-only (subtract parse) ===
Console.WriteLine($"\n[4] Lint-only estimate: {lintAlloc - parseAlloc:N0} bytes (full - parse)");

// === Phase 5: Breakdown of lint diagnostic strings ===
Console.WriteLine($"\n=== Diagnostic string cost ===");
// Re-run without dispose to inspect diagnostics
var result2 = engine.Check(bytes, filePath, lintConfig);
long diagStringBytes = 0;
long diagFixBytes = 0;
var ruleIds = new Dictionary<string, int>();
for (int i = 0; i < result2.Diagnostics.Length; i++)
{
    var d = result2.Diagnostics[i];
    diagStringBytes += (d.Message?.Length ?? 0) * 2 + 40; // string obj header + chars
    if (d.RuleId is not null) diagStringBytes += d.RuleId.Length * 2 + 40;
    if (d.Help is not null) diagStringBytes += d.Help.Length * 2 + 40;
    if (d.Fix is not null)
    {
        diagFixBytes += 48; // DiagnosticFix overhead
        if (d.Fix.Value.Edits is not null) diagFixBytes += d.Fix.Value.Edits.Length * 48;
    }

    var rid = d.RuleId ?? "(parser)";
    ruleIds[rid] = ruleIds.GetValueOrDefault(rid) + 1;
}
Console.WriteLine($"  Total diagnostics: {result2.Diagnostics.Length}");
Console.WriteLine($"  Diagnostic string cost: ~{diagStringBytes:N0} bytes");
Console.WriteLine($"  Diagnostic fix cost: ~{diagFixBytes:N0} bytes");
Console.WriteLine($"\n  By rule:");
foreach (var kv in ruleIds.OrderByDescending(x => x.Value))
{
    Console.WriteLine($"    {kv.Key}: {kv.Value}");
}
result2.ParseResult.Arena?.Dispose();

// === Phase 6: Expression cache analysis ===
Console.WriteLine($"\n=== Expression cache analysis ===");
// The lintConfig's expression cache is populated from prior runs
// Count unique expressions in the fixture
int exprCount = 0;
var uniqueExprs = new HashSet<string>();
var span = bytes.AsSpan();
int idx = 0;
while (idx < span.Length - 3)
{
    var dollarbrace = span[idx..].IndexOf("${{"u8);
    if (dollarbrace < 0) break;
    idx += dollarbrace + 3;
    var end = span[idx..].IndexOf("}}"u8);
    if (end < 0) break;
    var exprSlice = span.Slice(idx, end);
    var expr = Encoding.UTF8.GetString(exprSlice.TrimStart((byte)' ').TrimEnd((byte)' '));
    uniqueExprs.Add(expr);
    exprCount++;
    idx += end + 2;
}
Console.WriteLine($"  Total ${{{{ }}}} occurrences: {exprCount}");
Console.WriteLine($"  Unique expressions: {uniqueExprs.Count}");
foreach (var e in uniqueExprs) Console.WriteLine($"    \"{e}\"");

// === Phase 7: VYaml double-read cost ===
Console.WriteLine($"\n=== VYaml double-read overhead ===");
// ParseClassified creates TWO YamlParsers
GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, true, true);
before = GC.GetTotalAllocatedBytes(precise: true);
{
    var r1 = VYaml.Parser.YamlParser.FromBytes(bytes.AsMemory());
    // Read just enough for hints (a few events)
    for (int i = 0; i < 20 && !r1.End; i++) r1.Read();
}
var hintAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
Console.WriteLine($"  Hint reader (partial read): {hintAlloc:N0} bytes");
Console.WriteLine($"  Full reader (parse): {vyamlAlloc:N0} bytes");
Console.WriteLine($"  Total VYaml cost per parse: ~{hintAlloc + vyamlAlloc:N0} bytes");

// === Phase 8: Per-rule lint cost estimation ===
Console.WriteLine($"\n=== Per-rule lint cost (isolated) ===");
// Run with only one rule at a time to estimate each rule's contribution
var allRuleNames = new[] {
    "ActionRefRule", "ExprRule", "ExprUndefinedVarRule",
    "JobStructureRule", "ShellcheckRule", "ScheduleEventRule",
    "StepStructureRule", "WorkflowStructureRule", "PermissionsRule"
};

foreach (var ruleName in allRuleNames)
{
    try
    {
        var defaultEngine = new LintEngine();
        // Use reflection to access rules
        var rulesField = typeof(LintEngine).GetField("rules", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var allRules = (List<IRule>)rulesField.GetValue(defaultEngine)!;
        var matchingRules = allRules.Where(r => r.GetType().Name == ruleName).ToList();
        if (matchingRules.Count == 0) continue;

        var singleEngine = new LintEngine(matchingRules);
        // Warm up
        for (int i = 0; i < 3; i++)
        {
            var sr = singleEngine.Check(bytes, filePath, lintConfig);
            sr.ParseResult.Arena?.Dispose();
        }

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        before = GC.GetTotalAllocatedBytes(precise: true);
        var sr2 = singleEngine.Check(bytes, filePath, lintConfig);
        var ruleAlloc = GC.GetTotalAllocatedBytes(precise: true) - before;
        Console.WriteLine($"  {ruleName}: {ruleAlloc:N0} bytes ({sr2.Diagnostics.Length} diags)");
        sr2.ParseResult.Arena?.Dispose();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {ruleName}: ERROR - {ex.Message}");
    }
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
