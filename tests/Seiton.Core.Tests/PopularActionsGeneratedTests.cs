using Seiton.Core.Generated;
using Seiton.Core.Linting.Rules;

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

    [Test]
    public async Task GetRunsUsing_KnownAction_ReturnsNonEmpty()
    {
        PopularActions.TryGet("actions/checkout@v4"u8, out var spec);
        var runsUsing = spec.GetRunsUsing();
        var isEmpty = runsUsing.IsEmpty;
        var isNode24 = runsUsing.SequenceEqual("node24"u8);

        await Assert.That(isEmpty).IsFalse();
        await Assert.That(isNode24).IsTrue();
    }

    [Test]
    public async Task GetRunsUsing_AllCatalogEntries_HaveNonEmptyRunsUsing()
    {
        // Every popular action in the catalog should have runs.using metadata
        var actions = new[]
        {
            "actions/cache@v5"u8.ToArray(),
            "actions/checkout@v7"u8.ToArray(),
            "actions/download-artifact@v8"u8.ToArray(),
            "actions/setup-dotnet@v5"u8.ToArray(),
            "actions/setup-go@v6"u8.ToArray(),
            "actions/setup-node@v6"u8.ToArray(),
            "actions/upload-artifact@v7"u8.ToArray(),
            "docker/login-action@v4"u8.ToArray(),
        };

        foreach (var actionRef in actions)
        {
            var found = PopularActions.TryGet(actionRef, out var spec);
            await Assert.That(found).IsTrue();
            await Assert.That(spec.GetRunsUsing().IsEmpty).IsFalse();
        }
    }

    [Test]
    public async Task GetOutputNames_ActionsCache_ContainsCacheHit()
    {
        PopularActions.TryGet("actions/cache@v4"u8, out var spec);
        var outputs = spec.GetOutputNames();

        await Assert.That(outputs.Length).IsGreaterThan(0);

        var hasCacheHit = false;
        for (var i = 0; i < outputs.Length; i++)
        {
            if ("cache-hit"u8.SequenceEqual(outputs[i]))
            {
                hasCacheHit = true;
                break;
            }
        }

        await Assert.That(hasCacheHit).IsTrue();
    }

    [Test]
    public async Task GetOutputNames_ActionsCheckout_ReturnsOutputs()
    {
        PopularActions.TryGet("actions/checkout@v4"u8, out var spec);
        var outputs = spec.GetOutputNames();

        // checkout has "ref" and "commit" as outputs
        await Assert.That(outputs.Length).IsGreaterThan(0);
    }
}

public sealed class OutdatedActionRunnerRuleLogicTests
{
    [Test]
    public async Task IsDeprecated_Node12_ReturnsTrue()
    {
        var result = OutdatedActionRunnerRule.IsDeprecated("node12"u8);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsDeprecated_Node16_ReturnsTrue()
    {
        var result = OutdatedActionRunnerRule.IsDeprecated("node16"u8);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsDeprecated_Node20_ReturnsFalse()
    {
        var result = OutdatedActionRunnerRule.IsDeprecated("node20"u8);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsDeprecated_Composite_ReturnsFalse()
    {
        var result = OutdatedActionRunnerRule.IsDeprecated("composite"u8);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsDeprecated_Empty_ReturnsFalse()
    {
        var result = OutdatedActionRunnerRule.IsDeprecated(ReadOnlySpan<byte>.Empty);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsDeprecated_Docker_ReturnsFalse()
    {
        var result = OutdatedActionRunnerRule.IsDeprecated("docker"u8);
        await Assert.That(result).IsFalse();
    }
}
