using Seiton.Core.Generated;

namespace Seiton.Core.Tests;

public sealed class PopularActionsGeneratedTests
{
    [Test]
    public async Task TryGet_KnownActionReference_ReturnsSpec()
    {
        var found = PopularActions.TryGet("actions/checkout@v4"u8, out var spec);

        await Assert.That(found).IsTrue();
        await Assert.That(spec.IsInputAllowed("fetch-depth"u8)).IsTrue();
        await Assert.That(spec.IsInputAllowed("FETCH-DEPTH"u8)).IsTrue();
        await Assert.That(spec.IsInputAllowed("fetch-depht"u8)).IsFalse();
    }

    [Test]
    public async Task TryGet_UnknownOrLocalActionReference_ReturnsFalse()
    {
        var unknownFound = PopularActions.TryGet("octocat/unknown@v1"u8, out _);
        var localFound = PopularActions.TryGet("./.github/actions/test"u8, out _);
        var dockerFound = PopularActions.TryGet("docker://alpine:3.20"u8, out _);

        await Assert.That(unknownFound).IsFalse();
        await Assert.That(localFound).IsFalse();
        await Assert.That(dockerFound).IsFalse();
    }
}
