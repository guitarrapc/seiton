using System.Text;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed class RunnerLabelsCSharpGenerator
{
    public string Generate(RunnerLabelsModel model)
    {
        var preview = model.PreviewLabels
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var previewSet = new HashSet<string>(preview, StringComparer.Ordinal);
        var stable = model.StableLabels
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !previewSet.Contains(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-runner-labels");
        sb.AppendLine(
            """
            namespace Seiton.Core.Generated;

            internal static class RunnerLabels
            {
                internal static bool IsKnownHostedLabel(ReadOnlySpan<byte> labelUtf8)
                {
                    return IsStableHostedLabel(labelUtf8)
                        || IsPreviewHostedLabel(labelUtf8);
                }

                internal static bool IsPreviewHostedLabel(ReadOnlySpan<byte> labelUtf8)
                {
            """);

        if (preview.Length == 0)
        {
            sb.AppendLine("        return false;");
        }
        else
        {
            for (var i = 0; i < preview.Length; i++)
            {
                var op = i == 0 ? "return " : "    || ";
                var suffix = i == preview.Length - 1 ? ";" : string.Empty;
                sb.AppendLine($"        {op}EqualsAsciiIgnoreCase(labelUtf8, \"{preview[i]}\"u8){suffix}");
            }
        }

        sb.Append(
            """
                }

                internal static bool IsStableHostedLabel(ReadOnlySpan<byte> labelUtf8)
                {
            """);
        sb.AppendLine();

        if (stable.Length == 0)
        {
            sb.AppendLine("        return false;");
        }
        else
        {
            for (var i = 0; i < stable.Length; i++)
            {
                var op = i == 0 ? "return " : "    || ";
                var suffix = i == stable.Length - 1 ? ";" : string.Empty;
                sb.AppendLine($"        {op}EqualsAsciiIgnoreCase(labelUtf8, \"{stable[i]}\"u8){suffix}");
            }
        }

        sb.Append(
            """
                }

                internal static bool IsSelfHostedLabel(ReadOnlySpan<byte> labelUtf8)
                {
                    return EqualsAsciiIgnoreCase(labelUtf8, "self-hosted"u8);
                }

                static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
                {
                    if (left.Length != right.Length)
                    {
                        return false;
                    }

                    for (var i = 0; i < left.Length; i++)
                    {
                        var l = left[i];
                        var r = right[i];
                        if (l == r)
                        {
                            continue;
                        }

                        if (l is >= (byte)'A' and <= (byte)'Z')
                        {
                            l = (byte)(l + 32);
                        }

                        if (r is >= (byte)'A' and <= (byte)'Z')
                        {
                            r = (byte)(r + 32);
                        }

                        if (l != r)
                        {
                            return false;
                        }
                    }

                    return true;
                }
            }
            """);

        return TextNormalization.NormalizeToLf(sb.ToString());
    }
}
