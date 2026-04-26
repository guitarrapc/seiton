#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core/Seiton.Core.csproj
#nullable disable
using System.Text;
using Seiton.Core;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;

// Raw string exactly as in the test, with NormalizeYaml
var raw = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: |
                            echo "::set-output name=foo::bar"
                            echo "::set-env name=TOKEN::x"
            """;

// Reproduce NormalizeYaml
var yaml = NormalizeYaml(raw);
Console.WriteLine($"Normalized YAML (escaped):");
foreach (var ch in yaml)
{
    if (ch == '\n') Console.Write("\\n");
    else if (ch == '\r') Console.Write("\\r");
    else Console.Write(ch);
}
Console.WriteLine();
Console.WriteLine($"YAML length: {yaml.Length}");

var bytes = Encoding.UTF8.GetBytes(yaml);
var parseResult = WorkflowParser.Parse(bytes, "test.yml");
var workflow = parseResult.Workflow;
if (workflow is null) { Console.WriteLine("No workflow"); return; }
foreach (var job in workflow.Jobs)
{
    var jobDef = job.Value;
    if (jobDef is null) continue;
    foreach (var step in jobDef.Steps)
    {
        if (step.Exec is Seiton.Core.Parsing.Ast.ExecRun run)
        {
            var val = parseResult.Arena.GetStringValue(run.Run);
            var str = Encoding.UTF8.GetString(val);
            Console.WriteLine($"\nrun.Run string value (len={val.Length}):");
            Console.Write("Hex: ");
            foreach (var b in val) Console.Write($"{b:X2} ");
            Console.WriteLine();
            Console.WriteLine($"---\n{str}\n---");

            var slice = parseResult.Arena.GetStringSlice(run.Run);
            Console.WriteLine($"Slice: offset={slice.Offset}, length={slice.Length}");
        }
    }
}

var result = new LintEngine([new DeprecatedCommandsRule()]).Check(bytes, "test.yml");
Console.WriteLine($"\nTotal diagnostics: {result.Diagnostics.Length}");
foreach (var d in result.Diagnostics)
{
    Console.WriteLine($"  [{d.RuleId}] {d.Location.StartLine}:{d.Location.StartColumn} {d.Message}");
}

static string NormalizeYaml(string raw2)
{
    var normalized = raw2.Replace("\r\n", "\n");
    var lines = normalized.Split('\n');
    var start = 0;
    while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start])) start++;
    var end = lines.Length - 1;
    while (end >= start && string.IsNullOrWhiteSpace(lines[end])) end--;
    if (end < start) return string.Empty;
    var minIndent = int.MaxValue;
    for (var i = start; i <= end; i++)
    {
        var line = lines[i];
        if (line.Length == 0) continue;
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        if (indent < line.Length && indent < minIndent) minIndent = indent;
    }
    if (minIndent == int.MaxValue) minIndent = 0;
    var sb = new StringBuilder();
    for (var i = start; i <= end; i++)
    {
        var line = lines[i];
        sb.Append(line.Length >= minIndent ? line[minIndent..] : line);
        if (i < end) sb.Append('\n');
    }
    return sb.ToString();
}
