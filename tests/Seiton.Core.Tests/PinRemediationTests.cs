using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Linting.Rules;
using System.Text;

namespace Seiton.Core.Tests;

public sealed class PinRemediationTests
{
    private const string ActionSha = "0123456789abcdef0123456789abcdef01234567";
    private const string ImageDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Test]
    public async Task RemediateAsync_AttachesFixes_ForUnpinnedUsesAndImage()
    {
        var source = Encoding.UTF8.GetBytes(CreateUnpinnedYaml());
        var lintEngine = CreatePinLintEngine();
        using var lintResult = lintEngine.Check(source, "pin-remediation-success.yml");

        var engine = new PinRemediationEngine(
            new DelegateActionShaResolver((_, _, _, _) => Task.FromResult(ActionShaResolution.Resolved(ActionSha, "v4"))),
            new DelegateImageDigestResolver((_, _) => Task.FromResult<string?>(ImageDigest)),
            new FixPinningConfig { EnableNetwork = true }, new FixImagesConfig { EnableNetwork = true }, new NetworkConfig());

        var remediation = await engine.RemediateAsync(lintResult.Diagnostics, source);

        await Assert.That(remediation.ResolvedCount).IsEqualTo(2);
        await Assert.That(remediation.SkippedCount).IsEqualTo(0);
        await Assert.That(remediation.FailedCount).IsEqualTo(0);
        await Assert.That(remediation.Diagnostics.Any(d => d.RuleId == "unpinned-uses" && d.Fix.HasValue)).IsTrue();
        await Assert.That(remediation.Diagnostics.Any(d => d.RuleId == "unpinned-image" && d.Fix.HasValue)).IsTrue();
    }

    [Test]
    public async Task RemediateAsync_DoesNotResolve_WhenAllowNetworkFalse()
    {
        var source = Encoding.UTF8.GetBytes(CreateUnpinnedYaml());
        var lintEngine = CreatePinLintEngine();
        using var lintResult = lintEngine.Check(source, "pin-remediation-network-off.yml");

        var actionCalls = 0;
        var imageCalls = 0;
        var engine = new PinRemediationEngine(
            new DelegateActionShaResolver((_, _, _, _) =>
            {
                actionCalls++;
                return Task.FromResult(ActionShaResolution.Resolved(ActionSha, "v4"));
            }),
            new DelegateImageDigestResolver((_, _) =>
            {
                imageCalls++;
                return Task.FromResult<string?>(ImageDigest);
            }),
            new FixPinningConfig(), new FixImagesConfig(), new NetworkConfig());

        var remediation = await engine.RemediateAsync(lintResult.Diagnostics, source);

        await Assert.That(remediation.ResolvedCount).IsEqualTo(0);
        await Assert.That(remediation.SkippedCount).IsEqualTo(0);
        await Assert.That(remediation.FailedCount).IsEqualTo(0);
        await Assert.That(actionCalls).IsEqualTo(0);
        await Assert.That(imageCalls).IsEqualTo(0);
        await Assert.That(remediation.Diagnostics.All(d => d.Fix is null)).IsTrue();
    }

    [Test]
    public async Task RemediateAsync_ContinuesWhenFailOpenTrue_AndCountsFailures()
    {
        var source = Encoding.UTF8.GetBytes(CreateUnpinnedYaml());
        var lintEngine = CreatePinLintEngine();
        using var lintResult = lintEngine.Check(source, "pin-remediation-fail-open.yml");

        var engine = new PinRemediationEngine(
            new DelegateActionShaResolver((_, _, _, _) => throw new InvalidOperationException("action resolver failed")),
            new DelegateImageDigestResolver((_, _) => Task.FromResult<string?>(ImageDigest)),
            new FixPinningConfig { EnableNetwork = true }, new FixImagesConfig { EnableNetwork = true }, new NetworkConfig());

        var remediation = await engine.RemediateAsync(lintResult.Diagnostics, source);

        await Assert.That(remediation.ResolvedCount).IsEqualTo(1);
        await Assert.That(remediation.SkippedCount).IsEqualTo(0);
        await Assert.That(remediation.FailedCount).IsEqualTo(1);
        await Assert.That(remediation.Diagnostics.Any(d => d.RuleId == "unpinned-uses" && d.Fix is null)).IsTrue();
        await Assert.That(remediation.Diagnostics.Any(d => d.RuleId == "unpinned-image" && d.Fix.HasValue)).IsTrue();
    }

    [Test]
    public async Task RemediateAsync_ThrowsWhenFailOpenFalse()
    {
        var source = Encoding.UTF8.GetBytes(CreateUnpinnedYaml());
        var lintEngine = CreatePinLintEngine();
        using var lintResult = lintEngine.Check(source, "pin-remediation-fail-closed.yml");

        var engine = new PinRemediationEngine(
            new DelegateActionShaResolver((_, _, _, _) => throw new InvalidOperationException("action resolver failed")),
            new DelegateImageDigestResolver((_, _) => Task.FromResult<string?>(ImageDigest)),
            new FixPinningConfig { EnableNetwork = true }, new FixImagesConfig { EnableNetwork = true }, new NetworkConfig { OnError = NetworkErrorMode.Fail });

        await Assert.That(async () => await engine.RemediateAsync(lintResult.Diagnostics, source))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ApplyAndRelint_ClearsUnpinnedDiagnostics_AfterRemediationFixesApplied()
    {
        var source = Encoding.UTF8.GetBytes(CreateUnpinnedYaml());
        var lintEngine = CreatePinLintEngine();
        using var lintResult = lintEngine.Check(source, "pin-remediation-revalidate.yml");

        var remediationEngine = new PinRemediationEngine(
            new DelegateActionShaResolver((_, _, _, _) => Task.FromResult(ActionShaResolution.Resolved(ActionSha, "v4"))),
            new DelegateImageDigestResolver((_, _) => Task.FromResult<string?>(ImageDigest)),
            new FixPinningConfig { EnableNetwork = true }, new FixImagesConfig { EnableNetwork = true }, new NetworkConfig());

        var remediation = await remediationEngine.RemediateAsync(lintResult.Diagnostics, source);
        using var revalidated = FixEngine.ApplyAndRelint(
            lintEngine,
            source,
            "pin-remediation-revalidate.yml",
            remediation.Diagnostics);

        await Assert.That(revalidated.Before.Diagnostics.Any(d => d.RuleId == "unpinned-uses")).IsTrue();
        await Assert.That(revalidated.Before.Diagnostics.Any(d => d.RuleId == "unpinned-image")).IsTrue();
        await Assert.That(revalidated.After.Diagnostics.Any(d => d.RuleId == "unpinned-uses")).IsFalse();
        await Assert.That(revalidated.After.Diagnostics.Any(d => d.RuleId == "unpinned-image")).IsFalse();
        await Assert.That(revalidated.After.HasFatalError).IsFalse();

        var updatedYaml = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);
        await Assert.That(updatedYaml.Contains($"actions/checkout@{ActionSha} # v4", StringComparison.Ordinal)).IsTrue();
        await Assert.That(updatedYaml.Contains($"docker://ghcr.io/astral-sh/uv:latest@{ImageDigest}", StringComparison.Ordinal)).IsTrue();
    }

    private static LintEngine CreatePinLintEngine()
    {
        return new LintEngine([
            new UnpinnedUsesRule(),
            new UnpinnedImageRule(),
        ]);
    }

    private static string CreateUnpinnedYaml()
    {
        return """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v4
              - uses: docker://ghcr.io/astral-sh/uv:latest
        """;
    }

    private sealed class DelegateActionShaResolver(
        Func<string, string, string, CancellationToken, Task<ActionShaResolution>> impl) : IActionShaResolver
    {
        public Task<ActionShaResolution> ResolveAsync(string owner, string repo, string refStr, CancellationToken cancellationToken = default)
            => impl(owner, repo, refStr, cancellationToken);
    }

    private sealed class DelegateImageDigestResolver(
        Func<string, CancellationToken, Task<string?>> impl) : IImageDigestResolver
    {
        public Task<string?> ResolveAsync(string imageRef, CancellationToken cancellationToken = default)
            => impl(imageRef, cancellationToken);
    }
}
