#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core/Seiton.Core.csproj
using System.Text;
using Seiton.Core.Parsing;

var yaml = """
on:
  workflow_dispatch:
    inputs: &inputs
      foo:
        type: string
  workflow_call:
    inputs:
      <<: *inputs
      bar:
        type: string

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - run: env
        env: &default_env
          FOO: BAR
      - run: env
        env:
          <<: *default_env
          TEST: test
      - &default_step
        run: echo hello
        working-directory: /foo/bar
        shell: bash
      - <<: *default_step
        run: echo bye
""";

var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");

Console.WriteLine("=== All Diagnostics ===");
foreach (var d in result.Diagnostics)
{
    Console.WriteLine($"  {d.Location.StartLine}:{d.Location.StartColumn} [{d.Severity}] {d.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Expected positions ===");
Console.WriteLine("  8:7  — <<: *inputs");
Console.WriteLine("  21:11 — <<: *default_env");
Console.WriteLine("  27:9  — <<: *default_step");
