using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class BotActorsCSharpGenerator
{
    public string Generate(BotActorsModel model)
    {
        var actors = model.BotActors
            .OrderBy(static x => x.Login, StringComparer.Ordinal)
            .ToArray();

        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-bot-actors");
        sb.AppendLine(
            """
            namespace Seiton.Core.Generated;

            internal static class BotActors
            {
            """);

        // IsKnownBotId — checks if a numeric string is a known bot user ID
        sb.AppendLine("    /// <summary>Returns true if the value matches a known bot user ID.</summary>");
        sb.AppendLine("    internal static bool IsKnownBotId(ReadOnlySpan<byte> value)");
        sb.AppendLine("    {");
        if (actors.Length == 0)
        {
            sb.AppendLine("        return false;");
        }
        else
        {
            for (var i = 0; i < actors.Length; i++)
            {
                var op = i == 0 ? "return " : "    || ";
                var suffix = i == actors.Length - 1 ? ";" : string.Empty;
                sb.AppendLine($"        {op}value.SequenceEqual(\"{actors[i].Id}\"u8){suffix}");
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        // ContainsKnownBotId — checks if a larger span contains any known bot user ID
        sb.AppendLine("    /// <summary>Returns true if the value contains any known bot user ID as a byte substring.</summary>");
        sb.AppendLine("    internal static bool ContainsKnownBotId(ReadOnlySpan<byte> value)");
        sb.AppendLine("    {");
        if (actors.Length == 0)
        {
            sb.AppendLine("        return false;");
        }
        else
        {
            for (var i = 0; i < actors.Length; i++)
            {
                var op = i == 0 ? "return " : "    || ";
                var suffix = i == actors.Length - 1 ? ";" : string.Empty;
                sb.AppendLine($"        {op}value.IndexOf(\"{actors[i].Id}\"u8) >= 0{suffix}");
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        // IsKnownBotLogin — checks if value matches a known bot login exactly
        sb.AppendLine("    /// <summary>Returns true if the value exactly matches a known bot login name.</summary>");
        sb.AppendLine("    internal static bool IsKnownBotLogin(ReadOnlySpan<byte> value)");
        sb.AppendLine("    {");
        if (actors.Length == 0)
        {
            sb.AppendLine("        return false;");
        }
        else
        {
            for (var i = 0; i < actors.Length; i++)
            {
                var op = i == 0 ? "return " : "    || ";
                var suffix = i == actors.Length - 1 ? ";" : string.Empty;
                sb.AppendLine($"        {op}value.SequenceEqual(\"{actors[i].Login}\"u8){suffix}");
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        // AllBotActorIds — for diagnostic messages
        var idList = string.Join(", ", actors.Select(static x => $"{x.Login}={x.Id}"));
        sb.AppendLine($"    internal const string AllBotActorInfo = \"{idList}\";");

        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
    }
}
