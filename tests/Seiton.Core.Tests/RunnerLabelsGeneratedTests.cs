using Seiton.Core.Generated;

namespace Seiton.Core.Tests;

public sealed class RunnerLabelsGeneratedTests
{
    [Test]
    public async Task IsDeprecatedHostedLabel_CurrentSnapshot_DoesNotFlagKnownLabels()
    {
        await Assert.That(RunnerLabels.IsDeprecatedHostedLabel("ubuntu-22.04"u8)).IsFalse();
        await Assert.That(RunnerLabels.IsDeprecatedHostedLabel("ubuntu-24.04"u8)).IsFalse();
        await Assert.That(RunnerLabels.IsDeprecatedHostedLabel("ubuntu-26.04"u8)).IsFalse();
        await Assert.That(RunnerLabels.IsDeprecatedHostedLabel("self-hosted"u8)).IsFalse();
    }

    [Test]
    public async Task IsKnownHostedLabel_PreviewUbuntu2604Labels_AreRecognized()
    {
        await Assert.That(RunnerLabels.IsKnownHostedLabel("ubuntu-26.04"u8)).IsTrue();
        await Assert.That(RunnerLabels.IsKnownHostedLabel("ubuntu-26.04-arm"u8)).IsTrue();
        await Assert.That(RunnerLabels.IsPreviewHostedLabel("ubuntu-26.04"u8)).IsTrue();
    }

    [Test]
    public async Task IsKnownHostedLabel_UnknownLabel_ReturnsFalse()
    {
        await Assert.That(RunnerLabels.IsKnownHostedLabel("linux-latest"u8)).IsFalse();
        await Assert.That(RunnerLabels.IsKnownHostedLabel("ubuntu-9999"u8)).IsFalse();
    }
}
