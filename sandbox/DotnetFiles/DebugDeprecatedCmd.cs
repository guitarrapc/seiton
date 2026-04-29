#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core/Seiton.Core.csproj
#nullable disable
using System.Text;
using Seiton.Core;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;

var yaml = File.ReadAllText("testdata/err/minimal_cycle_in_needs.yaml");
var bytes = Encoding.UTF8.GetBytes(yaml);
var parseResult = WorkflowParser.Parse(bytes, "test.yml");
var workflow = parseResult.Workflow;
if (workflow is null) { Console.WriteLine("No workflow"); return; }

foreach (var job in workflow.Jobs)
{
    var jobDef = job.Value;
    if (jobDef is null) continue;
    var idRange = parseResult.Arena.GetStringRange(jobDef.Id);
    var idSlice = parseResult.Arena.GetStringSlice(jobDef.Id);
    var idText = Encoding.UTF8.GetString(parseResult.Arena.GetStringValue(jobDef.Id));
    Console.WriteLine($"Job '{idText}': Range={idRange.StartLine}:{idRange.StartColumn} (start={idRange.Start}, len={idRange.Length}), Slice offset={idSlice.Offset} len={idSlice.Length}");
    Console.WriteLine($"  job.Range: {jobDef.Range.StartLine}:{jobDef.Range.StartColumn}");
}

var result = new LintEngine([new NeedsGraphRule()]).Check(bytes, "test.yml");
foreach (var d in result.Diagnostics.Where(d2 => d2.RuleId == "needs-graph"))
{
    Console.WriteLine($"  [{d.RuleId}] {d.Location.StartLine}:{d.Location.StartColumn} {d.Message}");
}
