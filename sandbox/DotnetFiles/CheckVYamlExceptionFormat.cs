#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core/Seiton.Core.csproj
using System.Text;
using Seiton.Core;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

// C-1: broken_yaml.yaml
Console.WriteLine("=== C-1: broken_yaml ===");
var brokenYaml = """
on: push
jobs:
  linux:
    runs-on: ubuntu-latest
    steps:
      - run: foo:
"""u8;
var result = WorkflowParser.Parse(brokenYaml.ToArray(), "test.yaml");
foreach (var d in result.Diagnostics)
    Console.WriteLine($"  Line={d.Location.StartLine} Col={d.Location.StartColumn} Msg={d.Message}");
Console.WriteLine($"  Expected: Line=6 Col=16");

// C-5: webhook option (release with tags)
Console.WriteLine("\n=== C-5: webhook option empty ===");
var webhookYaml = """
on:
  release:
    tags: v*.*.*
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - run: echo ok
"""u8;
result = WorkflowParser.Parse(webhookYaml.ToArray(), "test.yaml");
foreach (var d in result.Diagnostics)
    Console.WriteLine($"  Line={d.Location.StartLine} Col={d.Location.StartColumn} Msg={d.Message}");
Console.WriteLine($"  Expected: option name should be 'tags'");

// C-9: timeout-minutes col:0
Console.WriteLine("\n=== C-9: timeout-minutes ===");
var timeoutYaml = """
on: push
jobs:
  test:
    strategy:
      fail-fast: off
      max-parallel: 1.5
    runs-on: ubuntu-latest
    steps:
      - run: sleep 200
        timeout-minutes: two minutes
"""u8;
result = WorkflowParser.Parse(timeoutYaml.ToArray(), "test.yaml");
foreach (var d in result.Diagnostics)
    Console.WriteLine($"  Line={d.Location.StartLine} Col={d.Location.StartColumn} Offset={d.Location.Start} Msg={d.Message}");
Console.WriteLine($"  Expected: timeout-minutes at Line=10 Col=26");

// Check VYaml mark directly for a simple case
Console.WriteLine("\n=== VYaml mark baseline ===");
var simpleYaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n        timeout-minutes: two\n"u8;
result = WorkflowParser.Parse(simpleYaml.ToArray(), "test.yaml");
foreach (var d in result.Diagnostics)
    Console.WriteLine($"  Line={d.Location.StartLine} Col={d.Location.StartColumn} Offset={d.Location.Start} Msg={d.Message}");
Console.WriteLine("  Expected: timeout-minutes error at Line=7 Col=26");

// C-6/C-7/C-8: duplicate diagnostics
Console.WriteLine("\n=== C-6: duplicate diagnostics ===");
var dupYaml = """
on: push
jobs:
  test:
    steps:
      - run: echo ok
"""u8;
var lintResult = new LintEngine().Check(dupYaml.ToArray(), "test.yaml");
var runsOnDiags = lintResult.Diagnostics.Where(d => d.Message.Contains("requires runs-on")).ToList();
Console.WriteLine($"  'requires runs-on' count = {runsOnDiags.Count} (expected: 1)");
foreach (var d in runsOnDiags)
    Console.WriteLine($"    [{DiagnosticDisplayRuleIds.Resolve(d.RuleId)}] {d.Message}");

var stepsDiags = lintResult.Diagnostics.Where(d => d.Message.Contains("cannot have both uses and")).ToList();
var dupYaml2 = """
on: push
jobs:
  test:
    uses: org/repo/.github/workflows/build.yml@main
    runs-on: ubuntu-latest
    steps:
      - run: echo ok
"""u8;
lintResult = new LintEngine().Check(dupYaml2.ToArray(), "test.yaml");
var bothDiags = lintResult.Diagnostics.Where(d => d.Message.Contains("cannot have both")).ToList();
Console.WriteLine($"\n  'cannot have both' count = {bothDiags.Count} (expected: 2, uses+steps and uses+runs-on)");
foreach (var d in bothDiags)
    Console.WriteLine($"    [{DiagnosticDisplayRuleIds.Resolve(d.RuleId)}] {d.Message}");
