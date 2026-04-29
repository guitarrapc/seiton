﻿#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core/Seiton.Core.csproj
using System;
using Seiton.Core.Linting.Fixing;

static string NormalizeEol(string value)
{
    return value
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\r", "\n", StringComparison.Ordinal);
}

static string NormalizeYamlLiteral(string value)
{
    var normalized = NormalizeEol(value);
    var lines = normalized.Split('\n');

    var minIndent = int.MaxValue;
    for (var i = 0; i < lines.Length; i++)
    {
        var line = lines[i];
        if (line.Length == 0)
        {
            continue;
        }

        var indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        if (indent == line.Length)
        {
            continue;
        }

        if (indent < minIndent)
        {
            minIndent = indent;
        }
    }

    if (minIndent == int.MaxValue || minIndent == 0)
    {
        return normalized;
    }

    for (var i = 0; i < lines.Length; i++)
    {
        var line = lines[i];
        if (line.Length >= minIndent)
        {
            lines[i] = line[minIndent..];
        }
    }

    return string.Join("\n", lines);
}

var source1 = NormalizeYamlLiteral("""
        jobs:
          build:
            runs-on: ubuntu-latest
        """) + "  \tsteps:\n";

Console.WriteLine("--source1 lines--");
var s1Lines = source1.Split('\n');
for (var i = 0; i < s1Lines.Length; i++)
{
    Console.WriteLine($"{i + 1}: [{s1Lines[i].Replace("\t", "\\t")}] ");
}
var ok1 = FixFormatting.TryInferIndentation(source1, null, 3, 4, 5, out var indent1);
Console.WriteLine($"ok1={ok1}, indent=[{indent1.Replace("\t", "\\t")}] unit=[{FixFormatting.InferIndentationUnit(source1).Replace("\t", "\\t")}]\n");

var source2 = NormalizeYamlLiteral("""
        jobs:
          build: {}
        """) + "\tnote: tab-leading\n";

Console.WriteLine("--source2 lines--");
var s2Lines = source2.Split('\n');
for (var i = 0; i < s2Lines.Length; i++)
{
    Console.WriteLine($"{i + 1}: [{s2Lines[i].Replace("\t", "\\t")}] ");
}
var ok2 = FixFormatting.TryInferIndentation(source2, null, 3, 4, 4, out var indent2);
Console.WriteLine($"ok2={ok2}, indent=[{indent2.Replace("\t", "\\t")}] unit=[{FixFormatting.InferIndentationUnit(source2).Replace("\t", "\\t")}]\n");
