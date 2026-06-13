using System.Text;
using System.Text.RegularExpressions;
using Seiton.Update.Model;

namespace Seiton.Update.Generators;

internal sealed partial class RunnerLabelsCSharpGenerator
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

        var deprecated = model.DeprecatedLabels
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var deprecatedListStr = string.Join(", ", deprecated.Select(static l => $"\\\"" + l + "\\\""));

        var sb = new StringBuilder();
        GeneratorHelper.AppendGeneratedHeader(sb, "sync-runner-labels");

        // Categorize labels for diagnostic messages
        var selfHostedPresets = new[] { "self-hosted", "x64", "arm", "arm64", "linux", "macos", "windows" };

        var allHosted = stable.Concat(preview).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var largerLabels = allHosted.Where(IsLargerRunnerLabel).ToArray();
        var standardLabels = allHosted.Where(l => !IsLargerRunnerLabel(l)).ToArray();

        var standardListStr = string.Join(", ", standardLabels.Select(static l => $"\\\"" + l + "\\\""));
        var largerListStr = string.Join(", ", largerLabels.Select(static l => $"\\\"" + l + "\\\""));
        var selfHostedListStr = string.Join(", ", selfHostedPresets.OrderBy(static x => x, StringComparer.Ordinal).Select(static l => $"\\\"" + l + "\\\""));

        sb.AppendLine(
            $$"""
            namespace Seiton.Core.Generated;

            internal static class RunnerLabels
            {
                /// <summary>Comma-separated list of standard hosted runner labels for diagnostic messages.</summary>
                internal const string HostedLabelList = "{{standardListStr}}";

                /// <summary>Comma-separated list of larger runner labels for diagnostic messages.</summary>
                internal const string LargerLabelList = "{{largerListStr}}";

                /// <summary>Comma-separated list of self-hosted preset labels for diagnostic messages.</summary>
                internal const string SelfHostedPresetLabelList = "{{selfHostedListStr}}";

                /// <summary>Comma-separated list of deprecated hosted runner labels for diagnostic messages.</summary>
                internal const string DeprecatedLabelList = "{{deprecatedListStr}}";

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

                /// <summary>
                /// Returns <c>true</c> when the label matches a GitHub self-hosted runner preset label.
                /// Preset labels: self-hosted, linux, macos, windows, x64, arm, arm64.
                /// See: https://docs.github.com/en/actions/hosting-your-own-runners/managing-self-hosted-runners/using-self-hosted-runners-in-a-workflow
                /// </summary>
                internal static bool IsSelfHostedPresetLabel(ReadOnlySpan<byte> labelUtf8)
                {
            """);
        sb.AppendLine();

        for (var i = 0; i < selfHostedPresets.Length; i++)
        {
            var op = i == 0 ? "return " : "    || ";
            var suffix = i == selfHostedPresets.Length - 1 ? ";" : string.Empty;
            sb.AppendLine($"        {op}EqualsAsciiIgnoreCase(labelUtf8, \"{selfHostedPresets[i]}\"u8){suffix}");
        }

        sb.Append(
            """
                }

                /// <summary>Returns <c>true</c> when the label is a curated deprecated hosted runner label.</summary>
                internal static bool IsDeprecatedHostedLabel(ReadOnlySpan<byte> labelUtf8)
                {
            """);
        sb.AppendLine();

        if (deprecated.Length == 0)
        {
            sb.AppendLine("        return false;");
        }
        else
        {
            for (var i = 0; i < deprecated.Length; i++)
            {
                var op = i == 0 ? "return " : "    || ";
                var suffix = i == deprecated.Length - 1 ? ";" : string.Empty;
                sb.AppendLine($"        {op}EqualsAsciiIgnoreCase(labelUtf8, \"{deprecated[i]}\"u8){suffix}");
            }
        }

        sb.Append(
            """
                }
            """);

        sb.Append(
            """


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

    private static bool IsLargerRunnerLabel(string label)
    {
        return label.EndsWith("-large", StringComparison.Ordinal)
            || label.EndsWith("-xlarge", StringComparison.Ordinal)
            || LargerCoresRegex().IsMatch(label);
    }

    [GeneratedRegex(@"-\d+-cores$")]
    private static partial Regex LargerCoresRegex();
}
