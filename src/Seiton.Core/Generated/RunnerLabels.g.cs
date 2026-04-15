namespace Seiton.Core.Generated;

internal static class RunnerLabels
{
    internal static bool IsKnownHostedLabel(ReadOnlySpan<byte> labelUtf8)
    {
        return EqualsAsciiIgnoreCase(labelUtf8, "ubuntu-latest"u8)
            || EqualsAsciiIgnoreCase(labelUtf8, "ubuntu-24.04"u8)
            || EqualsAsciiIgnoreCase(labelUtf8, "ubuntu-22.04"u8)
            || EqualsAsciiIgnoreCase(labelUtf8, "ubuntu-20.04"u8)
            || EqualsAsciiIgnoreCase(labelUtf8, "windows-latest"u8)
            || EqualsAsciiIgnoreCase(labelUtf8, "windows-2025"u8)
            || EqualsAsciiIgnoreCase(labelUtf8, "windows-2022"u8)
            || EqualsAsciiIgnoreCase(labelUtf8, "windows-2019"u8)
            || EqualsAsciiIgnoreCase(labelUtf8, "macos-latest"u8)
            || EqualsAsciiIgnoreCase(labelUtf8, "macos-15"u8)
            || EqualsAsciiIgnoreCase(labelUtf8, "macos-14"u8)
            || EqualsAsciiIgnoreCase(labelUtf8, "macos-13"u8)
            || EqualsAsciiIgnoreCase(labelUtf8, "macos-12"u8);
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
