using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class RuleCatalogDescriptorTests
{
    [Test]
    public async Task GetAllRuleDescriptors_ReturnsAllRegisteredRules()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();

        // Total rules: 52 default + 4 online = 56
        // (Syntax is not in the catalog)
        await Assert.That(descriptors.Count).IsEqualTo(56);
    }

    [Test]
    public async Task GetAllRuleDescriptors_EachHasNonEmptyIdAndName()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();

        foreach (var d in descriptors)
        {
            await Assert.That(d.Id).IsNotNull().And.IsNotEmpty();
            await Assert.That(d.Name).IsNotNull().And.IsNotEmpty();
        }
    }

    [Test]
    public async Task GetAllRuleDescriptors_IdsAreUnique()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var ids = descriptors.Select(d => d.Id).ToList();
        var distinctIds = ids.Distinct().ToList();

        await Assert.That(ids.Count).IsEqualTo(distinctIds.Count);
    }

    [Test]
    public async Task GetAllRuleDescriptors_ContainsKnownOptInRule()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var concurrencyLimits = descriptors.FirstOrDefault(d => d.Id == "concurrency-limits");

        await Assert.That(concurrencyLimits.Id).IsNotNull();
        await Assert.That(concurrencyLimits.IsOptIn).IsTrue();
        await Assert.That(concurrencyLimits.IsOnline).IsFalse();
    }

    [Test]
    public async Task GetAllRuleDescriptors_ContainsKnownOnlineRule()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var knownVuln = descriptors.FirstOrDefault(d => d.Id == "known-vulnerable-actions");

        await Assert.That(knownVuln.Id).IsNotNull();
        await Assert.That(knownVuln.IsOptIn).IsTrue();
        await Assert.That(knownVuln.IsOnline).IsTrue();
    }

    [Test]
    public async Task GetAllRuleDescriptors_NonDisableableRulesMarkedCorrectly()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var denyWriteAll = descriptors.First(d => d.Id == "deny-write-all");
        var denyReadAll = descriptors.First(d => d.Id == "deny-read-all");
        var jobStructure = descriptors.First(d => d.Id == "job-structure");

        await Assert.That(denyWriteAll.IsNonDisableable).IsTrue();
        await Assert.That(denyReadAll.IsNonDisableable).IsTrue();
        await Assert.That(jobStructure.IsNonDisableable).IsFalse();
    }

    [Test]
    public async Task GetAllRuleDescriptors_DocumentKindSupport()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var actionShellIsRequired = descriptors.First(d => d.Id == "action-shell-is-required");

        // action-shell-is-required applies to ActionMetadata
        await Assert.That(actionShellIsRequired.SupportsAction).IsTrue();
    }

    [Test]
    public async Task GetAllRuleDescriptors_JobStructureSupportsWorkflow()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var jobStructure = descriptors.First(d => d.Id == "job-structure");

        await Assert.That(jobStructure.SupportsWorkflow).IsTrue();
    }
}
