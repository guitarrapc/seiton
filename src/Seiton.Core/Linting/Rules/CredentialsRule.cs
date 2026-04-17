using System.Text;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class CredentialsRule : RuleBase
{
    HashSet<string>? additionalPublicRegistries;

    public override string Id => "credentials";

    public override string Name => "Credentials Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalPublicRegistries = BuildNormalizedSet(config.AdditiveCustomization.AdditionalPublicRegistries);
    }

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        ValidateContainer(job, "job.container", job.Container);

        var serviceMap = job.Services?.ServiceMap;
        if (serviceMap is null || serviceMap.Count == 0)
        {
            return;
        }

        foreach (var pair in serviceMap)
        {
            var service = pair.Value;
            var serviceName = Decode(service.Name.Value);
            ValidateContainer(job, $"job.services.{serviceName}", service.Container);
        }
    }

    void ValidateContainer(Job job, string locationName, Container? container)
    {
        if (container is null)
        {
            return;
        }

        var imageNode = container.Image;
        if (imageNode.Expression is not null || container.Credentials is not null || Config.Utf8Yaml is null)
        {
            return;
        }

        var image = imageNode.Value.AsSpan(Config.Utf8Yaml);
        if (image.IndexOf("${{"u8) >= 0)
        {
            return;
        }

        if (!TryGetRegistryHost(image, out var host) || IsPublicRegistry(host) || IsAdditionalPublicRegistry(host))
        {
            return;
        }

        var imageText = Decode(imageNode.Value);
        var hostText = Encoding.UTF8.GetString(host);
        AddJobWarning(job, $"{locationName} image '{imageText}' uses registry '{hostText}' but credentials are not configured", imageNode.Range);
    }

    static bool TryGetRegistryHost(ReadOnlySpan<byte> image, out ReadOnlySpan<byte> host)
    {
        host = default;

        var slash = image.IndexOf((byte)'/');
        if (slash <= 0)
        {
            return false;
        }

        var first = image[..slash];
        var hasDot = first.IndexOf((byte)'.') >= 0;
        var hasColon = first.IndexOf((byte)':') >= 0;
        if (!hasDot && !hasColon && !AsciiEqualsIgnoreCase(first, "localhost"u8))
        {
            return false;
        }

        host = first;
        return true;
    }

    static bool IsPublicRegistry(ReadOnlySpan<byte> host)
    {
        return AsciiEqualsIgnoreCase(host, "gcr.io"u8)
            || AsciiEqualsIgnoreCase(host, "ghcr.io"u8)
            || AsciiEqualsIgnoreCase(host, "docker.io"u8)
            || AsciiEqualsIgnoreCase(host, "public.ecr.aws"u8)
            || AsciiEqualsIgnoreCase(host, "quay.io"u8)
            || AsciiEqualsIgnoreCase(host, "registry.k8s.io"u8)
            || AsciiEqualsIgnoreCase(host, "mcr.microsoft.com"u8)
            || AsciiEqualsIgnoreCase(host, "cgr.dev"u8)
            || AsciiEqualsIgnoreCase(host, "nvcr.io"u8)
            || AsciiEqualsIgnoreCase(host, "registry.access.redhat.com"u8);
    }

    static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i];
            var r = right[i];
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

    bool IsAdditionalPublicRegistry(ReadOnlySpan<byte> host)
    {
        if (additionalPublicRegistries is null || additionalPublicRegistries.Count == 0)
        {
            return false;
        }

        return additionalPublicRegistries.Contains(NormalizeAsciiLower(host));
    }

    static HashSet<string>? BuildNormalizedSet(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    static string NormalizeAsciiLower(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = (char)value[i];
            if (ch is >= 'A' and <= 'Z')
            {
                ch = (char)(ch + 32);
            }

            buffer[i] = ch;
        }

        return new string(buffer);
    }
}
