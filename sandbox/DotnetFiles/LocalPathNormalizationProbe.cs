#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core/Seiton.Core.csproj
using System.Diagnostics;
using System.Text;
using Seiton.Core.Linting;

const int stepCount = 60;
const int iterations = 200;

var repositoryRoot = Path.Combine(Path.GetTempPath(), "seiton-local-path-probe", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(repositoryRoot);

try
{
    var workflowDir = Path.Combine(repositoryRoot, ".github", "workflows");
    var actionDir = Path.Combine(repositoryRoot, ".github", "actions", "echo-output");
    Directory.CreateDirectory(workflowDir);
    Directory.CreateDirectory(actionDir);

    var actionYamlPath = Path.Combine(actionDir, "action.yml");
    File.WriteAllText(actionYamlPath, BuildActionYaml());

    var workflowPath = Path.Combine(workflowDir, "caller.yml");
    var workflowYaml = BuildWorkflowYaml(stepCount);
    var utf8Yaml = Encoding.UTF8.GetBytes(workflowYaml);
    File.WriteAllBytes(workflowPath, utf8Yaml);

    Warmup(utf8Yaml, workflowPath);

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();
    var diagnosticTotal = 0;
    for (var i = 0; i < iterations; i++)
    {
        using var result = new LintEngine().Check(utf8Yaml, workflowPath);
        diagnosticTotal += result.DiagnosticCount;
    }

    sw.Stop();
    var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

    Console.WriteLine($"Iterations: {iterations}");
    Console.WriteLine($"Steps: {stepCount}");
    Console.WriteLine($"ElapsedMsTotal: {sw.Elapsed.TotalMilliseconds:F3}");
    Console.WriteLine($"ElapsedMsPerOp: {sw.Elapsed.TotalMilliseconds / iterations:F6}");
    Console.WriteLine($"DiagnosticCountTotal: {diagnosticTotal}");
    Console.WriteLine($"AllocatedBytesTotal: {allocatedBytes}");
    Console.WriteLine($"AllocatedBytesPerOp: {(double)allocatedBytes / iterations:F2}");
}
finally
{
    if (Directory.Exists(repositoryRoot))
    {
        Directory.Delete(repositoryRoot, recursive: true);
    }
}

static void Warmup(byte[] utf8Yaml, string workflowPath)
{
    for (var i = 0; i < 10; i++)
    {
        using var result = new LintEngine().Check(utf8Yaml, workflowPath);
        _ = result.DiagnosticCount;
    }
}

static string BuildActionYaml()
{
    return """
    name: Echo output
    description: Emits a fixed output
    outputs:
      answer:
        description: Fixed answer
        value: ${{ steps.emit.outputs.answer }}
    runs:
      using: composite
      steps:
        - id: emit
          run: echo "answer=ok" >> "$GITHUB_OUTPUT"
          shell: bash
    """;
}

static string BuildWorkflowYaml(int stepCount)
{
    var sb = new StringBuilder();
    sb.AppendLine("name: probe");
    sb.AppendLine("on: push");
    sb.AppendLine("jobs:");
    sb.AppendLine("  build:");
    sb.AppendLine("    runs-on: ubuntu-latest");
    sb.AppendLine("    steps:");

    for (var i = 0; i < stepCount; i++)
    {
        sb.AppendLine($"      - id: action_{i}");
        sb.AppendLine("        uses: ./.github/actions/echo-output");
        sb.AppendLine("      - run: echo \"${{ steps.action_" + i + ".outputs.answer }}\"");
    }

    return sb.ToString();
}
