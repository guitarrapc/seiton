#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core/Seiton.Core.csproj
using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

var yaml = """
on: push
permissions: write-all
jobs:
    build:
        runs-on: ubuntu-latest
        steps:
            - run: echo hello
""";
var config = new LintConfig {
    Rules = new Dictionary<string, RuleConfig> {
        ["deny-write-all"] = new RuleConfig { Severity = DiagnosticSeverity.Warning },
    },
};
using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml", config);
foreach (var d in result.Diagnostics) {
    Console.WriteLine($"[{d.Severity}] RuleId={d.RuleId ?? "(null)"} Msg={d.Message}");
}
