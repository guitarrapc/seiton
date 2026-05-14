using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class IanaTimeZonesCSharpGenerator
{
    public string Generate(IanaTimeZonesModel model)
    {
        var allIds = model.ZoneIds
            .Concat(model.LinkIds)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-iana-timezones");
        sb.AppendLine("using System.Collections.Frozen;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine();
        sb.AppendLine("namespace Seiton.Core.Generated;");
        sb.AppendLine();
        sb.AppendLine($"/// <summary>IANA Time Zone Database identifiers (version: {model.Version}). {allIds.Length} entries.</summary>");
        sb.AppendLine("internal static class IanaTimeZones");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Returns true if the given string is a known IANA timezone identifier (case-sensitive).</summary>");
        sb.AppendLine("    internal static bool IsKnown(string id) => KnownIds.Contains(id);");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Returns true if the given UTF-8 span is a known IANA timezone identifier (case-sensitive, zero-allocation).</summary>");
        sb.AppendLine("    internal static bool IsKnown(ReadOnlySpan<byte> utf8Id)");
        sb.AppendLine("    {");
        sb.AppendLine("        Span<char> chars = stackalloc char[utf8Id.Length];");
        sb.AppendLine("        var charCount = Encoding.UTF8.GetChars(utf8Id, chars);");
        sb.AppendLine("        return AlternateLookup.Contains(chars[..charCount]);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static readonly FrozenSet<string> KnownIds = FrozenSet.ToFrozenSet(");
        sb.AppendLine("    [");

        for (var i = 0; i < allIds.Length; i++)
        {
            var suffix = i < allIds.Length - 1 ? "," : "";
            sb.AppendLine($"        \"{allIds[i]}\"{suffix}");
        }

        sb.AppendLine("    ], StringComparer.Ordinal);");
        sb.AppendLine();
        sb.AppendLine("    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> AlternateLookup = KnownIds.GetAlternateLookup<ReadOnlySpan<char>>();");
        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
    }
}
