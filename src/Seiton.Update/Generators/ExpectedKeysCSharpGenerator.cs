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

        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
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
}
