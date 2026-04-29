#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:package VYaml
using VYaml.Parser;
using System.Text;

var path = @"D:\github\guitarrapc\seiton\tests\Seiton.Core.Tests\fixtures\schema\actionlint\testdata\err\merge_key_unsupported.yaml";
var bytes = System.IO.File.ReadAllBytes(path);
var parser = YamlParser.FromBytes(bytes.AsMemory());

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
