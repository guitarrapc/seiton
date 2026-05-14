using System.Reflection;
using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class LocalReusableWorkflowOutputResolverUnitTests
{
    [Test]
    public async Task ResolveOutputNames_NormalizeKeyFails_FallsBackToRawPathCache()
    {
        var resolver = new LocalReusableWorkflowOutputResolver("/tmp/repo/.github/workflows/caller.yml");
        var rawCacheKey = "./\0.yml";
        var cached = new[] { "cached_output" };

        var cacheField = typeof(LocalReusableWorkflowOutputResolver).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic);
        await Assert.That(cacheField).IsNotNull();

        var cache = cacheField!.GetValue(resolver) as Dictionary<string, string[]?>;
        await Assert.That(cache).IsNotNull();
        cache![rawCacheKey] = cached;

        var resolved = resolver.ResolveOutputNames("./\0.yml"u8);

        await Assert.That(ReferenceEquals(resolved, cached)).IsTrue();
    }
}
