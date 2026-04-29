﻿#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core/Seiton.Core.csproj
using System.Text;
using Seiton.Core;
using Seiton.Core.Linting;

var withYaml = """
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    with:
      node-version: '20'
    steps:
      - run: echo ok
""";

var permissionsYaml = """
on: push
jobs:
  build:
    permissions:
      contents: admin
    runs-on: ubuntu-latest
    steps:
      - run: echo ok
""";

Dump("with", withYaml);
Dump("permissions", permissionsYaml);

static void Dump(string name, string yaml)
{
    var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), name + ".yml");
    Console.WriteLine($"## {name}");
    foreach (var diagnostic in result.Diagnostics)
    {
        Console.WriteLine(diagnostic.Message);
    }
}
