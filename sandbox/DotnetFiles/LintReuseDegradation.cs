#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Configuration=Release
#:project ../../src/Seiton.Core

using System.Diagnostics;
using System.Text;
using Seiton.Core.Linting;

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

LintConfig MakeCfg() => new()
{
    Utf8Yaml = yamlBytes,
    FilePath = filePath,
    Fix = new FixConfig { Enabled = false, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } }
};

void Run(string label, bool reuseEngine, bool reuseConfig, int n = 20)
{
    Console.WriteLine($"--- {label} ---");
    var engine = new LintEngine();
    var cfg = MakeCfg();
    for (var i = 0; i < n; i++)
    {
        var e = reuseEngine ? engine : new LintEngine();
        var c = reuseConfig ? cfg : MakeCfg();
        var sw = Stopwatch.StartNew();
        using var r = e.Check(yamlBytes, filePath, c);
        sw.Stop();
        if (i < 5 || i % 5 == 0 || i == n - 1)
            Console.WriteLine($"  iter {i,2}: {sw.Elapsed.TotalMilliseconds,8:N2} ms  diags={r.Diagnostics.Length}");
    }
}

Run("reuse engine + reuse config (benchmark)", reuseEngine: true, reuseConfig: true);
Run("fresh engine + reuse config", reuseEngine: false, reuseConfig: true);
Run("reuse engine + fresh config", reuseEngine: true, reuseConfig: false);
Run("fresh engine + fresh config", reuseEngine: false, reuseConfig: false);
