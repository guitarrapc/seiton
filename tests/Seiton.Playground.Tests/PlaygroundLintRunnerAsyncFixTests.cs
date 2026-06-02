using Seiton.Core.Linting.PinRemediation;

namespace Seiton.Playground.Tests;

/// <summary>
/// Tests for <see cref="PlaygroundLintRunner.ApplyAllFixesAsync"/> — the network-based
/// pin remediation path in the Playground.
/// </summary>
[NotInParallel("PlaygroundConfig")]
public sealed class PlaygroundLintRunnerAsyncFixTests : IDisposable
{

    public void Dispose()
    {
        // Reset resolver overrides and config after each test to avoid cross-test contamination.
        PlaygroundLintRunner.ActionShaResolverOverride = null;
        PlaygroundLintRunner.ImageDigestResolverOverride = null;
        PlaygroundLintRunner.SetConfig(null);
    }

    [Test]
    public async Task ApplyAllFixesAsync_WithNetworkEnabled_PinsUnpinnedAction()
    {
        // Arrange: YAML with an unpinned action reference
        const string yaml = """
            on: push
            permissions: read-all
            jobs:
              build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                steps:
                  - uses: actions/checkout@v4
                    with:
                      persist-credentials: false
            """;

        // Set config with network enabled for pinning
        PlaygroundLintRunner.SetConfig("""
            fix:
              pinning:
                enable-network: true
            """);

        // Mock resolver returns a known SHA for actions/checkout@v4
        const string fakeSha = "11bd71901bbe5b1630ceea73d27597364c9af683";
        const string fakeComment = "v4";
        PlaygroundLintRunner.ActionShaResolverOverride = new FakeActionShaResolver(fakeSha, fakeComment);

        // Act
        var result = await PlaygroundLintRunner.ApplyAllFixesAsync(yaml, ".github/workflows/ci.yml");

        // Assert: check counts first to understand flow
        await Assert.That(result.ResolvedCount).IsGreaterThanOrEqualTo(1);
        // The output YAML should contain the pinned SHA
        await Assert.That(result.Yaml).Contains(fakeSha);
        await Assert.That(result.Yaml).Contains("# v4");
    }

    [Test]
    public async Task ApplyAllFixesAsync_WithNetworkDisabled_DoesNotPin()
    {
        // Arrange: YAML with an unpinned action reference
        const string yaml = """
            on: push
            permissions: read-all
            jobs:
              build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                steps:
                  - uses: actions/checkout@v4
                    with:
                      persist-credentials: false
            """;

        // Set config with network DISABLED (default)
        PlaygroundLintRunner.SetConfig("""
            fix:
              pinning:
                enable-network: false
            """);

        // Even though resolver is available, network is disabled
        PlaygroundLintRunner.ActionShaResolverOverride = new FakeActionShaResolver("abc123def456", "v4");

        // Act
        var result = await PlaygroundLintRunner.ApplyAllFixesAsync(yaml, ".github/workflows/ci.yml");

        // Assert: YAML should NOT be pinned (no SHA in output)
        await Assert.That(result.Yaml).DoesNotContain("abc123def456");
        await Assert.That(result.ResolvedCount).IsEqualTo(0);
    }

    [Test]
    public async Task ApplyAllFixesAsync_NetworkFailure_GracefulDegradation()
    {
        // Arrange: YAML with unpinned action
        const string yaml = """
            on: push
            permissions: read-all
            jobs:
              build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                steps:
                  - uses: actions/checkout@v4
                    with:
                      persist-credentials: false
            """;

        PlaygroundLintRunner.SetConfig("""
            fix:
              pinning:
                enable-network: true
            """);

        // Resolver that always throws (simulates network failure)
        PlaygroundLintRunner.ActionShaResolverOverride = new FailingActionShaResolver();

        // Act: should not throw
        var result = await PlaygroundLintRunner.ApplyAllFixesAsync(yaml, ".github/workflows/ci.yml");

        // Assert: returns valid YAML, failed count is reported
        await Assert.That(result.Yaml).IsNotNull();
        await Assert.That(result.FailedCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(result.ResolvedCount).IsEqualTo(0);
    }

    [Test]
    public async Task ApplyAllFixesAsync_Cancellation_Respected()
    {
        // Arrange
        const string yaml = """
            on: push
            permissions: read-all
            jobs:
              build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                steps:
                  - uses: actions/checkout@v4
                    with:
                      persist-credentials: false
            """;

        PlaygroundLintRunner.SetConfig("""
            fix:
              pinning:
                enable-network: true
            """);

        // Resolver that blocks until cancellation
        PlaygroundLintRunner.ActionShaResolverOverride = new BlockingActionShaResolver();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert: should throw OperationCanceledException
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await PlaygroundLintRunner.ApplyAllFixesAsync(yaml, ".github/workflows/ci.yml", cts.Token));
    }

    [Test]
    public async Task ApplyAllFixesAsync_OfflineFixesAppliedFirst_ThenNetworkFixes()
    {
        // Arrange: YAML that triggers both offline fixes (deny-write-all) AND network fixes (unpinned-uses)
        const string yaml = """
            on: push
            permissions: write-all
            jobs:
              build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                steps:
                  - uses: actions/checkout@v4
                    with:
                      persist-credentials: false
            """;

        PlaygroundLintRunner.SetConfig("""
            fix:
              pinning:
                enable-network: true
            """);

        const string fakeSha = "11bd71901bbe5b1630ceea73d27597364c9af683";
        PlaygroundLintRunner.ActionShaResolverOverride = new FakeActionShaResolver(fakeSha, "v4");

        // Act
        var result = await PlaygroundLintRunner.ApplyAllFixesAsync(yaml, ".github/workflows/ci.yml");

        // Assert: both offline fix (write-all removed) and network fix (SHA pinned) were applied
        await Assert.That(result.Yaml).DoesNotContain("write-all");
        await Assert.That(result.Yaml).Contains(fakeSha);
    }

    [Test]
    public async Task ApplyAllFixesAsync_MultipleUnpinnedActions_NoOffsetCorruption()
    {
        // Regression test: When offline fixes (e.g. timeout-minutes insertion) shift byte offsets,
        // multiple pin fixes must still apply correctly without corrupting the YAML.
        const string yaml = """
            on:
              push:
                branches: main

            jobs:
              test:
                runs-on: ubuntu-24.04
                permissions:
                  contents: read
                steps:
                  - uses: actions/checkout@v6
                    with:
                      persist-credentials: false
                  - uses: actions/cache@v4
                    with:
                      path: ~/.npm
                      key: ubuntu-node-${{ hashFiles('**/package-lock.json') }}
                  - run: npm install && npm test
            """;

        PlaygroundLintRunner.SetConfig("""
            fix:
              defaults:
                job-timeout-minutes: 15
              pinning:
                enable-network: true
                min-age-days: 14
            """);

        // Resolver returns different SHAs for different actions
        PlaygroundLintRunner.ActionShaResolverOverride = new MultiFakeActionShaResolver(new Dictionary<string, (string sha, string comment)>
        {
            ["actions/checkout"] = ("de0fac2e4500dabe0009e67214ff5f5447ce83dd", "v6.0.2"),
            ["actions/cache"] = ("0057852bfaa89a56745cba8c7296529d2fc39830", "v4.3.0"),
        });

        // Act
        var result = await PlaygroundLintRunner.ApplyAllFixesAsync(yaml, ".github/workflows/ci.yml");

        // Assert: both actions are pinned correctly
        await Assert.That(result.Yaml).Contains("actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2");
        await Assert.That(result.Yaml).Contains("actions/cache@0057852bfaa89a56745cba8c7296529d2fc39830 # v4.3.0");
        // Assert: timeout-minutes was also inserted by offline fix
        await Assert.That(result.Yaml).Contains("timeout-minutes: 15");
        // Assert: no corruption — persist-credentials line is intact
        await Assert.That(result.Yaml).Contains("persist-credentials: false");
        // Assert: resolved count matches
        await Assert.That(result.ResolvedCount).IsEqualTo(2);
    }

    // ─── Test Doubles ───

    private sealed class FakeActionShaResolver(string sha, string tagComment) : IActionShaResolver
    {
        public Task<(string? Sha, string? TagComment)> ResolveAsync(
            string owner, string repo, string refStr, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<(string?, string?)>((sha, tagComment));
        }
    }

    private sealed class FailingActionShaResolver : IActionShaResolver
    {
        public Task<(string? Sha, string? TagComment)> ResolveAsync(
            string owner, string repo, string refStr, CancellationToken cancellationToken = default)
        {
            throw new HttpRequestException("Simulated network failure");
        }
    }

    private sealed class BlockingActionShaResolver : IActionShaResolver
    {
        public async Task<(string? Sha, string? TagComment)> ResolveAsync(
            string owner, string repo, string refStr, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return (null, null);
        }
    }

    private sealed class MultiFakeActionShaResolver(Dictionary<string, (string sha, string comment)> resolutions) : IActionShaResolver
    {
        public Task<(string? Sha, string? TagComment)> ResolveAsync(
            string owner, string repo, string refStr, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"{owner}/{repo}";
            if (resolutions.TryGetValue(key, out var result))
            {
                return Task.FromResult<(string?, string?)>((result.sha, result.comment));
            }
            return Task.FromResult<(string?, string?)>((null, null));
        }
    }
}
