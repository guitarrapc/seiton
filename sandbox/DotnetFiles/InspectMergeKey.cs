#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:package VYaml
using VYaml.Parser;
using System.Text;

// Test how VYaml handles <<: *anchor merge key syntax
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
""";

var bytes = Encoding.UTF8.GetBytes(yaml).AsMemory();
var parser = YamlParser.FromBytes(bytes);

int count = 0;
try
{
    while (parser.Read())
    {
        count++;
        var ev = parser.CurrentEventType;
        string extra = "";
        if (ev == ParseEventType.Scalar)
        {
            try { extra = $" = '{Encoding.UTF8.GetString(parser.GetScalarAsUtf8())}'";
            } catch { extra = " = <error>"; }
        }
        else if (ev == ParseEventType.Alias)
        {
            if (parser.TryGetCurrentAnchor(out var anc))
                extra = $" -> anchor {anc.Id} ({anc.Name})";
        }
        else if (parser.TryGetCurrentAnchor(out var anc))
        {
            extra = $" &{anc.Name}({anc.Id})";
        }
        Console.WriteLine($"  [{count,3}] {ev}{extra}");
    }
    Console.WriteLine($"Done. {count} events.");
}
catch (Exception ex)
{
    Console.WriteLine($"EXCEPTION at event {count}: {ex.GetType().Name}: {ex.Message}");
}
