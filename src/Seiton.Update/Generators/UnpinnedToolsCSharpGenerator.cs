using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class UnpinnedToolsCSharpGenerator
{
    public string Generate(UnpinnedToolsModel model)
    {
        var actions = model.Actions.ToArray();

        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-unpinned-tools");
        sb.AppendLine(
            """
            namespace Seiton.Core.Generated;

            internal static class UnpinnedToolsActions
            {
            """);

        // TryGetKnownActionIndex — matches lowercased owner/repo UTF-8
        sb.AppendLine("    internal static bool TryGetKnownActionIndex(ReadOnlySpan<byte> ownerRepo, out int index)");
        sb.AppendLine("    {");

        for (var i = 0; i < actions.Length; i++)
        {
            var ownerRepo = $"{actions[i].Owner}/{actions[i].Repo}";
            sb.AppendLine($"        if (ownerRepo.SequenceEqual(\"{ownerRepo}\"u8)) {{ index = {i}; return true; }}");
        }

        sb.AppendLine("        index = -1;");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // GetVersionInputKey — returns version input key as UTF-8 span
        sb.AppendLine("    internal static ReadOnlySpan<byte> GetVersionInputKey(int index) => index switch");
        sb.AppendLine("    {");
        for (var i = 0; i < actions.Length; i++)
        {
            sb.AppendLine($"        {i} => \"{actions[i].VersionInput}\"u8,");
        }
        sb.AppendLine("        _ => default,");
        sb.AppendLine("    };");
        sb.AppendLine();

        // GetMissingVersionMessage
        sb.AppendLine("    internal static string GetMissingVersionMessage(int index) => index switch");
        sb.AppendLine("    {");
        for (var i = 0; i < actions.Length; i++)
        {
            var ownerRepo = $"{actions[i].Owner}/{actions[i].Repo}";
            sb.AppendLine($"        {i} => \"'{ownerRepo}' does not specify '{actions[i].VersionInput}' input; implicitly uses unpinned latest version\",");
        }
        sb.AppendLine("        _ => \"\",");
        sb.AppendLine("    };");
        sb.AppendLine();

        // GetLatestMessage
        sb.AppendLine("    internal static string GetLatestMessage(int index) => index switch");
        sb.AppendLine("    {");
        for (var i = 0; i < actions.Length; i++)
        {
            var ownerRepo = $"{actions[i].Owner}/{actions[i].Repo}";
            sb.AppendLine($"        {i} => \"'{ownerRepo}' specifies '{actions[i].VersionInput}: latest' which is unpinned; pin to a specific version\",");
        }
        sb.AppendLine("        _ => \"\",");
        sb.AppendLine("    };");
        sb.AppendLine();

        // GetDynamicMessage
        sb.AppendLine("    internal static string GetDynamicMessage(int index) => index switch");
        sb.AppendLine("    {");
        for (var i = 0; i < actions.Length; i++)
        {
            var ownerRepo = $"{actions[i].Owner}/{actions[i].Repo}";
            sb.AppendLine($"        {i} => \"'{ownerRepo}' specifies '{actions[i].VersionInput}' dynamically which may be unpinned\",");
        }
        sb.AppendLine("        _ => \"\",");
        sb.AppendLine("    };");

        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
    }
}
