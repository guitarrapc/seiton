using VYaml.Parser;
using System.Text;

var yaml = """
on:
  push:
    branch: foo
  issues:
    types: created
  release:
    tags: v*.*.*
"""u8;

var parser = YamlParser.FromBytes(yaml);

while (parser.Read())
{
    if (parser.CurrentEventType == ParseEventType.Scalar)
    {
        var mark = parser.CurrentMark;
        var value = Encoding.UTF8.GetString(parser.GetScalarAsUtf8());
        Console.WriteLine($"Scalar: '{value}' at Position={mark.Position}, Line={mark.Line}, Col={mark.Col}");
    }
}
parser.Dispose();
