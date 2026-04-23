using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.OnlineAudit;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed class OnlineAuditEngineTests
{
    private static LintConfig EnableAllOnlineRules() => new()
    {
        Rules = new Dictionary<string, RuleConfig>(StringComparer.Ordinal)
        {
            [KnownVulnerableActionsRule.RuleId] = new() { Enabled = true },
            [ImpostorCommitRule.RuleId] = new() { Enabled = true },
            [RefConfusionRule.RuleId] = new() { Enabled = true },
            [StaleActionRefsRule.RuleId] = new() { Enabled = true },
        },
    };

    [Test]
    public async Task AuditAsync_PassThrough_WhenProvidersReturnNoData()
    {
        var engine = new LintEngine();
        var source = Encoding.UTF8.GetBytes(
            """
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
            """);
        var lintResult = engine.Check(source, "workflow.yml", EnableAllOnlineRules());

        var auditEngine = new OnlineAuditEngine(
            new DelegateActionAdvisoryProvider((_, _, _, _) => Task.FromResult<ActionAdvisory?>(null)),
            new DelegateActionRefResolver((_, _, _, _) => Task.FromResult(new ActionRefResolution())),
            new NetworkConfig());

        var result = await auditEngine.AuditAsync(lintResult, engine.ActiveOnlineRules);

        await Assert.That(result.Diagnostics.Length).IsEqualTo(lintResult.Diagnostics.Length);
        await Assert.That(result.AddedCount).IsEqualTo(0);
        await Assert.That(result.SkippedCount).IsEqualTo(0);
        await Assert.That(result.FailedCount).IsEqualTo(0);
    }

    [Test]
    public async Task AuditAsync_ReturnsPassThrough_WhenOnlineRulesNotEnabled()
    {
        var engine = new LintEngine();
        var source = Encoding.UTF8.GetBytes(
            """
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
            """);
        // No config enabling online rules — they're opt-in
        var lintResult = engine.Check(source, "workflow.yml");

        var auditEngine = new OnlineAuditEngine(
            new DelegateActionAdvisoryProvider((_, _, _, _) => Task.FromResult<ActionAdvisory?>(new ActionAdvisory("GHSA-test", "desc"))),
            new DelegateActionRefResolver((_, _, _, _) => Task.FromResult(new ActionRefResolution())),
            new NetworkConfig());

        var result = await auditEngine.AuditAsync(lintResult, engine.ActiveOnlineRules);

        await Assert.That(engine.ActiveOnlineRules.Count).IsEqualTo(0);
        await Assert.That(result.AddedCount).IsEqualTo(0);
    }

    [Test]
    public async Task AuditAsync_AddsExpectedDiagnostics_ForWorkflowCallAndStepUses()
    {
        var engine = new LintEngine();
        var source = Encoding.UTF8.GetBytes(
            """
            jobs:
              call-reusable:
                uses: octo-org/reusable/.github/workflows/deploy.yml@release
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
                  - uses: actions/setup-go@main
            """);
        var lintResult = engine.Check(source, "workflow.yml", EnableAllOnlineRules());

        var auditEngine = new OnlineAuditEngine(
            new DelegateActionAdvisoryProvider((owner, repo, reference, _) =>
            {
                if (owner == "actions" && repo == "setup-go" && reference == "main")
                {
                    return Task.FromResult<ActionAdvisory?>(new ActionAdvisory("GHSA-test", "known compromise"));
                }

                return Task.FromResult<ActionAdvisory?>(null);
            }),
            new DelegateActionRefResolver((owner, repo, reference, _) =>
            {
                if (owner == "actions" && repo == "checkout")
                {
                    return Task.FromResult(new ActionRefResolution(
                        CommitExists: true,
                        HasBranchReference: false,
                        HasTagReference: false,
                        IsTaggedCommit: false));
                }

                if (owner == "actions" && repo == "setup-go")
                {
                    return Task.FromResult(new ActionRefResolution(
                        CommitExists: false,
                        HasBranchReference: true,
                        HasTagReference: true,
                        IsTaggedCommit: false));
                }

                if (owner == "octo-org" && repo == "reusable")
                {
                    return Task.FromResult(new ActionRefResolution(
                        CommitExists: false,
                        HasBranchReference: true,
                        HasTagReference: false,
                        IsTaggedCommit: false));
                }

                return Task.FromResult(new ActionRefResolution());
            }),
            new NetworkConfig());

        var result = await auditEngine.AuditAsync(lintResult, engine.ActiveOnlineRules);
        var added = result.Diagnostics.Skip(lintResult.Diagnostics.Length).ToArray();

        await Assert.That(result.AddedCount).IsEqualTo(3);
        await Assert.That(added.Any(x => x.RuleId == "known-vulnerable-actions" && x.Message.Contains("actions/setup-go@main", StringComparison.Ordinal))).IsTrue();
        await Assert.That(added.Any(x => x.RuleId == "ref-confusion" && x.Message.Contains("actions/setup-go@main", StringComparison.Ordinal))).IsTrue();
        await Assert.That(added.Any(x => x.RuleId == "stale-action-refs" && x.Message.Contains("actions/checkout@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task AuditAsync_AddsImpostorCommit_WhenShaMissing()
    {
        var engine = new LintEngine();
        var source = Encoding.UTF8.GetBytes(
            """
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
            """);
        var lintResult = engine.Check(source, "workflow.yml", EnableAllOnlineRules());

        var auditEngine = new OnlineAuditEngine(
            null,
            new DelegateActionRefResolver((_, _, _, _) => Task.FromResult(new ActionRefResolution(
                CommitExists: false,
                HasBranchReference: false,
                HasTagReference: false,
                IsTaggedCommit: false))),
            new NetworkConfig());

        var result = await auditEngine.AuditAsync(lintResult, engine.ActiveOnlineRules);

        await Assert.That(result.AddedCount).IsEqualTo(1);
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "impostor-commit")).IsTrue();
    }

    [Test]
    public async Task AuditAsync_ContinuesWhenFailOpenTrue_AndCountsFailures()
    {
        var engine = new LintEngine();
        var source = Encoding.UTF8.GetBytes(
            """
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
            """);
        var lintResult = engine.Check(source, "workflow.yml", EnableAllOnlineRules());

        var auditEngine = new OnlineAuditEngine(
            null,
            new DelegateActionRefResolver((_, _, _, _) => throw new InvalidOperationException("boom")),
            new NetworkConfig());

        var result = await auditEngine.AuditAsync(lintResult, engine.ActiveOnlineRules);

        await Assert.That(result.AddedCount).IsEqualTo(0);
        await Assert.That(result.FailedCount).IsEqualTo(1);
    }

    [Test]
    public async Task AuditAsync_ThrowsWhenFailOpenFalse()
    {
        var engine = new LintEngine();
        var source = Encoding.UTF8.GetBytes(
            """
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
            """);
        var lintResult = engine.Check(source, "workflow.yml", EnableAllOnlineRules());

        var auditEngine = new OnlineAuditEngine(
            null,
            new DelegateActionRefResolver((_, _, _, _) => throw new InvalidOperationException("boom")),
            new NetworkConfig { OnError = NetworkErrorMode.Fail });

        await Assert.That(async () => await auditEngine.AuditAsync(lintResult, engine.ActiveOnlineRules))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AuditAsync_ProcessesAllActions_WhenNoIgnoreConfig()
    {
        var engine = new LintEngine();
        var source = Encoding.UTF8.GetBytes(
            """
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
            """);
        var lintResult = engine.Check(source, "workflow.yml", EnableAllOnlineRules());

        var auditEngine = new OnlineAuditEngine(
            new DelegateActionAdvisoryProvider((_, _, _, _) => Task.FromResult<ActionAdvisory?>(new ActionAdvisory("GHSA-test", "desc"))),
            new DelegateActionRefResolver((_, _, _, _) => Task.FromResult(new ActionRefResolution(
                CommitExists: false,
                HasBranchReference: true,
                HasTagReference: true,
                IsTaggedCommit: false))),
            new NetworkConfig());

        var result = await auditEngine.AuditAsync(lintResult, engine.ActiveOnlineRules);

        await Assert.That(result.AddedCount).IsGreaterThanOrEqualTo(0);
        await Assert.That(result.SkippedCount).IsEqualTo(0);
    }

    [Test]
    public async Task ActiveOnlineRules_ContainsAllFourRules_WhenEnabled()
    {
        var engine = new LintEngine();
        var source = Encoding.UTF8.GetBytes(
            """
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
            """);
        engine.Check(source, "workflow.yml", EnableAllOnlineRules());

        await Assert.That(engine.ActiveOnlineRules.Count).IsEqualTo(4);
        var ruleIds = engine.ActiveOnlineRules.Select(r => r.Id).OrderBy(id => id).ToArray();
        await Assert.That(ruleIds).IsEquivalentTo(new[]
        {
            ImpostorCommitRule.RuleId,
            KnownVulnerableActionsRule.RuleId,
            RefConfusionRule.RuleId,
            StaleActionRefsRule.RuleId,
        });
    }

    [Test]
    public async Task OnlineRules_CollectTargets_DuringVisitorTraversal()
    {
        var engine = new LintEngine();
        var source = Encoding.UTF8.GetBytes(
            """
            jobs:
              call-reusable:
                uses: org/repo/.github/workflows/ci.yml@main
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - uses: ./local-action
                  - uses: docker://alpine:3.18
            """);
        engine.Check(source, "workflow.yml", EnableAllOnlineRules());

        // Each online rule should have collected 3 remote targets (excluding local and docker)
        for (var i = 0; i < engine.ActiveOnlineRules.Count; i++)
        {
            var rule = engine.ActiveOnlineRules[i];
            // org/repo (workflow call) + actions/checkout = 2 remote targets
            await Assert.That(rule.CollectedTargets.Count).IsEqualTo(2);
        }
    }

    private sealed class DelegateActionAdvisoryProvider(
        Func<string, string, string, CancellationToken, Task<ActionAdvisory?>> impl) : IActionAdvisoryProvider
    {
        public Task<ActionAdvisory?> GetAdvisoryAsync(string owner, string repo, string reference, CancellationToken cancellationToken = default)
            => impl(owner, repo, reference, cancellationToken);
    }

    private sealed class DelegateActionRefResolver(
        Func<string, string, string, CancellationToken, Task<ActionRefResolution>> impl) : IActionRefResolver
    {
        public Task<ActionRefResolution> ResolveAsync(string owner, string repo, string reference, CancellationToken cancellationToken = default)
            => impl(owner, repo, reference, cancellationToken);
    }
}
