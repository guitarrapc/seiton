namespace Seiton.Benchmark;

internal static class WorkflowYamlBuilder
{
    /// <param name="jobCount">Number of jobs to generate.</param>
    /// <param name="stepsPerJob">Number of steps per job.</param>
    /// <param name="nameSuffix">Appended to workflow <c>name:</c> — changes root section hash (full-change scenario).</param>
    /// <param name="firstJobStepSuffix">Appended to first job's step name — changes only job0 hash (partial-change scenario).</param>
    internal static string Build(int jobCount, int stepsPerJob, string? nameSuffix = null, string? firstJobStepSuffix = null)
    {
        var sb = new System.Text.StringBuilder(capacity: 8_192);
        sb.Append("name: bench").AppendLine(nameSuffix ?? "");
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

            // For partial-change scenario: modify first step name in job0 only
            var stepSuffix = (j == 0) ? firstJobStepSuffix : null;

            for (var s = 0; s < stepsPerJob; s++)
            {
                if ((s & 1) == 0)
                {
                    sb.Append("      - name: Run").AppendLine(s == 0 && stepSuffix is not null ? stepSuffix : "");
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
}
