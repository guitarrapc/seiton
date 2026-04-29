#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core/Seiton.Core.csproj
using System.Text;
using Seiton.Core;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

// Test 1: quoted float in step timeout-minutes
var yaml1 = """
on: push
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - run: echo 'quoted float'
        timeout-minutes: '3.5'
""";

// Test 2: direct parse test
var yaml2 = Encoding.UTF8.GetBytes(yaml1);
var parseResult = WorkflowParser.Parse(yaml2, "test.yaml");
Console.WriteLine($"Parse diagnostics: {parseResult.Diagnostics.Length}");
foreach (var d in parseResult.Diagnostics)
{
    Console.WriteLine($"  PARSE {d.Location.StartLine}:{d.Location.StartColumn}: {d.Message} [{d.RuleId}]");
}

var lintResult = new LintEngine().Check(yaml2, "test.yaml");
Console.WriteLine($"Lint diagnostics: {lintResult.Diagnostics.Length}");
foreach (var d in lintResult.Diagnostics)
{
    Console.WriteLine($"  LINT {d.Location.StartLine}:{d.Location.StartColumn}: {d.Message} [{d.RuleId}]");
}
