using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class DenyJobContainerLatestImageRule : RuleBase
{
    public override string Id => "deny_job_container_latest_image";

    public override string Name => "Deny Job Container Latest Image Rule";

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        var imageNode = job.Container?.Image;
        if (imageNode is null || imageNode.Expression is not null)
        {
            return;
        }

        var image = imageNode.Value.AsSpan(Config.Utf8Yaml);
        if (image.IsEmpty || IsSha256DigestPinned(image))
        {
            return;
        }

        if (HasExplicitLatestTag(image))
        {
            var imageText = Decode(imageNode.Value);
            AddJobError(
                job,
                $"job.container.image '{imageText}' must not use mutable ':latest'; pin to immutable digest (@sha256:<64-hex>)",
                imageNode.Range);
            return;
        }

        if (HasImplicitLatest(image))
        {
            var imageText = Decode(imageNode.Value);
            AddJobError(
                job,
                $"job.container.image '{imageText}' has implicit latest tag; pin to immutable digest (@sha256:<64-hex>)",
                imageNode.Range);
        }
    }

    static bool HasExplicitLatestTag(ReadOnlySpan<byte> image)
    {
        var at = image.LastIndexOf((byte)'@');
        var beforeDigest = at >= 0 ? image[..at] : image;

        var lastSlash = beforeDigest.LastIndexOf((byte)'/');
        var lastColon = beforeDigest.LastIndexOf((byte)':');
        if (lastColon < 0 || lastColon < lastSlash)
        {
            return false;
        }

        var tag = beforeDigest[(lastColon + 1)..];
        return EqualsAsciiIgnoreCase(tag, "latest"u8);
    }

    static bool HasImplicitLatest(ReadOnlySpan<byte> image)
    {
        var at = image.LastIndexOf((byte)'@');
        if (at >= 0)
        {
            return false;
        }

        var lastSlash = image.LastIndexOf((byte)'/');
        var lastColon = image.LastIndexOf((byte)':');
        return lastColon < 0 || lastColon < lastSlash;
    }

    static bool IsSha256DigestPinned(ReadOnlySpan<byte> image)
    {
        var at = image.LastIndexOf((byte)'@');
        if (at < 0 || at + 1 >= image.Length)
        {
            return false;
        }

        var digest = image[(at + 1)..];
        if (!digest.StartsWith("sha256:"u8))
        {
            return false;
        }

        var hash = digest["sha256:"u8.Length..];
        if (hash.Length != 64)
        {
            return false;
        }

        for (var i = 0; i < hash.Length; i++)
        {
            var b = hash[i];
            var isDigit = b is >= (byte)'0' and <= (byte)'9';
            var isLowerHex = b is >= (byte)'a' and <= (byte)'f';
            var isUpperHex = b is >= (byte)'A' and <= (byte)'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
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
