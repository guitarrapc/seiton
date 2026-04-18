using Seiton.Core.Linting;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Core.Tests;

public sealed class PinRemediationEngineTests
{
    [Test]
    public async Task RemediateAsync_PassThrough_WhenAllowNetworkFalse_AndResolversNull()
    {
        var source = Encoding.UTF8.GetBytes("steps:\n  - uses: actions/checkout@v4\n");
        var diagnostics = new[]
        {
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "action uses 'actions/checkout@v4' is not pinned to a full-length commit SHA",
                new TextRange(0, source.Length, 1, 1, 2, 30),
                RuleId: "unpinned-uses"),
        };

        var engine = new PinRemediationEngine(null, null, new FixPinningConfig(), new FixImagesConfig(), new NetworkConfig());

        var result = await engine.RemediateAsync(diagnostics, source);

        await Assert.That(result.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(result.Diagnostics[0]).IsEqualTo(diagnostics[0]);
        await Assert.That(result.ResolvedCount).IsEqualTo(0);
        await Assert.That(result.SkippedCount).IsEqualTo(0);
        await Assert.That(result.FailedCount).IsEqualTo(0);
    }

    [Test]
    public async Task RemediateAsync_ResolveSkipFail_AreCountedCorrectly_WhenFailOpenTrue()
    {
        var yaml = "steps:\n  - uses: actions/checkout@v4\n  - uses: docker://ghcr.io/astral-sh/uv:latest\n";
        var source = Encoding.UTF8.GetBytes(yaml);
        var diagnostics = new[]
        {
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "action uses 'actions/checkout@v4' is not pinned to a full-length commit SHA",
                new TextRange(0, source.Length, 1, 1, 2, 30),
                RuleId: "unpinned-uses"),
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "docker action uses 'docker://ghcr.io/astral-sh/uv:latest' is not pinned by digest (expected @sha256:<64-hex>)",
                new TextRange(0, source.Length, 1, 1, 3, 60),
                RuleId: "unpinned-image"),
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "action uses 'actions/setup-go@v5' is not pinned to a full-length commit SHA",
                new TextRange(0, source.Length, 1, 1, 3, 60),
                RuleId: "unpinned-uses"),
        };

        var actionResolver = new DelegateActionShaResolver((owner, repo, refStr, _) =>
        {
            if (repo == "checkout")
            {
                return Task.FromResult<(string?, string?)>(("0123456789abcdef0123456789abcdef01234567", "v4"));
            }
            throw new InvalidOperationException("resolver failure");
        });
        var imageResolver = new DelegateImageDigestResolver((_, _) =>
            Task.FromResult<string?>(null));

        var engine = new PinRemediationEngine(
            actionResolver,
            imageResolver,
            new FixPinningConfig { EnableNetwork = true },
            new FixImagesConfig { EnableNetwork = true },
            new NetworkConfig());

        var result = await engine.RemediateAsync(diagnostics, source);

        await Assert.That(result.ResolvedCount).IsEqualTo(1);
        await Assert.That(result.SkippedCount).IsEqualTo(1);
        await Assert.That(result.FailedCount).IsEqualTo(1);
        await Assert.That(result.Diagnostics[0].Fix.HasValue).IsEqualTo(true);
        await Assert.That(result.Diagnostics[1].Fix.HasValue).IsEqualTo(false);
        await Assert.That(result.Diagnostics[2].Fix.HasValue).IsEqualTo(false);
    }

    [Test]
    public async Task RemediateAsync_Throws_WhenResolverFails_AndFailOpenFalse()
    {
        var source = Encoding.UTF8.GetBytes("steps:\n  - uses: actions/setup-go@v5\n");
        var diagnostics = new[]
        {
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "action uses 'actions/setup-go@v5' is not pinned to a full-length commit SHA",
                new TextRange(0, source.Length, 1, 1, 2, 32),
                RuleId: "unpinned-uses"),
        };

        var actionResolver = new DelegateActionShaResolver((_, _, _, _) =>
            throw new InvalidOperationException("boom"));

        var engine = new PinRemediationEngine(
            actionResolver,
            null,
            new FixPinningConfig { EnableNetwork = true },
            new FixImagesConfig { EnableNetwork = true },
            new NetworkConfig { OnError = NetworkErrorMode.Fail });

        await Assert.That(async () => await engine.RemediateAsync(diagnostics, source))
            .Throws<InvalidOperationException>();
    }

    private sealed class DelegateActionShaResolver(
        Func<string, string, string, CancellationToken, Task<(string?, string?)>> impl) : IActionShaResolver
    {
        public Task<(string? Sha, string? TagComment)> ResolveAsync(string owner, string repo, string refStr, CancellationToken cancellationToken = default)
            => impl(owner, repo, refStr, cancellationToken);
    }

    private sealed class DelegateImageDigestResolver(
        Func<string, CancellationToken, Task<string?>> impl) : IImageDigestResolver
    {
        public Task<string?> ResolveAsync(string imageRef, CancellationToken cancellationToken = default)
            => impl(imageRef, cancellationToken);
    }
}
