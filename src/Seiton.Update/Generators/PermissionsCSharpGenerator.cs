using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class PermissionsCSharpGenerator
{
    public string Generate(PermissionsModel model)
    {
        var scopes = model.Scopes
            .OrderBy(static s => s.Name, StringComparer.Ordinal)
            .ToArray();

        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-permissions");
        sb.AppendLine("namespace Seiton.Core.Generated;");
        sb.AppendLine();
        sb.AppendLine("internal static class PermissionScopes");
        sb.AppendLine("{");

        // AllScopeNames array
        sb.AppendLine("    /// <summary>All known permission scope names, sorted alphabetically.</summary>");
        sb.AppendLine("    internal static readonly string[] AllScopeNames =");
        sb.AppendLine("    [");
        for (var i = 0; i < scopes.Length; i++)
        {
            var comma = i < scopes.Length - 1 ? "," : "";
            sb.AppendLine($"        \"{scopes[i].Name}\"{comma}");
        }
        sb.AppendLine("    ];");
        sb.AppendLine();

        // AllScopesList pre-formatted string for error messages
        sb.Append("    /// <summary>Pre-formatted list of all scope names for error messages.</summary>");
        sb.AppendLine();
        sb.AppendLine($"    internal static readonly string AllScopesList = \"{string.Join(", ", scopes.Select(static s => "\\\"" + s.Name + "\\\""))}\";");
        sb.AppendLine();

        // IsKnownScope method — string switch, called with a decoded scope name
        sb.AppendLine("    internal static bool IsKnownScope(string name)");
        sb.AppendLine("    {");
        sb.AppendLine("        return name switch");
        sb.AppendLine("        {");
        foreach (var scope in scopes)
        {
            sb.AppendLine($"            \"{scope.Name}\" => true,");
        }
        sb.AppendLine("            _ => false,");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine();

        // GetAllowedValues method — returns allowed values for a scope, or null if unknown
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Returns the allowed permission values for the given scope name,");
        sb.AppendLine("    /// or null if the scope is unknown.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    internal static string[]? GetAllowedValues(string scopeName)");
        sb.AppendLine("    {");
        sb.AppendLine("        return scopeName switch");
        sb.AppendLine("        {");

        // Group scopes by their allowed values to minimize code duplication
        var groups = scopes.GroupBy(s => string.Join("|", s.Allowed), StringComparer.Ordinal).ToArray();
        foreach (var group in groups)
        {
            var members = group.ToArray();
            var allowedArray = $"[{string.Join(", ", members[0].Allowed.Select(static v => $"\"{v}\""))}]";
            foreach (var m in members)
            {
                sb.AppendLine($"            \"{m.Name}\" => {allowedArray},");
            }
        }

        sb.AppendLine("            _ => null,");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine();

        // GetDeprecationNote method — UTF-8 span based, so active scopes cost no string materialization
        var deprecated = scopes.Where(static s => s.DeprecationNote is not null).ToArray();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Returns the deprecation note for a scope GitHub has retired but still accepts,");
        sb.AppendLine("    /// or null when the scope is active or unknown.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    internal static string? GetDeprecationNote(ReadOnlySpan<byte> scopeNameUtf8)");
        sb.AppendLine("    {");
        foreach (var scope in deprecated)
        {
            sb.AppendLine($"        if (scopeNameUtf8.SequenceEqual(\"{scope.Name}\"u8))");
            sb.AppendLine("        {");
            sb.AppendLine($"            return \"{EscapeCSharpString(scope.DeprecationNote!)}\";");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("        return null;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
    }

    /// <summary>
    /// Escapes a deprecation note for emission into a regular C# string literal. Line terminators
    /// matter most: a raw one produces an unterminated literal, and <c>verify-permissions</c> compares
    /// text only, so the broken file would reach the compiler unnoticed. Scope names are emitted
    /// unescaped: every producer of the snapshot constrains them to lowercase kebab-case.
    /// </summary>
    private static string EscapeCSharpString(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    // NEL and the Unicode line separators are line terminators in C#; the remaining
                    // control characters are escaped too so the emitted literal stays readable.
                    if (char.IsControl(c) || c == '\u2028' || c == '\u2029')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
