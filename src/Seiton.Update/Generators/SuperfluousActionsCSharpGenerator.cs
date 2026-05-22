using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class SuperfluousActionsCSharpGenerator
{
    public string Generate(SuperfluousActionsModel model)
    {
        var actions = model.Actions.ToArray();

        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-superfluous-actions");
        sb.AppendLine(
            """
            namespace Seiton.Core.Generated;

            internal static class SuperfluousActions
            {
            """);

        // TryGetReplacement — matches lowercased owner/repo UTF-8, returns action name and replacement
        sb.AppendLine("    /// <summary>Returns true if the owner/repo matches a known superfluous action, outputting the display name and replacement tool.</summary>");
        sb.AppendLine("    internal static bool TryGetReplacement(ReadOnlySpan<byte> ownerRepo, out string actionName, out string replacement)");
        sb.AppendLine("    {");

        for (var i = 0; i < actions.Length; i++)
        {
            var ownerRepo = $"{actions[i].Owner}/{actions[i].Repo}";
            sb.AppendLine($"        if (ownerRepo.SequenceEqual(\"{ownerRepo}\"u8))");
            sb.AppendLine("        {");
            sb.AppendLine($"            actionName = \"{ownerRepo}\";");
            sb.AppendLine($"            replacement = \"{EscapeString(actions[i].Replacement)}\";");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            if (i < actions.Length - 1)
            {
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("        actionName = string.Empty;");
        sb.AppendLine("        replacement = string.Empty;");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");

        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
    }

    private static string EscapeString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
