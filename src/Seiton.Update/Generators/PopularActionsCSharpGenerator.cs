using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class PopularActionsCSharpGenerator
{
    public string Generate(IReadOnlyList<PopularActionModel> actions)
    {
        var normalized = actions
            .Where(static x => !string.IsNullOrWhiteSpace(x.Uses))
            .Select(static x => new PopularActionModel(
                x.Uses,
                x.Inputs
                    .Where(static n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static n => n, StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(static x => x.Uses, StringComparer.Ordinal)
            .ToArray();

        var sb = new StringBuilder();
        sb.Append(
            """
            namespace Seiton.Core.Generated;

            internal static class PopularActions
            {
                internal enum ActionId
                {
            """);
        sb.AppendLine();

        foreach (var action in normalized)
        {
            sb.AppendLine($"        {ToActionIdName(action.Uses)},");
        }

        sb.Append(
            """
                }

                internal readonly struct ActionSpec
                {
                    internal ActionId Id { get; }

                    internal ActionSpec(ActionId id)
                    {
                        Id = id;
                    }

                    internal bool IsInputAllowed(ReadOnlySpan<byte> inputNameUtf8)
                    {
                        return Id switch
                        {
            """);
        sb.AppendLine();

        foreach (var action in normalized)
        {
            var actionId = ToActionIdName(action.Uses);
            if (action.Inputs.Count == 0)
            {
                sb.AppendLine($"                ActionId.{actionId} => false,");
                continue;
            }

            sb.AppendLine($"                ActionId.{actionId} =>");
            for (var i = 0; i < action.Inputs.Count; i++)
            {
                var input = action.Inputs[i];
                var suffix = i == action.Inputs.Count - 1 ? "," : "";
                var op = i == 0 ? "" : "|| ";
                sb.AppendLine($"                    {op}EqualsAsciiIgnoreCase(inputNameUtf8, \"{input}\"u8){suffix}");
            }
        }

        sb.Append(
            """
                            _ => false,
                        };
                    }
                }

                internal static bool TryGet(ReadOnlySpan<byte> usesUtf8, out ActionSpec spec)
                {
            """);
        sb.AppendLine();

        foreach (var action in normalized)
        {
            var usesName = action.Uses;
            var actionId = ToActionIdName(action.Uses);
            sb.AppendLine($"        if (MatchesActionReference(usesUtf8, \"{usesName}\"u8))");
            sb.AppendLine("        {");
            sb.AppendLine($"            spec = new ActionSpec(ActionId.{actionId});");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.Append(
            """
                    spec = default;
                    return false;
                }

                static bool MatchesActionReference(ReadOnlySpan<byte> usesUtf8, ReadOnlySpan<byte> actionNameUtf8)
                {
                    if (usesUtf8.IsEmpty)
                    {
                        return false;
                    }

                    if (usesUtf8.StartsWith("./"u8) || usesUtf8.StartsWith("../"u8) || usesUtf8.StartsWith("docker://"u8))
                    {
                        return false;
                    }

                    if (usesUtf8.Length < actionNameUtf8.Length)
                    {
                        return false;
                    }

                    if (!EqualsAsciiIgnoreCase(usesUtf8.Slice(0, actionNameUtf8.Length), actionNameUtf8))
                    {
                        return false;
                    }

                    return usesUtf8.Length == actionNameUtf8.Length || usesUtf8[actionNameUtf8.Length] == (byte)'@';
                }

                static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
                {
                    if (left.Length != right.Length)
                    {
                        return false;
                    }

                    for (var i = 0; i < left.Length; i++)
                    {
                        if (ToLowerAscii(left[i]) != ToLowerAscii(right[i]))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                static byte ToLowerAscii(byte value)
                {
                    return value is >= (byte)'A' and <= (byte)'Z'
                        ? (byte)(value + 32)
                        : value;
                }
            }
            """);

        return TextNormalization.NormalizeToLf(sb.ToString());
    }

    static string ToActionIdName(string uses)
    {
        var parts = uses
            .Split(new[] { '/', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static p => new string(p.Where(static c => char.IsLetterOrDigit(c)).ToArray()))
            .Where(static p => p.Length > 0)
            .ToArray();

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            sb.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
            {
                sb.Append(part.AsSpan(1));
            }
        }

        return sb.ToString();
    }
}
