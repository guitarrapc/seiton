namespace Seiton.Tests;

public sealed class ToolVersionResolverTests
{
    [Test]
    public async Task TrimBuildMetadata_WithPlus_StripSuffix()
    {
        var normalized = ToolVersionResolver.TrimBuildMetadata("1.2.3+abc123");

        await Assert.That(normalized).IsEqualTo("1.2.3");
    }

    [Test]
    public async Task ResolveFromAssembly_WithInformationalVersion_ReturnsStableValue()
    {
        var version = ToolVersionResolver.ResolveFromAssembly(typeof(ToolVersionResolverTests).Assembly);

        await Assert.That(string.IsNullOrWhiteSpace(version)).IsEqualTo(false);
        await Assert.That(version).DoesNotContain("+");
    }
}
