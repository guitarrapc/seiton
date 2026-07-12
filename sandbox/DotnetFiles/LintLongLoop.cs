#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
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

var engine = new LintEngine();
var cfg = new LintConfig
{
    Utf8Yaml = yamlBytes,
    FilePath = filePath,
    Fix = new FixConfig { Enabled = false, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 360 } }
};

const int Blocks = 15;
const int PerBlock = 100;
var times = new double[PerBlock];
for (var b = 0; b < Blocks; b++)
{
    for (var i = 0; i < PerBlock; i++)
    {
        var sw = Stopwatch.StartNew();
        using var r = engine.Check(yamlBytes, filePath, cfg);
        sw.Stop();
        times[i] = sw.Elapsed.TotalMilliseconds;
    }
    Array.Sort(times);
    Console.WriteLine($"block {b,2} (iters {b * PerBlock,5}-{(b + 1) * PerBlock - 1,5}): p50 {times[PerBlock / 2],7:N2} ms   min {times[0],7:N2} ms");
}
