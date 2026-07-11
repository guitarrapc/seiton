#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Configuration=Release
#:project ../../src/Seiton.Core

using System.Diagnostics;
using System.Text;
using Seiton.Core.Linting;

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

const int Iterations = 7;

static double MeasureMs(LintEngine engine, byte[] yamlBytes, string filePath, LintConfig config)
{
    // warmup
    for (var i = 0; i < 3; i++)
    {
        using var _ = engine.Check(yamlBytes, filePath, config);
    }
    var best = double.MaxValue;
    for (var i = 0; i < Iterations; i++)
    {
        var sw = Stopwatch.StartNew();
        using var _ = engine.Check(yamlBytes, filePath, config);
        sw.Stop();
        best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
    }
    return best;
}

LintConfig MakeConfig(Dictionary<string, RuleConfig>? rules) => new()
{
    Utf8Yaml = yamlBytes,
    FilePath = filePath,
    Rules = rules,
    Fix = new FixConfig { Enabled = false, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } }
};

// all-rules total
var allEngine = new LintEngine();
var allMs = MeasureMs(allEngine, yamlBytes, filePath, MakeConfig(null));

// baseline: all disabled
var offRules = new Dictionary<string, RuleConfig>(StringComparer.Ordinal);
foreach (var id in ruleIds) offRules[id] = new RuleConfig { Enabled = false };
var baseEngine = new LintEngine();
var baseMs = MeasureMs(baseEngine, yamlBytes, filePath, MakeConfig(offRules));

Console.WriteLine($"ALL RULES : {allMs,8:N2} ms");
Console.WriteLine($"NO RULES  : {baseMs,8:N2} ms (parse + shared)");
Console.WriteLine();

var results = new List<(string ruleId, double ms)>();
foreach (var ruleId in ruleIds)
{
    var rules = new Dictionary<string, RuleConfig>(StringComparer.Ordinal);
    foreach (var otherId in ruleIds)
    {
        if (otherId != ruleId) rules[otherId] = new RuleConfig { Enabled = false };
    }
    var engine = new LintEngine();
    var ms = MeasureMs(engine, yamlBytes, filePath, MakeConfig(rules));
    results.Add((ruleId, ms - baseMs));
}

results.Sort((a, b) => b.ms.CompareTo(a.ms));
Console.WriteLine($"  {"Rule",-42}  {"ms over baseline",16}");
foreach (var (ruleId, ms) in results)
{
    Console.WriteLine($"  {ruleId,-42}  {ms,16:N2}");
}
