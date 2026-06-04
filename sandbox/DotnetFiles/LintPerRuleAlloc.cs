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

// Warmup
var engine = new LintEngine();
var lintConfig = new LintConfig
{
    Utf8Yaml = yamlBytes,
    FilePath = filePath,
    Fix = new FixConfig { Enabled = false, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } }
};
engine.Check(yamlBytes, filePath, lintConfig);

// ─── Per-rule isolation: run lint with only one rule at a time ───
Console.WriteLine("=== Per-rule memory isolation (Large: 20 jobs × 12 steps) ===");
Console.WriteLine();

// Get all rule IDs from a default engine
var defaultEngine = new LintEngine();
var tempConfig = new LintConfig { Utf8Yaml = yamlBytes, FilePath = filePath, Fix = new FixConfig() };
var fullResult = defaultEngine.Check(yamlBytes, filePath, tempConfig);

// All default rule IDs that can be configured
string[] ruleIds = [
    "job-structure", "reusable-workflow", "permissions", "popular-action-inputs",
    "unpinned-uses", "unpinned-image", "dangerous-triggers", "job-permissions-required",
    "needs-graph", "shell-name", "runner-label", "id-naming", "glob-pattern",
    "dispatch-inputs", "schedule-event", "deny-write-all", "credentials",
    "template-injection", "expr-undefined-var", "run-env-context-direct-use",
    "runner-no-latest", "run-secrets-context-direct-use", "run-inputs-context-direct-use",
    "secrets-whole-context-access", "checkout-persist-credentials", "deny-read-all",
    "deny-inherit-secrets", "job-timeout-minutes-required", "github-app-token-inputs",
    "cache-poisoning-trigger", "self-hosted-runner-trigger", "unredacted-secrets", "secrets-outside-env",
    "workflow-secrets", "job-secrets", "action-shell-is-required", "matrix", "env-var",
    "deprecated-commands", "if-cond", "fake-ternary", "archived-uses",
    "insecure-commands", "overprovisioned-secrets", "forbidden-uses",
    "ref-version-mismatch", "use-trusted-publishing", "local-action-inputs",
];

var ruleAllocations = new List<(string ruleId, long allocated, int diagnostics)>();

foreach (var ruleId in ruleIds)
{
    // Create config that disables all rules except the target
    var ruleConfigs = new Dictionary<string, RuleConfig>(StringComparer.Ordinal);
    foreach (var otherId in ruleIds)
    {
        if (otherId != ruleId)
            ruleConfigs[otherId] = new RuleConfig { Enabled = false };
    }

    var singleRuleConfig = new LintConfig
    {
        Utf8Yaml = yamlBytes,
        FilePath = filePath,
        Rules = ruleConfigs,
        Fix = new FixConfig { Enabled = false, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } }
    };

    // Warmup this config
    var singleEngine = new LintEngine();
    singleEngine.Check(yamlBytes, filePath, singleRuleConfig);

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before = GC.GetTotalAllocatedBytes(precise: true);
    var result = singleEngine.Check(yamlBytes, filePath, singleRuleConfig);
    var after = GC.GetTotalAllocatedBytes(precise: true);

    ruleAllocations.Add((ruleId, after - before, result.Diagnostics.Length));
}

// Also measure "no rules" baseline (all disabled)
{
    var noRulesConfig = new Dictionary<string, RuleConfig>(StringComparer.Ordinal);
    foreach (var id in ruleIds)
        noRulesConfig[id] = new RuleConfig { Enabled = false };

    var baselineConfig = new LintConfig
    {
        Utf8Yaml = yamlBytes,
        FilePath = filePath,
        Rules = noRulesConfig,
        Fix = new FixConfig { Enabled = false, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } }
    };

    var baselineEngine = new LintEngine();
    baselineEngine.Check(yamlBytes, filePath, baselineConfig);

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before = GC.GetTotalAllocatedBytes(precise: true);
    var result = baselineEngine.Check(yamlBytes, filePath, baselineConfig);
    var after = GC.GetTotalAllocatedBytes(precise: true);

    Console.WriteLine($"  {"(baseline: no rules)",-42}  {after - before,10:N0} B  ({(after - before) / 1024.0,8:N1} KB)  diags={result.Diagnostics.Length}");
}

// Sort by allocation desc
ruleAllocations.Sort((a, b) => b.allocated.CompareTo(a.allocated));

Console.WriteLine();
Console.WriteLine($"  {"Rule",-42}  {"Allocated",10}  {"KB",8}  Diags");
Console.WriteLine($"  {new string('-', 42)}  {new string('-', 10)}  {new string('-', 8)}  {new string('-', 5)}");
foreach (var (ruleId, allocated, diags) in ruleAllocations)
{
    Console.WriteLine($"  {ruleId,-42}  {allocated,10:N0} B  ({allocated / 1024.0,6:N1} KB)  {diags}");
}

// ─── Also measure all-rules total ───
Console.WriteLine();
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before = GC.GetTotalAllocatedBytes(precise: true);
    var result = engine.Check(yamlBytes, filePath, lintConfig);
    var after = GC.GetTotalAllocatedBytes(precise: true);

    Console.WriteLine($"  {"ALL RULES TOTAL",-42}  {after - before,10:N0} B  ({(after - before) / 1024.0,6:N1} KB)  {result.Diagnostics.Length}");
    Console.WriteLine($"  Sum of individual rules: {ruleAllocations.Sum(x => x.allocated):N0} B");
    Console.WriteLine($"  (Difference is shared cost: parse, inline suppression, config normalization)");
}
