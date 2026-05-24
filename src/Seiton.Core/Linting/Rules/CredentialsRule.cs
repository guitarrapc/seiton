using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

using static Seiton.Core.Linting.RuleConfigHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags hardcoded credentials or non-secret references in container registry credentials.</summary>
public sealed class CredentialsRule() : RuleBase(RuleId.Credentials)
{
    private HashSet<string> additionalPublicRegistries = [];

    public override string Name => "Credentials Rule";

    public override void SetConfig(LintConfig config)
    {
        base.SetConfig(config);
        additionalPublicRegistries = config.GetRuleConfig(Id)?.PublicRegistries is { Count: > 0 } registries
            ? BuildNormalizedSet(registries)
            : [];
    }

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        ValidateContainer(job, "job.container", "\"container\" section", job.Container);

        var serviceMap = job.Services?.ServiceMap;
        if (serviceMap is null || serviceMap.Value.Count == 0)
        {
            return;
        }

        foreach (var pair in serviceMap)
        {
            var service = pair.Value;
            var serviceName = Decode(Arena.GetStringSlice(service.Name));
            ValidateContainer(job, $"job.services.{serviceName}", $"\"{serviceName}\" service", service.Container);
        }
    }

    private void ValidateContainer(Job job, string locationName, string credentialLocationName, Container? container)
    {
        if (container is null)
        {
            return;
        }

        if (container.Credentials is not null)
        {
            ValidateHardcodedPassword(job, credentialLocationName, container.Credentials);
            return;
        }

        var imageNode = container.Image;
        if (Arena.GetStringExpression(imageNode).HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var image = Arena.GetStringValue(imageNode);
        if (ExpressionScanHelpers.ContainsExpressionMarker(image))
        {
            return;
        }

        if (!TryGetRegistryHost(image, out var host))
        {
            return;
        }

        if (IsPublicRegistry(host) || IsAdditionalPublicRegistry(host))
        {
            return;
        }

        var imageText = Decode(Arena.GetStringSlice(imageNode));
        var hostText = Encoding.UTF8.GetString(host);
        AddJobWarning(job, $"{locationName} image '{imageText}' uses registry '{hostText}' but credentials are not configured", Arena.GetStringRange(imageNode));
    }

    private void ValidateHardcodedPassword(Job job, string locationName, Credentials credentials)
    {
        var passwordNode = credentials.Password;
        if (!passwordNode.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        // If the entire credentials block is an expression, skip
        if (Arena.GetStringExpression(credentials.Expression).HasValue)
        {
            return;
        }

        // If password is an expression (${{ ... }}), it's likely secrets reference — OK
        if (Arena.GetStringExpression(passwordNode).HasValue)
        {
            return;
        }

        var password = Arena.GetStringValue(passwordNode);
        if (ExpressionScanHelpers.ContainsExpressionMarker(password))
        {
            return;
        }

        AddJobError(job, $"\"password\" section in {locationName} should be specified via secrets. do not put password value directly", Arena.GetStringRange(passwordNode));
    }

    private static bool TryGetRegistryHost(ReadOnlySpan<byte> image, out ReadOnlySpan<byte> host)
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

    private static bool IsPublicRegistry(ReadOnlySpan<byte> host)
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

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
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

    private bool IsAdditionalPublicRegistry(ReadOnlySpan<byte> host)
    {
        if (additionalPublicRegistries.Count == 0)
        {
            return false;
        }

        return additionalPublicRegistries.Contains(NormalizeAsciiLower(host));
    }
}
