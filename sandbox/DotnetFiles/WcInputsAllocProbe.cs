#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core

using System.Diagnostics;
using System.Text;
using Seiton.Core.Linting;

// Workflow with N workflow_call inputs, each default back-referencing the previous input.
static string BuildYaml(int inputCount)
{
    var sb = new StringBuilder(16_384);
    sb.AppendLine("on:");
    sb.AppendLine("  workflow_call:");
    sb.AppendLine("    inputs:");
    sb.AppendLine("      input0:");
    sb.AppendLine("        type: string");
    for (var i = 1; i < inputCount; i++)
    {
        sb.AppendLine($"      input{i}:");
        sb.AppendLine("        type: string");
        sb.AppendLine($"        default: ${{{{ inputs.input{i - 1} }}}}");
    }
    sb.AppendLine("jobs:");
    sb.AppendLine("  test:");
    sb.AppendLine("    runs-on: ubuntu-latest");
    sb.AppendLine("    steps:");
    sb.AppendLine("      - run: echo ok");
    return sb.ToString().Replace("\r\n", "\n");
}

var yamlBytes = Encoding.UTF8.GetBytes(BuildYaml(inputCount: 50));
var filePath = "wc-inputs-probe.yml";
var engine = new LintEngine();
var cfg = new LintConfig { Utf8Yaml = yamlBytes, FilePath = filePath, Fix = new FixConfig() };

for (var i = 0; i < 20; i++)
{
    using var _ = engine.Check(yamlBytes, filePath, cfg);
}

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
var before = GC.GetTotalAllocatedBytes(precise: true);
var sw = Stopwatch.StartNew();
using (var r = engine.Check(yamlBytes, filePath, cfg))
{
    sw.Stop();
    var after = GC.GetTotalAllocatedBytes(precise: true);
    Console.WriteLine($"diags={r.Diagnostics.Length}  alloc={after - before:N0} B  time={sw.Elapsed.TotalMilliseconds:N2} ms");
}
