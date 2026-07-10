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
                    .Where(static n => !string.IsNullOrWhiteSpace(n.Name))
                    .DistinctBy(static n => n.Name, StringComparer.Ordinal)
                    .OrderBy(static n => n.Name, StringComparer.Ordinal)
                    .ToArray(),
                x.Outputs
                    .Where(static n => !string.IsNullOrWhiteSpace(n.Name))
                    .DistinctBy(static n => n.Name, StringComparer.Ordinal)
                    .OrderBy(static n => n.Name, StringComparer.Ordinal)
                    .ToArray(),
                x.RunsUsing,
                x.MaxDeprecatedMajorVersion,
                x.RequiredPermissions
                    .Where(static p => !string.IsNullOrWhiteSpace(p.Scope))
                    .DistinctBy(static p => (p.Scope, p.Access), EqualityComparer<(string, string)>.Default)
                    .OrderBy(static p => p.Scope, StringComparer.Ordinal)
                    .ThenBy(static p => p.Access, StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(static x => x.Uses, StringComparer.Ordinal)
            .ToArray();

        var sb = new StringBuilder();
        // Per-action list accessors return cached static arrays: callers treat them as
        // read-only, and per-call collection expressions would allocate on every lookup
        // (GetOutputNames is on ExprUndefinedVarRule's per-step hot path).
        var staticFields = new StringBuilder();
        AppendGeneratedHeader(sb, "sync-popular-actions");
        sb.AppendLine(
            """
            namespace Seiton.Core.Generated;

            internal static class PopularActions
            {
                internal enum ActionId
                {
            """);

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
                sb.AppendLine($"                    {op}EqualsAsciiIgnoreCase(inputNameUtf8, \"{input.Name}\"u8){suffix}");
            }
        }

        sb.Append(
            """
                            _ => false,
                        };
                    }

                    internal bool IsInputRequired(ReadOnlySpan<byte> inputNameUtf8)
                    {
                        return Id switch
                        {
            """);
        sb.AppendLine();

        foreach (var action in normalized)
        {
            var actionId = ToActionIdName(action.Uses);
            var requiredInputs = action.Inputs.Where(static i => i.Required).ToArray();
            if (requiredInputs.Length == 0)
            {
                sb.AppendLine($"                ActionId.{actionId} => false,");
                continue;
            }

            sb.AppendLine($"                ActionId.{actionId} =>");
            for (var i = 0; i < requiredInputs.Length; i++)
            {
                var input = requiredInputs[i];
                var suffix = i == requiredInputs.Length - 1 ? "," : "";
                var op = i == 0 ? "" : "|| ";
                sb.AppendLine($"                    {op}EqualsAsciiIgnoreCase(inputNameUtf8, \"{input.Name}\"u8){suffix}");
            }
        }

        sb.AppendLine(
            """
                            _ => false,
                        };
                    }

                    internal byte[][] GetRequiredInputs()
                    {
                        return Id switch
                        {
            """);

        foreach (var action in normalized)
        {
            var actionId = ToActionIdName(action.Uses);
            var requiredInputs = action.Inputs.Where(static i => i.Required).ToArray();
            if (requiredInputs.Length == 0)
            {
                sb.AppendLine($"                ActionId.{actionId} => [],");
                continue;
            }

            var items = string.Join(", ", requiredInputs.Select(static i => $"\"{i.Name}\"u8.ToArray()"));
            staticFields.AppendLine($"        private static readonly byte[][] RequiredInputs{actionId} = [{items}];");
            sb.AppendLine($"                ActionId.{actionId} => RequiredInputs{actionId},");
        }

        sb.Append(
            """
                            _ => [],
                        };
                    }

                    internal string[] GetInputNames()
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
                sb.AppendLine($"                ActionId.{actionId} => [],");
                continue;
            }

            var items = string.Join(", ", action.Inputs.Select(static i => $"\"{i.Name}\""));
            staticFields.AppendLine($"        private static readonly string[] InputNames{actionId} = [{items}];");
            sb.AppendLine($"                ActionId.{actionId} => InputNames{actionId},");
        }

        sb.Append(
            """
                            _ => [],
                        };
                    }

                    internal byte[][] GetOutputNames()
                    {
                        return Id switch
                        {
            """);
        sb.AppendLine();

        foreach (var action in normalized)
        {
            var actionId = ToActionIdName(action.Uses);
            if (action.Outputs.Count == 0)
            {
                sb.AppendLine($"                ActionId.{actionId} => [],");
                continue;
            }

            var items = string.Join(", ", action.Outputs.Select(static o => $"\"{o.Name}\"u8.ToArray()"));
            staticFields.AppendLine($"        private static readonly byte[][] OutputNames{actionId} = [{items}];");
            sb.AppendLine($"                ActionId.{actionId} => OutputNames{actionId},");
        }

        sb.Append(
            """
                            _ => [],
                        };
                    }

                    internal ReadOnlySpan<byte> GetDeprecatedInputMessage(ReadOnlySpan<byte> inputNameUtf8)
                    {
                        return Id switch
                        {
            """);
        sb.AppendLine();

        foreach (var action in normalized)
        {
            var actionId = ToActionIdName(action.Uses);
            var deprecatedInputs = action.Inputs.Where(static i => !string.IsNullOrWhiteSpace(i.DeprecationMessage)).ToArray();
            if (deprecatedInputs.Length == 0)
            {
                sb.AppendLine($"                ActionId.{actionId} => default,");
                continue;
            }

            sb.AppendLine($"                ActionId.{actionId} =>");
            for (var i = 0; i < deprecatedInputs.Length; i++)
            {
                var input = deprecatedInputs[i];
                var message = EscapeCSharpString(input.DeprecationMessage!);
                var prefix = i == 0 ? "" : ": ";
                if (i == 0)
                {
                    sb.AppendLine($"                    EqualsAsciiIgnoreCase(inputNameUtf8, \"{input.Name}\"u8) ? \"{message}\"u8");
                }
                else
                {
                    sb.AppendLine($"                    : EqualsAsciiIgnoreCase(inputNameUtf8, \"{input.Name}\"u8) ? \"{message}\"u8");
                }
            }
            sb.AppendLine("                    : default,");
        }

        sb.Append(
            """
                            _ => default,
                        };
                    }

                    internal ReadOnlySpan<byte> GetRunsUsing()
                    {
                        return Id switch
                        {
            """);
        sb.AppendLine();

        foreach (var action in normalized)
        {
            var actionId = ToActionIdName(action.Uses);
            if (string.IsNullOrWhiteSpace(action.RunsUsing))
            {
                sb.AppendLine($"                ActionId.{actionId} => default,");
            }
            else
            {
                sb.AppendLine($"                ActionId.{actionId} => \"{action.RunsUsing}\"u8,");
            }
        }

        sb.Append(
            """
                            _ => default,
                        };
                    }

                    internal int GetMaxDeprecatedMajorVersion()
                    {
                        return Id switch
                        {
            """);
        sb.AppendLine();

        foreach (var action in normalized)
        {
            var actionId = ToActionIdName(action.Uses);
            sb.AppendLine($"                ActionId.{actionId} => {action.MaxDeprecatedMajorVersion},");
        }

        sb.Append(
            """
                            _ => 0,
                        };
                    }

                    internal (string Scope, string Access)[] GetRequiredPermissions()
                    {
                        return Id switch
                        {
            """);
        sb.AppendLine();

        foreach (var action in normalized)
        {
            var actionId = ToActionIdName(action.Uses);
            if (action.RequiredPermissions.Count == 0)
            {
                sb.AppendLine($"                ActionId.{actionId} => [],");
            }
            else
            {
                var items = string.Join(", ", action.RequiredPermissions.Select(static p =>
                {
                    var scope = p.Scope.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    var access = p.Access.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    return $"(\"{scope}\", \"{access}\")";
                }));
                staticFields.AppendLine($"        private static readonly (string Scope, string Access)[] RequiredPermissions{actionId} = [{items}];");
                sb.AppendLine($"                ActionId.{actionId} => RequiredPermissions{actionId},");
            }
        }

        sb.AppendLine(
            """
                            _ => [],
                        };
                    }
            """);

        if (staticFields.Length > 0)
        {
            sb.AppendLine();
            sb.Append(staticFields);
        }

        sb.Append(
            """
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

    private static void AppendGeneratedHeader(StringBuilder sb, string command)
    {
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// This file is generated by Seiton.Update. Do not edit manually.");
        sb.AppendLine($"// Regenerate: dotnet run --project src/Seiton.Update -- {command}");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();
    }

    private static string ToActionIdName(string uses)
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

    private static string EscapeCSharpString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
