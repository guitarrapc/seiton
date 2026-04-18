using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.OnlineAudit;

namespace Seiton.Core.Tests;

public sealed class OnlineAuditEngineTests
{
    [Test]
    public async Task AuditAsync_PassThrough_WhenAllowNetworkFalse()
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
        var lintResult = engine.Check(source, "workflow.yml");

        var auditEngine = new OnlineAuditEngine(
            new DelegateActionAdvisoryProvider((_, _, _, _) => Task.FromResult<ActionAdvisory?>(null)),
            new DelegateActionRefResolver((_, _, _, _) => Task.FromResult(new ActionRefResolution())),
            new NetworkConfig());

        var result = await auditEngine.AuditAsync(lintResult, source, "workflow.yml");

        await Assert.That(result.Diagnostics.Length).IsEqualTo(lintResult.Diagnostics.Length);
        await Assert.That(result.AddedCount).IsEqualTo(0);
        await Assert.That(result.SkippedCount).IsEqualTo(0);
        await Assert.That(result.FailedCount).IsEqualTo(0);
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
        var lintResult = engine.Check(source, "workflow.yml");

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

        var result = await auditEngine.AuditAsync(lintResult, source, "workflow.yml");
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
        var lintResult = engine.Check(source, "workflow.yml");

        var auditEngine = new OnlineAuditEngine(
            null,
            new DelegateActionRefResolver((_, _, _, _) => Task.FromResult(new ActionRefResolution(
                CommitExists: false,
                HasBranchReference: false,
                HasTagReference: false,
                IsTaggedCommit: false))),
            new NetworkConfig());

        var result = await auditEngine.AuditAsync(lintResult, source, "workflow.yml");

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
        var lintResult = engine.Check(source, "workflow.yml");

        var auditEngine = new OnlineAuditEngine(
            null,
            new DelegateActionRefResolver((_, _, _, _) => throw new InvalidOperationException("boom")),
            new NetworkConfig());

        var result = await auditEngine.AuditAsync(lintResult, source, "workflow.yml");

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
        var lintResult = engine.Check(source, "workflow.yml");

        var auditEngine = new OnlineAuditEngine(
            null,
            new DelegateActionRefResolver((_, _, _, _) => throw new InvalidOperationException("boom")),
            new NetworkConfig { OnError = NetworkErrorMode.Fail });

        await Assert.That(async () => await auditEngine.AuditAsync(lintResult, source, "workflow.yml"))
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
        var lintResult = engine.Check(source, "workflow.yml");

        var auditEngine = new OnlineAuditEngine(
            new DelegateActionAdvisoryProvider((_, _, _, _) => Task.FromResult<ActionAdvisory?>(new ActionAdvisory("GHSA-test", "desc"))),
            new DelegateActionRefResolver((_, _, _, _) => Task.FromResult(new ActionRefResolution(
                CommitExists: false,
                HasBranchReference: true,
                HasTagReference: true,
                IsTaggedCommit: false))),
            new NetworkConfig());

        var result = await auditEngine.AuditAsync(lintResult, source, "workflow.yml");

        await Assert.That(result.AddedCount).IsGreaterThanOrEqualTo(0);
        await Assert.That(result.SkippedCount).IsEqualTo(0);
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
