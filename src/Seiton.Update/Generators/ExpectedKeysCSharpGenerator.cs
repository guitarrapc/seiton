using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class ExpectedKeysCSharpGenerator
{
    public string Generate(ExpectedKeysModel model)
    {
        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-expected-keys");
        sb.AppendLine(
            """
            namespace Seiton.Core.Generated;

            internal static class ExpectedKeys
            {
            """);

        for (var i = 0; i < model.Sections.Count; i++)
        {
            var section = model.Sections[i];
            var constantName = ToPascalCase(section.Name) + "Keys";
            var quotedKeys = section.Keys
                .Select(static k => $"\\\"{k}\\\"")
                .ToArray();
            var value = string.Join(", ", quotedKeys);

            sb.AppendLine($"    /// <summary>{section.Description}</summary>");
            sb.AppendLine($"    internal const string {constantName} = \"{value}\";");

            if (i < model.Sections.Count - 1)
            {
                sb.AppendLine();
            }
        }

        var jobSection = model.Sections.FirstOrDefault(static s => s.Name == "job");
        if (jobSection is not null)
        {
            sb.AppendLine();
            AppendJobMappingKeyArtifacts(sb, jobSection.Keys);
        }

        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
    }

    private static void AppendJobMappingKeyArtifacts(StringBuilder sb, IReadOnlyList<string> mappingKeys)
    {
        sb.AppendLine("    /// <summary>UTF-8 ordinals for jobs.&lt;job_id&gt; mapping keys; matches <see cref=\"JobMappingKeyTable\"/>.</summary>");
        sb.AppendLine("    internal enum JobMappingKey : byte");
        sb.AppendLine("    {");
        for (var i = 0; i < mappingKeys.Count; i++)
        {
            sb.AppendLine($"        {ToMappingKeyEnumName(mappingKeys[i])} = {i},");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>UTF-8 rows for <see cref=\"JobMappingKey\"/>; ordinal must match enum value and duplicate-tracking bit index.</summary>");
        sb.AppendLine("    internal readonly struct JobMappingKeyTable : global::Seiton.Core.Parsing.IUtf8OrderedKeyTable");
        sb.AppendLine("    {");
        sb.AppendLine($"        public static int KeyCount => {mappingKeys.Count};");
        sb.AppendLine();
        sb.AppendLine("        public static ReadOnlySpan<byte> Utf8Key(int ordinal) => ordinal switch");
        sb.AppendLine("        {");
        for (var i = 0; i < mappingKeys.Count; i++)
        {
            sb.AppendLine($"            {i} => \"{mappingKeys[i]}\"u8,");
        }

        sb.AppendLine("            _ => ReadOnlySpan<byte>.Empty,");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal static bool IsKnownJobKey(ReadOnlySpan<byte> keyUtf8)");
        sb.AppendLine("    {");
        if (mappingKeys.Count == 0)
        {
            sb.AppendLine("        return false;");
        }
        else
        {
            sb.Append("        return ");
            for (var i = 0; i < mappingKeys.Count; i++)
            {
                if (i > 0)
                {
                    sb.AppendLine();
                    sb.Append("            || ");
                }

                sb.Append($"keyUtf8.SequenceEqual(\"{mappingKeys[i]}\"u8)");
            }

            sb.AppendLine(";");
        }

        sb.AppendLine("    }");
    }

    private static string ToPascalCase(string kebabCase)
    {
        var parts = kebabCase.Split(['-', '.']);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
            {
                sb.Append(part, 1, part.Length - 1);
            }
        }

        return sb.ToString();
    }

    private static string ToMappingKeyEnumName(string key)
    {
        if (key.Length == 0)
        {
            throw new InvalidOperationException("Job mapping key must not be empty.");
        }

        if (!key.Contains('-', StringComparison.Ordinal))
        {
            if (key.Length == 1)
            {
                return char.ToUpperInvariant(key[0]).ToString();
            }

            return char.ToUpperInvariant(key[0]) + key[1..];
        }

        return string.Concat(key.Split('-').Select(static segment =>
            char.ToUpperInvariant(segment[0]) + segment[1..]));
    }
}
