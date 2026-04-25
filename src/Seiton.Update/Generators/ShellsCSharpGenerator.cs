using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class ShellsCSharpGenerator
{
    public string Generate(ShellsModel model)
    {
        var allShells = model.Shells
            .Select(static x => x.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var linuxShells = model.Shells
            .Where(static x => x.Platforms.Any(p => p.Equals("linux", StringComparison.OrdinalIgnoreCase)))
            .Select(static x => x.Name)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var macosShells = model.Shells
            .Where(static x => x.Platforms.Any(p => p.Equals("macos", StringComparison.OrdinalIgnoreCase)))
            .Select(static x => x.Name)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var windowsShells = model.Shells
            .Where(static x => x.Platforms.Any(p => p.Equals("windows", StringComparison.OrdinalIgnoreCase)))
            .Select(static x => x.Name)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-shells");
        sb.AppendLine(
            """
            namespace Seiton.Core.Generated;

            internal static class Shells
            {
            """);

        // IsValidShell — all known shell names
        AppendShellCheck(sb, "IsValidShell", allShells);
        sb.AppendLine();

        // IsAvailableOnLinux
        AppendShellCheck(sb, "IsAvailableOnLinux", linuxShells);
        sb.AppendLine();

        // IsAvailableOnMacOS
        AppendShellCheck(sb, "IsAvailableOnMacOS", macosShells);
        sb.AppendLine();

        // IsAvailableOnWindows
        AppendShellCheck(sb, "IsAvailableOnWindows", windowsShells);
        sb.AppendLine();

        // AllValidShellNames — for diagnostic messages
        var quotedNames = allShells.Select(static x => $"\"{x}\"");
        sb.AppendLine($"    internal const string AllValidShellNames = \"{string.Join(", ", allShells)}\";");

        sb.AppendLine("}");

        return TextNormalization.NormalizeToLf(sb.ToString());
    }

    private static void AppendShellCheck(StringBuilder sb, string methodName, string[] shells)
    {
        sb.AppendLine($"    internal static bool {methodName}(ReadOnlySpan<byte> shellUtf8)");
        sb.AppendLine("    {");

        if (shells.Length == 0)
        {
            sb.AppendLine("        return false;");
        }
        else
        {
            for (var i = 0; i < shells.Length; i++)
            {
                var op = i == 0 ? "return " : "    || ";
                var suffix = i == shells.Length - 1 ? ";" : string.Empty;
                sb.AppendLine($"        {op}shellUtf8.SequenceEqual(\"{shells[i]}\"u8){suffix}");
            }
        }

        sb.AppendLine("    }");
    }
}
