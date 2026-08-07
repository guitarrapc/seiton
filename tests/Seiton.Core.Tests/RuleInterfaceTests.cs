using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{
    [Test]
    public async Task RuleInterface_CanBeUsedWithWorkflowVisitor()
    {
        var sourceBytes = Array.Empty<byte>();
        var arena = new AstArena(sourceBytes);

        var runPayload = arena.AddExecRun(new ExecRunData
        {
            Run = arena.AddString(new Utf8Slice(0, 0), false, default),
        });
        var runStep = arena.AddStep(new StepData
        {
            ExecKind = StepExecKind.Run,
            ExecPayload = runPayload,
        });

        var buildJob = arena.AddJob(new JobData
        {
            Id = arena.AddString(new Utf8Slice(0, 0), false, default),
            Steps = arena.AddStepIdList([runStep]),
        });
        var jobsFirst = arena.JobEntryCount;
        arena.AddJobEntry(new JobEntryData { Key = new Utf8Slice(0, 0), Job = buildJob });

        var firstEvent = arena.EventCount;
        arena.AddEvent(new EventData
        {
            Kind = EventKind.Webhook,
            EventName = arena.AddString(new Utf8Slice(0, 0), false, default),
            Payload = arena.AddWebhookEvent(new WebhookEventData
            {
                Hook = arena.AddString(new Utf8Slice(0, 0), false, default),
            }),
        });
        arena.AddEvent(new EventData
        {
            Kind = EventKind.Scheduled,
            EventName = arena.AddString(new Utf8Slice(0, 0), false, default),
            Payload = arena.AddScheduledEvent(default),
        });

        var workflow = new Workflow
        {
            On = new NodeRange(firstEvent, 2),
            Jobs = new NodeRange(jobsFirst, 1),
        };

        var rule = new CountingRule();
        rule.SetConfig(LintConfig.Empty);

        var visitor = new WorkflowVisitor();
        visitor.AddPass(rule);
        visitor.Visit(new WorkflowRef(arena, workflow));

        await Assert.That(rule.Id).IsEqualTo(RuleId.JobStructure);
        await Assert.That(rule.Name).IsEqualTo("Test Rule");
        await Assert.That(rule.WorkflowPreCount).IsEqualTo(1);
        await Assert.That(rule.EventCount).IsEqualTo(2);
        await Assert.That(rule.JobPreCount).IsEqualTo(1);
        await Assert.That(rule.StepCount).IsEqualTo(1);
        await Assert.That(rule.JobPostCount).IsEqualTo(1);
        await Assert.That(rule.WorkflowPostCount).IsEqualTo(1);
        await Assert.That(rule.GetDiagnostics()).IsEmpty();
    }

    [Test]
    public async Task RuleCatalog_DefaultRules_MatchDocumentedScope()
    {
        var rules = RuleCatalog.CreateDefaultRules();

        await Assert.That(rules.Length).IsEqualTo(60);
        await Assert.That(rules[0].Id).IsEqualTo(RuleId.JobStructure);
        await Assert.That(rules[1].Id).IsEqualTo(RuleId.ReusableWorkflow);
        await Assert.That(rules[2].Id).IsEqualTo(RuleId.Permissions);
        await Assert.That(rules[3].Id).IsEqualTo(RuleId.PopularActionInputs);
        await Assert.That(rules[4].Id).IsEqualTo(RuleId.UnpinnedUses);
        await Assert.That(rules[5].Id).IsEqualTo(RuleId.UnpinnedImage);
        await Assert.That(rules[6].Id).IsEqualTo(RuleId.DangerousTriggers);
        await Assert.That(rules[7].Id).IsEqualTo(RuleId.JobPermissionsRequired);
        await Assert.That(rules[8].Id).IsEqualTo(RuleId.NeedsGraph);
        await Assert.That(rules[9].Id).IsEqualTo(RuleId.ShellName);
        await Assert.That(rules[10].Id).IsEqualTo(RuleId.RunnerLabel);
        await Assert.That(rules[11].Id).IsEqualTo(RuleId.IdNaming);
        await Assert.That(rules[12].Id).IsEqualTo(RuleId.GlobPattern);
        await Assert.That(rules[13].Id).IsEqualTo(RuleId.DispatchInputs);
        await Assert.That(rules[14].Id).IsEqualTo(RuleId.ScheduleEvent);
        await Assert.That(rules[15].Id).IsEqualTo(RuleId.DenyWriteAll);
        await Assert.That(rules[16].Id).IsEqualTo(RuleId.Credentials);
        await Assert.That(rules[17].Id).IsEqualTo(RuleId.TemplateInjection);
        await Assert.That(rules[18].Id).IsEqualTo(RuleId.ExprUndefinedVar);
        await Assert.That(rules[19].Id).IsEqualTo(RuleId.RunEnvContextDirectUse);
        await Assert.That(rules[20].Id).IsEqualTo(RuleId.RunnerNoLatest);
        await Assert.That(rules[21].Id).IsEqualTo(RuleId.RunSecretsContextDirectUse);
        await Assert.That(rules[22].Id).IsEqualTo(RuleId.RunInputsContextDirectUse);
        await Assert.That(rules[23].Id).IsEqualTo(RuleId.SecretsWholeContextAccess);
        await Assert.That(rules[24].Id).IsEqualTo(RuleId.CheckoutPersistCredentials);
        await Assert.That(rules[25].Id).IsEqualTo(RuleId.DenyReadAll);
        await Assert.That(rules[26].Id).IsEqualTo(RuleId.DenyInheritSecrets);
        await Assert.That(rules[27].Id).IsEqualTo(RuleId.JobTimeoutMinutesRequired);
        await Assert.That(rules[28].Id).IsEqualTo(RuleId.GitHubAppTokenInputs);
        await Assert.That(rules[29].Id).IsEqualTo(RuleId.CachePoisoning);
        await Assert.That(rules[30].Id).IsEqualTo(RuleId.SelfHostedRunner);
        await Assert.That(rules[31].Id).IsEqualTo(RuleId.UnredactedSecrets);
        await Assert.That(rules[32].Id).IsEqualTo(RuleId.SecretsOutsideEnv);
        await Assert.That(rules[33].Id).IsEqualTo(RuleId.WorkflowSecrets);
        await Assert.That(rules[34].Id).IsEqualTo(RuleId.JobSecrets);
        await Assert.That(rules[35].Id).IsEqualTo(RuleId.ActionShellIsRequired);
        await Assert.That(rules[36].Id).IsEqualTo(RuleId.Matrix);
        await Assert.That(rules[37].Id).IsEqualTo(RuleId.EnvVar);
        await Assert.That(rules[38].Id).IsEqualTo(RuleId.DeprecatedCommands);
        await Assert.That(rules[39].Id).IsEqualTo(RuleId.IfCond);
        await Assert.That(rules[40].Id).IsEqualTo(RuleId.FakeTernary);
        await Assert.That(rules[41].Id).IsEqualTo(RuleId.ArchivedUses);
        await Assert.That(rules[42].Id).IsEqualTo(RuleId.InsecureCommands);
        await Assert.That(rules[43].Id).IsEqualTo(RuleId.OverprovisionedSecrets);
        await Assert.That(rules[44].Id).IsEqualTo(RuleId.ForbiddenUses);
        await Assert.That(rules[45].Id).IsEqualTo(RuleId.RefVersionMismatch);
        await Assert.That(rules[46].Id).IsEqualTo(RuleId.UseTrustedPublishing);
        await Assert.That(rules[47].Id).IsEqualTo(RuleId.LocalActionInputs);
        await Assert.That(rules[48].Id).IsEqualTo(RuleId.WorkflowCallInputDefault);
        await Assert.That(rules[49].Id).IsEqualTo(RuleId.OutdatedActionRunner);
        await Assert.That(rules[50].Id).IsEqualTo(RuleId.IfExprWrapper);
        await Assert.That(rules[51].Id).IsEqualTo(RuleId.ConcurrencyLimits);
        await Assert.That(rules[52].Id).IsEqualTo(RuleId.UnsoundCondition);
        await Assert.That(rules[53].Id).IsEqualTo(RuleId.UnpinnedTools);
        await Assert.That(rules[54].Id).IsEqualTo(RuleId.UnsoundContains);
        await Assert.That(rules[55].Id).IsEqualTo(RuleId.BotConditions);
        await Assert.That(rules[56].Id).IsEqualTo(RuleId.Artipacked);
        await Assert.That(rules[57].Id).IsEqualTo(RuleId.CheckoutUnsafePr);
        await Assert.That(rules[58].Id).IsEqualTo(RuleId.BackgroundSteps);
        await Assert.That(rules[59].Id).IsEqualTo(RuleId.DeprecatedPermissions);

        await Assert.That(RuleCatalog.GetPriority("job-structure")).IsEqualTo(0);
        await Assert.That(RuleCatalog.GetPriority("reusable-workflow")).IsEqualTo(1);
        await Assert.That(RuleCatalog.GetPriority("permissions")).IsEqualTo(2);
        await Assert.That(RuleCatalog.GetPriority("popular-action-inputs")).IsEqualTo(3);
        await Assert.That(RuleCatalog.GetPriority("unpinned-uses")).IsEqualTo(4);
        await Assert.That(RuleCatalog.GetPriority("unpinned-image")).IsEqualTo(5);
        await Assert.That(RuleCatalog.GetPriority("dangerous-triggers")).IsEqualTo(6);
        await Assert.That(RuleCatalog.GetPriority("job-permissions-required")).IsEqualTo(7);
        await Assert.That(RuleCatalog.GetPriority("needs-graph")).IsEqualTo(8);
        await Assert.That(RuleCatalog.GetPriority("shell-name")).IsEqualTo(9);
        await Assert.That(RuleCatalog.GetPriority("runner-label")).IsEqualTo(10);
        await Assert.That(RuleCatalog.GetPriority("id-naming")).IsEqualTo(11);
        await Assert.That(RuleCatalog.GetPriority("glob-pattern")).IsEqualTo(12);
        await Assert.That(RuleCatalog.GetPriority("dispatch-inputs")).IsEqualTo(13);
        await Assert.That(RuleCatalog.GetPriority("schedule-event")).IsEqualTo(14);
        await Assert.That(RuleCatalog.GetPriority("deny-write-all")).IsEqualTo(15);
        await Assert.That(RuleCatalog.GetPriority("credentials")).IsEqualTo(16);
        await Assert.That(RuleCatalog.GetPriority("template-injection")).IsEqualTo(17);
        await Assert.That(RuleCatalog.GetPriority("expr-undefined-var")).IsEqualTo(18);
        await Assert.That(RuleCatalog.GetPriority("run-env-context-direct-use")).IsEqualTo(19);
        await Assert.That(RuleCatalog.GetPriority("runner-no-latest")).IsEqualTo(20);
        await Assert.That(RuleCatalog.GetPriority("run-secrets-context-direct-use")).IsEqualTo(21);
        await Assert.That(RuleCatalog.GetPriority("run-inputs-context-direct-use")).IsEqualTo(22);
        await Assert.That(RuleCatalog.GetPriority("secrets-whole-context-access")).IsEqualTo(23);
        await Assert.That(RuleCatalog.GetPriority("checkout-persist-credentials")).IsEqualTo(24);
        await Assert.That(RuleCatalog.GetPriority("deny-read-all")).IsEqualTo(25);
        await Assert.That(RuleCatalog.GetPriority("deny-inherit-secrets")).IsEqualTo(26);
        await Assert.That(RuleCatalog.GetPriority("job-timeout-minutes-required")).IsEqualTo(27);
        await Assert.That(RuleCatalog.GetPriority("github-app-token-inputs")).IsEqualTo(28);
        await Assert.That(RuleCatalog.GetPriority("cache-poisoning-trigger")).IsEqualTo(33);
        await Assert.That(RuleCatalog.GetPriority("self-hosted-runner-trigger")).IsEqualTo(34);
        await Assert.That(RuleCatalog.GetPriority("unredacted-secrets")).IsEqualTo(35);
        await Assert.That(RuleCatalog.GetPriority("secrets-outside-env")).IsEqualTo(36);
        await Assert.That(RuleCatalog.GetPriority("workflow-secrets")).IsEqualTo(37);
        await Assert.That(RuleCatalog.GetPriority("job-secrets")).IsEqualTo(38);
        await Assert.That(RuleCatalog.GetPriority("action-shell-is-required")).IsEqualTo(39);
        await Assert.That(RuleCatalog.GetPriority("matrix")).IsEqualTo(40);
        await Assert.That(RuleCatalog.GetPriority("env-var")).IsEqualTo(41);
        await Assert.That(RuleCatalog.GetPriority("deprecated-commands")).IsEqualTo(42);
        await Assert.That(RuleCatalog.GetPriority("if-cond")).IsEqualTo(43);
        await Assert.That(RuleCatalog.GetPriority("fake-ternary")).IsEqualTo(44);
        await Assert.That(RuleCatalog.GetPriority("archived-uses")).IsEqualTo(45);
        await Assert.That(RuleCatalog.GetPriority("insecure-commands")).IsEqualTo(46);
        await Assert.That(RuleCatalog.GetPriority("overprovisioned-secrets")).IsEqualTo(47);
        await Assert.That(RuleCatalog.GetPriority("forbidden-uses")).IsEqualTo(48);
        await Assert.That(RuleCatalog.GetPriority("ref-version-mismatch")).IsEqualTo(49);
        await Assert.That(RuleCatalog.GetPriority("use-trusted-publishing")).IsEqualTo(50);
        await Assert.That(RuleCatalog.GetPriority("local-action-inputs")).IsEqualTo(51);
        await Assert.That(RuleCatalog.GetPriority("workflow-call-input-default")).IsEqualTo(52);
        await Assert.That(RuleCatalog.GetPriority("outdated-action-runner")).IsEqualTo(53);
        await Assert.That(RuleCatalog.GetPriority("if-expr-wrapper")).IsEqualTo(54);
        await Assert.That(RuleCatalog.GetPriority("concurrency-limits")).IsEqualTo(55);
        await Assert.That(RuleCatalog.GetPriority("unsound-condition")).IsEqualTo(56);
        await Assert.That(RuleCatalog.GetPriority("unpinned-tools")).IsEqualTo(57);
        await Assert.That(RuleCatalog.GetPriority("unsound-contains")).IsEqualTo(58);
        await Assert.That(RuleCatalog.GetPriority("bot-conditions")).IsEqualTo(59);
        await Assert.That(RuleCatalog.GetPriority("artipacked")).IsEqualTo(60);
        await Assert.That(RuleCatalog.GetPriority("checkout-unsafe-pr")).IsEqualTo(61);
        await Assert.That(RuleCatalog.GetPriority("background-steps")).IsEqualTo(62);
        await Assert.That(RuleCatalog.GetPriority("deprecated-permissions")).IsEqualTo(63);
        await Assert.That(RuleCatalog.GetPriority("known-vulnerable-actions")).IsEqualTo(29);
        await Assert.That(RuleCatalog.GetPriority("impostor-commit")).IsEqualTo(30);
        await Assert.That(RuleCatalog.GetPriority("ref-confusion")).IsEqualTo(31);
        await Assert.That(RuleCatalog.GetPriority("stale-action-refs")).IsEqualTo(32);
    }

    [Test]
    public async Task RuleCatalog_OnlineAuditRules_AreKnownForResolution()
    {
        await Assert.That(RuleCatalog.TryResolveRuleId("known-vulnerable-actions", out var knownVulnerable)).IsTrue();
        await Assert.That(knownVulnerable).IsEqualTo(RuleId.KnownVulnerableActions);
        await Assert.That(RuleCatalog.TryResolveRuleId("impostor-commit", out var impostorCommit)).IsTrue();
        await Assert.That(impostorCommit).IsEqualTo(RuleId.ImpostorCommit);
        await Assert.That(RuleCatalog.TryResolveRuleId("ref-confusion", out var refConfusion)).IsTrue();
        await Assert.That(refConfusion).IsEqualTo(RuleId.RefConfusion);
        await Assert.That(RuleCatalog.TryResolveRuleId("stale-action-refs", out var staleActionRefs)).IsTrue();
        await Assert.That(staleActionRefs).IsEqualTo(RuleId.StaleActionRefs);
    }

    [Test]
    public async Task RuleCatalog_TriggerRuleIds_UseTriggerSuffixOnly()
    {
        await Assert.That(RuleCatalog.TryResolveRuleId("cache-poisoning-trigger", out var cachePoisoning)).IsTrue();
        await Assert.That(cachePoisoning).IsEqualTo(RuleId.CachePoisoning);
        await Assert.That(RuleCatalog.TryResolveRuleId("self-hosted-runner-trigger", out var selfHostedRunner)).IsTrue();
        await Assert.That(selfHostedRunner).IsEqualTo(RuleId.SelfHostedRunner);

        await Assert.That(RuleCatalog.TryResolveRuleId("cache-poisoning", out _)).IsFalse();
        await Assert.That(RuleCatalog.TryResolveRuleId("self-hosted-runner", out _)).IsFalse();
    }

    [Test]
    public async Task RuleCatalog_CanonicalIdFormat_IsRejectedAsUnknown()
    {
        // Canonical IDs (seiton-lint-rule-NNN) are no longer supported; only semantic IDs are accepted.
        await Assert.That(RuleCatalog.TryResolveRuleId("seiton-lint-rule-001", out _)).IsFalse();
        await Assert.That(RuleCatalog.TryResolveRuleId("seiton-lint-rule-008", out _)).IsFalse();
        await Assert.That(RuleCatalog.TryResolveRuleId("seiton-lint-rule-030", out _)).IsFalse();
    }

    [Test]
    public async Task RuleCatalog_Priorities_AreUnique()
    {
        // Priorities must be unique across all rules for deterministic rule ordering.
        var allRuleIds = Enum.GetValues<RuleId>().Where(id => id != RuleId.Syntax).ToArray();
        var priorityToRule = new Dictionary<int, string>();
        foreach (var ruleId in allRuleIds)
        {
            var semanticId = ruleId.ToId();
            var priority = RuleCatalog.GetPriority(semanticId);
            await Assert.That(priority).IsNotEqualTo(int.MaxValue - 1).Because($"rule '{semanticId}' must have a registered priority (got unknown sentinel)");
            if (priorityToRule.TryGetValue(priority, out var existing))
            {
                Assert.Fail($"Priority {priority} is used by both '{existing}' and '{semanticId}'. Priorities must be unique.");
            }
            priorityToRule[priority] = semanticId;
        }
    }

    // Template injection — position precision & per-reference reporting

    // Contextual Validation

    // Context availability — missing field visits

    // env context banned in workflow/job env

    // env context banned in job-level if

    // shell key context availability

    // special function availability

    // step.id no context allowed

    // available context listing in message

    // expr-undefined-var scope expansion

    // fromJSON broken JSON validation

    // double-quote string literal detection

    [Test]
    public async Task AutoFixCatalog_FixableRulesAttachFix_TableDriven()
    {
        var cases = new[]
        {
            new FixabilityCase(
                "job-structure",
                new JobStructureRule(),
                """
                on: push
                jobs:
                    build:
                        steps:
                            - run: echo ng
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "reusable-workflow",
                new ReusableWorkflowRule(),
                """
                on: push
                jobs:
                    reuse:
                        uses: owner/repo/.github/workflows/reuse.yml@main
                        container: node:20
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "permissions",
                new PermissionsRule(),
                """
                on: push
                permissions: admin-all
                jobs: {}
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "popular-action-inputs",
                new PopularActionInputsRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - uses: actions/checkout@v4
                              with:
                                  fetch-depht: 1
                """,
                ExpectsFix: true),
            new FixabilityCase(
                "unpinned-uses",
                new UnpinnedUsesRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - uses: actions/checkout@v4
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "unpinned-image",
                new UnpinnedImageRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        container:
                            image: ghcr.io/example/app:latest
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "dangerous-triggers",
                new DangerousTriggersRule(),
                """
                on: pull_request_target
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "job-permissions-required",
                new JobPermissionsRequiredRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: true),
            new FixabilityCase(
                "needs-graph",
                new NeedsGraphRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        needs: [missing]
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "shell-name",
                new ShellNameRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - shell: fish
                              run: echo ok
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "runner-label",
                new RunnerLabelRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-9999
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "runner-no-latest",
                new RunnerNoLatestRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "id-naming",
                new IdNamingRule(),
                """
                on: push
                jobs:
                    "build job":
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: true),
            new FixabilityCase(
                "glob-pattern",
                new GlobPatternRule(),
                """
                on:
                    push:
                        branches:
                            - "***"
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "deny-write-all",
                new DenyWriteAllRule(),
                """
                on: push
                permissions: write-all
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: true),
            new FixabilityCase(
                "credentials",
                new CredentialsRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        container:
                            image: registry.example.com/team/app:1.0.0
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "template-injection",
                new TemplateInjectionRule(),
                """
                on: pull_request
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo "${{ github.event.pull_request.title }}"
                """,
                ExpectsFix: true),
            new FixabilityCase(
                "expr-undefined-var",
                new ExprUndefinedVarRule(),
                """
                on: push
                jobs:
                    build:
                        if: ${{ steps.prep.outcome == 'success' }}
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "run-env-context-direct-use",
                new RunEnvContextDirectUseRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        env:
                            VERSION: 1.2.3
                        steps:
                            - run: echo "${{ env.VERSION }}"
                """,
                ExpectsFix: true),
            new FixabilityCase(
                "run-secrets-context-direct-use",
                new RunSecretsContextDirectUseRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        env:
                            TOKEN: ${{ secrets.MY_TOKEN }}
                        steps:
                            - run: echo "${{ secrets.MY_TOKEN }}"
                """,
                ExpectsFix: true),
            new FixabilityCase(
                "run-inputs-context-direct-use",
                new RunInputsContextDirectUseRule(),
                """
                on: workflow_dispatch
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        env:
                            TARGET: ${{ inputs.target }}
                        steps:
                            - run: echo "${{ inputs.target }}"
                """,
                ExpectsFix: true),
            new FixabilityCase(
                "secrets-whole-context-access",
                new SecretsWholeContextAccessRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo "${{ toJson(secrets) }}"
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "checkout-persist-credentials",
                new CheckoutPersistCredentialsRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - uses: actions/checkout@v4
                """,
                ExpectsFix: true),
            new FixabilityCase(
                "deny-read-all",
                new DenyReadAllRule(),
                """
                on: push
                permissions: read-all
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: true),
            new FixabilityCase(
                "deny-inherit-secrets",
                new DenyInheritSecretsRule(),
                """
                on: push
                jobs:
                    reuse:
                        uses: owner/repo/.github/workflows/reuse.yml@main
                        secrets: inherit
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "job-timeout-minutes-required",
                new JobTimeoutMinutesRequiredRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "github-app-token-inputs",
                new GitHubAppTokenInputsRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - uses: actions/create-github-app-token@v2
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "cache-poisoning-trigger",
                new CachePoisoningRule(),
                """
                on: pull_request_target
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - uses: actions/cache@v4
                              with:
                                  path: ~/.npm
                                  key: npm-${{ runner.os }}
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "self-hosted-runner-trigger",
                new SelfHostedRunnerRule(),
                """
                on: pull_request
                jobs:
                    build:
                        runs-on: self-hosted
                        steps:
                            - run: echo ok
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "unredacted-secrets",
                new UnredactedSecretsRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        env:
                            TOKEN: ${{ secrets.GITHUB_TOKEN }}
                        steps:
                            - run: echo "${TOKEN}"
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "secrets-outside-env",
                new SecretsOutsideEnvRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - if: ${{ secrets.GITHUB_TOKEN != '' }}
                              run: echo ng
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "workflow-secrets",
                new WorkflowSecretsRule(),
                """
                on: push
                env:
                    GITHUB_TOKEN: ${{ github.token }}
                jobs:
                    a:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo a
                    b:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo b
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "job-secrets",
                new JobSecretsRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        env:
                            GITHUB_TOKEN: ${{ github.token }}
                        steps:
                            - run: echo a
                            - run: echo b
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "matrix",
                new MatrixRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        strategy:
                            matrix:
                                os: []
                        steps:
                            - run: echo ng
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "env-var",
                new EnvVarRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        env:
                            github_token: x
                        steps:
                            - run: echo ng
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "deprecated-commands",
                new DeprecatedCommandsRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo "::set-output name=result::ok"
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "if-cond",
                new IfCondRule(),
                """
                on: push
                jobs:
                    build:
                        if: ${{ false }}
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo ng
                """,
                ExpectsFix: false),
                new FixabilityCase(
                    "fake-ternary",
                    new FakeTernaryRule(),
                    """
                    on: push
                    jobs:
                        build:
                            if: ${{ github.ref_name == 'main' && 'prod' || 'dev' }}
                            runs-on: ubuntu-latest
                            steps:
                                - run: echo ng
                    """,
                    ExpectsFix: false),
            new FixabilityCase(
                "archived-uses",
                new ArchivedUsesRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - uses: actions-rs/toolchain@v1
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "insecure-commands",
                new InsecureCommandsRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        env:
                            ACTIONS_ALLOW_UNSECURE_COMMANDS: true
                        steps:
                            - run: echo ng
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "overprovisioned-secrets",
                new OverprovisionedSecretsRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - env:
                                A: ${{ secrets.A }}
                                B: ${{ secrets.B }}
                                C: ${{ secrets.C }}
                                D: ${{ secrets.D }}
                                E: ${{ secrets.E }}
                                F: ${{ secrets.F }}
                              run: echo ng
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "forbidden-uses",
                new ForbiddenUsesRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - uses: bad-org/unsafe-action@v1
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "ref-version-mismatch",
                new RefVersionMismatchRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - uses: owner/action-v1@v2.0.0
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "use-trusted-publishing",
                new UseTrustedPublishingRule(),
                """
                on: push
                jobs:
                    publish:
                        runs-on: ubuntu-latest
                        steps:
                            - run: npm publish
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "unsound-condition",
                new UnsoundConditionRule(),
                "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: |\n      ${{ true }}\n    steps:\n      - run: echo ng\n",
                ExpectsFix: true),
            new FixabilityCase(
                "unpinned-tools",
                new UnpinnedToolsRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - uses: aquasecurity/setup-trivy@v0.2.0
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "unsound-contains",
                new UnsoundContainsRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        if: contains('refs/heads/main', github.ref)
                        steps:
                            - run: echo test
                """,
                ExpectsFix: false),
            new FixabilityCase(
                "bot-conditions",
                new BotConditionsRule(),
                """
                on: pull_request
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        if: github.actor == 'dependabot[bot]'
                        steps:
                            - run: echo test
                """,
                ExpectsFix: false),
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            using var result = new LintEngine([c.Rule]).Check(
                Encoding.UTF8.GetBytes(NormalizeYaml(c.Yaml)),
                $"fixability-{c.RuleId}.yml",
                new LintConfig { Fix = new FixConfig { Enabled = true } });
            var diagnostics = result.Diagnostics.Where(x => x.RuleId == c.RuleId).ToArray();
            if (diagnostics.Length == 0)
            {
                throw new InvalidOperationException($"fixability case '{c.RuleId}' produced no diagnostics");
            }

            if (c.ExpectsFix)
            {
                var hasFix = diagnostics.Any(x => x.Fix is not null);
                if (!hasFix)
                {
                    throw new InvalidOperationException($"fixability case '{c.RuleId}' expected at least one attached fix");
                }
            }
            else
            {
                var hasUnexpectedFix = diagnostics.Any(x => x.Fix is not null);
                if (hasUnexpectedFix)
                {
                    throw new InvalidOperationException($"fixability case '{c.RuleId}' unexpectedly attached a fix");
                }
            }
        }
    }

    [Test]
    public async Task ContainsOverload_ObjectArg_ReportsAllOverloadMismatches()
    {
        // When contains() is called with an object type as first arg,
        // both overloads (string,any) and (array<any>,any) should fail
        // and both should be reported as diagnostics.
        var yaml = NormalizeYaml("""
        on: push
        jobs:
          foo:
            strategy:
              matrix:
                include:
                  - obj: ${{ fromJSON('{"bool":true,"arr":[false]}') }}
                  - str: ${{ fromJSON('"hello"') }}
            runs-on: ubuntu-latest
            steps:
              - run: echo ${{ contains(matrix.obj, matrix.str) }}
        """);

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var allDiags = result.Diagnostics.Select(d => $"{d.Location.StartLine}:{d.Location.StartColumn}: {d.Message}").ToList();

        // Should have two "not assignable" diagnostics — one per overload
        var notAssignable = result.Diagnostics
            .Where(d => d.Message.Contains("not assignable", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(notAssignable.Length).IsEqualTo(2)
            .Because($"Expected 2 overload mismatch diagnostics but got:\n{string.Join("\n", allDiags)}");

        // One should mention array<any>, the other should mention string
        await Assert.That(notAssignable.Any(d => d.Message.Contains("\"array<any>\"", StringComparison.Ordinal))).IsTrue();
        await Assert.That(notAssignable.Any(d => d.Message.Contains("\"string\"", StringComparison.Ordinal))).IsTrue();
    }

    // ── template-injection fix tests ──────────────────────────────────────────────────────

    [Test]
    public async Task LintConfig_Validate_OutputSortOrder_Parses()
    {
        var yaml = """
        output:
          sort-order: rule
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Config!.Output.SortOrder).IsEqualTo(DiagnosticSortOrder.Rule);
    }

    [Test]
    public async Task LintConfig_Validate_OutputSortOrder_Location()
    {
        var yaml = """
        output:
          sort-order: location
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config!.Output.SortOrder).IsEqualTo(DiagnosticSortOrder.Location);
    }

    [Test]
    public async Task LintConfig_Validate_OutputSortOrder_InvalidValue_ReportsError()
    {
        var yaml = """
        output:
          sort-order: invalid
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("sort-order", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintConfig_Validate_OutputSortOrder_Default_IsLocation()
    {
        var yaml = """
        rules: {}
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config!.Output.SortOrder).IsEqualTo(DiagnosticSortOrder.Location);
    }

    [Test]
    public async Task LintConfig_Validate_OutputStructureSnippets_UnknownKey_ReportsError()
    {
        var yaml = """
        output:
          structure-snippets: false
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown output key", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parser_EmptyRunsOnLabel_MessageIncludesAvailableLabels()
    {
        // When runs-on has an empty label, the parser message should include
        // available labels so the user knows what valid values are.
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ''
            steps:
              - run: echo hello
        """;

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var emptyLabelDiag = result.Diagnostics.FirstOrDefault(d => d.Message?.Contains("should not be empty") == true);
        await Assert.That(emptyLabelDiag.Message).IsNotNull();
        await Assert.That(emptyLabelDiag.Message!).Contains("available labels are");
        await Assert.That(emptyLabelDiag.Message!).Contains("ubuntu-latest");
    }

    [Test]
    public async Task Parser_EmptyRunsOnLabelInArray_MessageIncludesAvailableLabels()
    {
        // When runs-on array has an empty element, the parser message for the
        // empty element should include available labels.
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ['ubuntu-latest', '']\n    steps:\n      - run: echo\n";

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var emptyLabelDiag = result.Diagnostics.FirstOrDefault(d => d.Message?.Contains("should not be empty") == true);
        await Assert.That(emptyLabelDiag.Message).IsNotNull();
        await Assert.That(emptyLabelDiag.Message!).Contains("available labels are");
        await Assert.That(emptyLabelDiag.Message!).Contains("ubuntu-latest");
    }

    [Test]
    public async Task Parser_EmptyRunsOnArray_MessageIncludesAvailableLabels()
    {
        // When runs-on is an empty array [], the message should include available labels.
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: []
            steps:
              - run: echo hello
        """;

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var emptyDiag = result.Diagnostics.FirstOrDefault(d => d.Message?.Contains("should not be empty") == true);
        await Assert.That(emptyDiag.Message).IsNotNull();
        await Assert.That(emptyDiag.Message!).Contains("available labels are");
        await Assert.That(emptyDiag.Message!).Contains("ubuntu-latest");
    }

    [Test]
    public async Task UnexpectedKey_MessageContainsHas()
    {
        var yaml = """
            on:
              push:
                BRANCHES: [main]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
            """;

        var parseResult = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        try
        {
            var diagnostics = parseResult.Diagnostics.Where(d => d.Message?.Contains("unexpected key", StringComparison.Ordinal) == true).ToList();
            await Assert.That(diagnostics).IsNotEmpty();
            await Assert.That(diagnostics[0].Message).Contains("has unexpected key");
        }
        finally
        {

        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Q1: disable-next-line above section vs above specific key
    // ──────────────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────────────
    // Q2: disable-next-line with multi-line if: block scalar
    // ──────────────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────────────
    // Q3: Multiple rule IDs in a single disable-next-line comment
    // ──────────────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────────────
    // disable-job: job-body diagnostics (Job.Range must cover full mapping)
    // ──────────────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────────────
    // Config exclusion: job-scoped suppression for body diagnostics
    // ──────────────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────────────
    // disable-job: step-level diagnostics inside job body
    // ──────────────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────────────
    // disable-job: multiple rules, suppression source, error cases
    // ──────────────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────────────
    // disable-job: cross-job boundary verification
    // ──────────────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────────────
    // disable-job + disable-next-line interaction
    // ──────────────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────────────
}
