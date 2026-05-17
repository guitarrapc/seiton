using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class RuleCatalogDescriptorTests
{
    [Test]
    public async Task GetAllRuleDescriptors_ReturnsAllRegisteredRules()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();

        // Total rules: 54 default + 4 online + 2 new expression rules = 60
        // (Syntax is not in the catalog)
        await Assert.That(descriptors.Count).IsEqualTo(60);
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
    public async Task GetAllRuleDescriptors_DocumentKindSupport()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var actionShellIsRequired = descriptors.First(d => d.Id == "action-shell-is-required");

        // action-shell-is-required applies to ActionMetadata
        await Assert.That(actionShellIsRequired.SupportsAction).IsTrue();
    }

    [Test]
    public async Task GetAllRuleDescriptors_UnpinnedToolsSupportsActionMetadata()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var unpinnedTools = descriptors.First(d => d.Id == "unpinned-tools");

        await Assert.That(unpinnedTools.SupportsWorkflow).IsTrue();
        await Assert.That(unpinnedTools.SupportsAction).IsTrue();
    }

    [Test]
    public async Task GetAllRuleDescriptors_JobStructureSupportsWorkflow()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var jobStructure = descriptors.First(d => d.Id == "job-structure");

        await Assert.That(jobStructure.SupportsWorkflow).IsTrue();
    }

    [Test]
    public async Task GetAllRuleDescriptors_EachHasDefaultSeverity()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var validSeverities = new[] { "error", "warning", "mixed" };

        foreach (var d in descriptors)
        {
            await Assert.That(validSeverities).Contains(d.DefaultSeverity);
        }
    }

    [Test]
    public async Task GetAllRuleDescriptors_JobStructureDefaultSeverityIsError()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var jobStructure = descriptors.First(d => d.Id == "job-structure");

        await Assert.That(jobStructure.DefaultSeverity).IsEqualTo("error");
    }

    [Test]
    public async Task GetAllRuleDescriptors_UnpinnedUsesDefaultSeverityIsMixed()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var unpinnedUses = descriptors.First(d => d.Id == "unpinned-uses");

        await Assert.That(unpinnedUses.DefaultSeverity).IsEqualTo("mixed");
    }

    [Test]
    public async Task GetAllRuleDescriptors_PopularActionInputsDefaultSeverityIsWarning()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var popularActionInputs = descriptors.First(d => d.Id == "popular-action-inputs");

        await Assert.That(popularActionInputs.DefaultSeverity).IsEqualTo("warning");
    }

    [Test]
    public async Task GetAllRuleDescriptors_TemplateInjectionSupportsAutoFix()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var templateInjection = descriptors.First(d => d.Id == "template-injection");

        await Assert.That(templateInjection.SupportsAutoFix).IsTrue();
    }

    [Test]
    public async Task GetAllRuleDescriptors_JobStructureDoesNotSupportAutoFix()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var jobStructure = descriptors.First(d => d.Id == "job-structure");

        await Assert.That(jobStructure.SupportsAutoFix).IsFalse();
    }

    [Test]
    public async Task GetAllRuleDescriptors_UnpinnedUsesSupportsAutoFix()
    {
        var descriptors = RuleCatalog.GetAllRuleDescriptors();
        var unpinnedUses = descriptors.First(d => d.Id == "unpinned-uses");

        await Assert.That(unpinnedUses.SupportsAutoFix).IsTrue();
    }
}
