#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Configuration=Release
#:project ../../src/Seiton.Core

using System.Diagnostics;
using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

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

const int Iterations = 9;

static double Measure(string label, Action op)
{
    for (var i = 0; i < 3; i++) op();
    var best = double.MaxValue;
    for (var i = 0; i < Iterations; i++)
    {
        var sw = Stopwatch.StartNew();
        op();
        sw.Stop();
        best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
    }
    Console.WriteLine($"{label,-40} {best,8:N2} ms");
    return best;
}

Measure("WorkflowParser.Parse", () =>
{
    using var r = WorkflowParser.Parse(yamlBytes, filePath);
});

Measure("LintEngine.Check (all default rules)", () =>
{
    var engine = new LintEngine();
    var cfg = new LintConfig { Utf8Yaml = yamlBytes, FilePath = filePath, Fix = new FixConfig { Enabled = false, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } } };
    using var r = engine.Check(yamlBytes, filePath, cfg);
});

// reused engine like benchmark does
var sharedEngine = new LintEngine();
var sharedCfg = new LintConfig { Utf8Yaml = yamlBytes, FilePath = filePath, Fix = new FixConfig { Enabled = false, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } } };
Measure("LintEngine.Check (reused engine)", () =>
{
    using var r = sharedEngine.Check(yamlBytes, filePath, sharedCfg);
});
