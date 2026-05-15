using System.Reflection;
using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class LocalReusableWorkflowOutputResolverUnitTests
{
    [Test]
    public async Task NormalizeCacheKey_ReturnsSlashNormalizedFullPath()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "caller.yml");
        var resolver = new LocalReusableWorkflowOutputResolver(workflowPath);

        var normalizeMethod = typeof(LocalReusableWorkflowOutputResolver).GetMethod("NormalizeCacheKey", BindingFlags.Instance | BindingFlags.NonPublic);
        await Assert.That(normalizeMethod).IsNotNull();

        var normalizedKey = normalizeMethod!.Invoke(resolver, ["./.github/workflows/reusable.yml"]) as string;
        var expected = ActionRefHelpers.NormalizePath(Path.GetFullPath(Path.Combine(repositoryRoot, ".github", "workflows", "reusable.yml")));

        await Assert.That(normalizedKey).IsEqualTo(expected);
        await Assert.That(normalizedKey).IsNotNull();
        await Assert.That(normalizedKey!).DoesNotContain("\\");
    }

    [Test]
    public async Task ResolveOutputNames_NonAsciiWorkflowPath_UsesUtf8DecodedCacheKey()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "caller.yml");
        var resolver = new LocalReusableWorkflowOutputResolver(workflowPath);
        var cached = new[] { "cached_output" };

        var cacheField = typeof(LocalReusableWorkflowOutputResolver).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic);
        await Assert.That(cacheField).IsNotNull();

        var cache = cacheField!.GetValue(resolver) as Dictionary<string, string[]?>;
        await Assert.That(cache).IsNotNull();

        var normalizedKey = ActionRefHelpers.NormalizePath(Path.GetFullPath(Path.Combine(repositoryRoot, ".github", "workflows", "再利用.yml")));
        cache![normalizedKey] = cached;

        var resolved = resolver.ResolveOutputNames("./.github/workflows/再利用.yml"u8);

        await Assert.That(ReferenceEquals(resolved, cached)).IsTrue();
    }

    [Test]
    public async Task ResolveOutputNames_NormalizeKeyFails_FallsBackToRawPathCache()
    {
        var resolver = new LocalReusableWorkflowOutputResolver("/tmp/repo/.github/workflows/caller.yml");
        var rawCacheKey = $".{Path.DirectorySeparatorChar}\0.yml";
        var normalizedRawCacheKey = ActionRefHelpers.NormalizePath(rawCacheKey);
        var cached = new[] { "cached_output" };

        var normalizeMethod = typeof(LocalReusableWorkflowOutputResolver).GetMethod("NormalizeCacheKey", BindingFlags.Instance | BindingFlags.NonPublic);
        await Assert.That(normalizeMethod).IsNotNull();
        var normalizedKey = normalizeMethod!.Invoke(resolver, [rawCacheKey]) as string;
        await Assert.That(normalizedKey).IsNull();

        var cacheField = typeof(LocalReusableWorkflowOutputResolver).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic);
        await Assert.That(cacheField).IsNotNull();

        var cache = cacheField!.GetValue(resolver) as Dictionary<string, string[]?>;
        await Assert.That(cache).IsNotNull();
        cache![normalizedRawCacheKey] = cached;

        var resolved = resolver.ResolveOutputNames("./\0.yml"u8);

        await Assert.That(ReferenceEquals(resolved, cached)).IsTrue();
    }
}
