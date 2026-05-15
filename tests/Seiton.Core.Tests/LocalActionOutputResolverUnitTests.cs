using System.Reflection;
using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class LocalActionOutputResolverUnitTests
{
    [Test]
    public async Task ResolveOutputNames_SlashNormalizedGithubActionPath_UsesCachedValue()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "caller.yml");
        var resolver = new LocalActionOutputResolver(workflowPath);
        var cached = new[] { "cached_output" };

        var cacheField = typeof(LocalActionOutputResolver).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic);
        await Assert.That(cacheField).IsNotNull();

        var cache = cacheField!.GetValue(resolver) as Dictionary<string, string[]?>;
        await Assert.That(cache).IsNotNull();

        var normalizedKey = ActionRefHelpers.NormalizePath(Path.GetFullPath(Path.Combine(repositoryRoot, ".github", "actions", "sample")));
        cache![normalizedKey] = cached;

        var resolved = resolver.ResolveOutputNames("./.github/actions/sample"u8);

        await Assert.That(ReferenceEquals(resolved, cached)).IsTrue();
    }
}
