using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class PinRemediationContractsTests
{
    [Test]
    public async Task IActionShaResolver_MockImplementationCompilesAndReturnsResolution()
    {
        IActionShaResolver resolver = new FakeActionShaResolver();

        var resolution = await resolver.ResolveAsync("actions", "checkout", "v4");

        await Assert.That(resolution.Sha).IsEqualTo("0123456789abcdef0123456789abcdef01234567");
        await Assert.That(resolution.TagComment).IsEqualTo("v4");
    }

    [Test]
    public async Task IImageDigestResolver_MockImplementationCompilesAndReturnsDigest()
    {
        IImageDigestResolver resolver = new FakeImageDigestResolver();

        var resolution = await resolver.ResolveAsync("node:20.11.1");

        await Assert.That(resolution.Digest).IsEqualTo("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await Assert.That(resolution.SkipReason).IsNull();
    }

    [Test]
    public async Task RemediationResult_HoldsDiagnosticCollectionAndCounters()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "sample", default, RuleId: "unpinned-uses"),
        };
        var result = new RemediationResult(diagnostics, ResolvedCount: 1, SkippedCount: 2, FailedCount: 3);

        await Assert.That(result.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(result.ResolvedCount).IsEqualTo(1);
        await Assert.That(result.SkippedCount).IsEqualTo(2);
        await Assert.That(result.FailedCount).IsEqualTo(3);
    }

    private sealed class FakeActionShaResolver : IActionShaResolver
    {
        public Task<ActionShaResolution> ResolveAsync(string owner, string repo, string refStr, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ActionShaResolution.Resolved("0123456789abcdef0123456789abcdef01234567", refStr));
        }
    }

    private sealed class FakeImageDigestResolver : IImageDigestResolver
    {
        public Task<ImageDigestResolution> ResolveAsync(string imageRef, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ImageDigestResolution.Resolved("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        }
    }
}
