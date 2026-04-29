using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

public sealed class RuleInterfaceTests
{
    [Test]
    public async Task LintEngine_ReturnsCombinedParseAndRuleDiagnostics()
    {
        var yaml = """
        on: push
        jobs:
          build:
            steps:
              - run: echo hello
        """;

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "lint-engine.yml");

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.ParseDiagnostics.Any(x => x.Message.Contains("\"runs-on\" section is missing", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"runs-on\" section is missing", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_FatalParse_ReturnsParseDiagnosticsOnly()
    {
        var yaml = "[]";

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "fatal.yml");

        await Assert.That(result.HasFatalError).IsTrue();
        await Assert.That(result.Workflow).IsNull();
        await Assert.That(result.Diagnostics).HasSingleItem();
        await Assert.That(result.Diagnostics[0].Message).IsEqualTo("workflow root must be object");
        await Assert.That(result.Diagnostics[0].FilePath).IsEqualTo("fatal.yml");
    }

    [Test]
    public async Task LintEngine_RuleDiagnostics_IncludeRuleIdAndFilePath()
    {
        var yaml = """
        on: push
        jobs:
            build:
                steps:
                    - run: echo hello
        """;

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "rule-filepath.yml");
        var diagnostic = result.Diagnostics.FirstOrDefault(x =>
            x.RuleId == "job-structure"
            && x.Message.Contains("\"runs-on\" section is missing", StringComparison.Ordinal));

        await Assert.That(diagnostic.Message.Length).IsGreaterThan(0);
        await Assert.That(diagnostic.RuleId).IsEqualTo("job-structure");
        await Assert.That(diagnostic.FilePath).IsEqualTo("rule-filepath.yml");
    }

    [Test]
    public async Task RuleInterface_CanBeUsedWithWorkflowVisitor()
    {
        var sourceBytes = Array.Empty<byte>();
        var arena = new AstArena(sourceBytes);

        var (jobs, _) = SliceMapTestExtensions.CreateSliceMap(
            (new Utf8String("build"u8), new Job
            {
                Id = arena.AddString(new Utf8Slice(0, 0), false, default),
                Steps =
                [
                    new Step
                    {
                        Exec = new ExecRun
                        {
                            Kind = StepExecKind.Run,
                            Run = arena.AddString(new Utf8Slice(0, 0), false, default),
                        },
                    },
                ],
            }));

        var workflow = new Workflow
        {
            On =
            [
                new WebhookEvent
                {
                    EventName = arena.AddString(new Utf8Slice(0, 0), false, default),
                    Hook = arena.AddString(new Utf8Slice(0, 0), false, default),
                },
                new ScheduledEvent
                {
                    EventName = arena.AddString(new Utf8Slice(0, 0), false, default),
                },
            ],
            Jobs = jobs,
        };

        var rule = new CountingRule();
        rule.SetConfig(LintConfig.Empty);

        var visitor = new WorkflowVisitor();
        visitor.AddPass(rule);
        visitor.Visit(workflow);

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
    public async Task SyntaxRule_ReportsJobConstraintDiagnostics()
    {
        var source = """
        jobs:
          build:
            uses: ./.github/workflows/reusable.yml
            runs-on: ubuntu-latest
            steps:
              - run: echo hello
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var arena = new AstArena(sourceBytes);

        var (jobs, _) = SliceMapTestExtensions.CreateSliceMap(
            (new Utf8String("build"u8), new Job
            {
                Id = arena.AddString(
                    new Utf8Slice(source.IndexOf("build", StringComparison.Ordinal), "build".Length),
                    false,
                    new TextRange(0, 0, 1, 1, 1, 1)),
                RunsOn = new Runner(),
                WorkflowCall = new WorkflowCall
                {
                    Uses = arena.AddString(new Utf8Slice(source.IndexOf("./.github/workflows/reusable.yml", StringComparison.Ordinal), "./.github/workflows/reusable.yml".Length), false, default),
                },
                Steps =
                [
                    new Step
                    {
                        Exec = new ExecRun
                        {
                            Kind = StepExecKind.Run,
                            Run = arena.AddString(new Utf8Slice(0, 0), false, default),
                        },
                    },
                ],
            }));

        var workflow = new Workflow
        {
            Jobs = jobs,
        };

        var visitor = new WorkflowVisitor();
        var rule = new SyntaxRule();
        rule.SetConfig(new LintConfig { Utf8Yaml = sourceBytes, Arena = arena });
        visitor.AddPass(rule);

        visitor.Visit(workflow);
        var diagnostics = rule.GetDiagnostics();

        await Assert.That(diagnostics.Any(x => x.Message.Contains("cannot have both uses and steps", StringComparison.Ordinal))).IsTrue();
        await Assert.That(diagnostics.Any(x => x.Message.Contains("cannot have both uses and runs-on", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task SyntaxRule_ReportsUnknownInputForPopularAction()
    {
        // Source buffer must contain all key-like text that SliceMap entries reference
        var source = "actions/checkout@v4\0fetch-depht\0build";
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var usesEnd = "actions/checkout@v4".Length;
        var inputKeyOffset = usesEnd + 1; // skip \0
        var inputKeyLength = "fetch-depht".Length;
        var buildKeyOffset = inputKeyOffset + inputKeyLength + 1;
        var buildKeyLength = "build".Length;

        var arena = new AstArena(sourceBytes);
        var inputsEntries = new SliceMap<StringNodeId>.Entry[]
        {
            new(new Utf8Slice(inputKeyOffset, inputKeyLength), arena.AddString(new Utf8Slice(0, 0), false, default)),
        };

        var (jobs, _) = SliceMapTestExtensions.CreateSliceMap(
            (new Utf8String("build"u8), new Job
            {
                Id = arena.AddString(
                    new Utf8Slice(buildKeyOffset, buildKeyLength),
                    false,
                    new TextRange(0, 0, 1, 1, 1, 1)),
                RunsOn = new Runner(),
                Steps =
                [
                    new Step
                    {
                        Exec = new ExecAction
                        {
                            Kind = StepExecKind.Action,
                            Uses = arena.AddString(
                                new Utf8Slice(0, usesEnd),
                                false,
                                new TextRange(0, usesEnd, 1, 1, 1, usesEnd + 1)),
                            Inputs = new SliceMap<StringNodeId>(inputsEntries, caseSensitive: false),
                        },
                        Range = new TextRange(0, 0, 1, 1, 1, 1),
                    },
                ],
            }));

        var workflow = new Workflow
        {
            Jobs = jobs,
        };

        var visitor = new WorkflowVisitor();
        var rule = new SyntaxRule();
        rule.SetConfig(new LintConfig { Utf8Yaml = sourceBytes, Arena = arena });
        visitor.AddPass(rule);

        visitor.Visit(workflow);
        var diagnostics = rule.GetDiagnostics();

        await Assert.That(diagnostics.Any(x => x.Severity == DiagnosticSeverity.Warning && x.Message.Contains("unknown input 'fetch-depht' for action 'actions/checkout@v4'", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_ReportsInvalidWorkflowPermissionsScalar()
    {
        var yaml = """
        on: push
        permissions: admin-all
        jobs: {}
        """.Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "permissions-invalid-scalar.yml");

        await Assert.That(result.ParseDiagnostics).IsEmpty();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("permissions scalar must be 'read-all' or 'write-all'", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_ReportsInvalidJobPermissionScopeValue()
    {
        var yaml = """
        on: push
        jobs:
            build:
                permissions:
                    contents: admin
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """.Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "permissions-invalid-scope.yml");

        await Assert.That(result.ParseDiagnostics).IsEmpty();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"admin\" is invalid as permission of scope \"contents\"", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_ReportsReusableWorkflowForbiddenKeys()
    {
        var yaml = """
        on: push
        jobs:
            reuse:
                uses: owner/repo/.github/workflows/reuse.yml@main
                container: node:20
        """.Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "reuse-forbidden-key.yml");

        // Parser no longer emits forbidden-key diagnostics (linter handles them)
        await Assert.That(result.ParseDiagnostics.Any(x => x.Message.Contains("calls reusable workflow with uses", StringComparison.Ordinal))).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("calls reusable workflow with uses", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task RuleCatalog_DefaultRules_MatchDocumentedScope()
    {
        var rules = RuleCatalog.CreateDefaultRules();

        await Assert.That(rules.Length).IsEqualTo(50);
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
        await Assert.That(RuleCatalog.GetPriority("cache-poisoning")).IsEqualTo(33);
        await Assert.That(RuleCatalog.GetPriority("self-hosted-runner")).IsEqualTo(34);
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
        await Assert.That(RuleCatalog.GetPriority("known-vulnerable-actions")).IsEqualTo(29);
        await Assert.That(RuleCatalog.GetPriority("impostor-commit")).IsEqualTo(30);
        await Assert.That(RuleCatalog.GetPriority("ref-confusion")).IsEqualTo(31);
        await Assert.That(RuleCatalog.GetPriority("stale-action-refs")).IsEqualTo(32);
    }

    [Test]
    public async Task RuleCatalog_OnlineAuditRules_AreKnownForResolutionAndCanonicalIds()
    {
        await Assert.That(RuleCatalog.TryResolveRuleId("known-vulnerable-actions", out var knownVulnerable)).IsTrue();
        await Assert.That(knownVulnerable).IsEqualTo(RuleId.KnownVulnerableActions);
        await Assert.That(RuleCatalog.GetCanonicalRuleId("local-action-inputs")).IsEqualTo("seiton-lint-rule-048");
        await Assert.That(RuleCatalog.GetCanonicalRuleId("workflow-call-input-default")).IsEqualTo("seiton-lint-rule-049");
        await Assert.That(RuleCatalog.GetCanonicalRuleId("outdated-action-runner")).IsEqualTo("seiton-lint-rule-050");
        await Assert.That(RuleCatalog.GetCanonicalRuleId("known-vulnerable-actions")).IsEqualTo("seiton-lint-rule-051");

        await Assert.That(RuleCatalog.TryResolveRuleId("seiton-lint-rule-052", out var impostorCommit)).IsTrue();
        await Assert.That(impostorCommit).IsEqualTo(RuleId.ImpostorCommit);
        await Assert.That(RuleCatalog.GetCanonicalRuleId("ref-confusion")).IsEqualTo("seiton-lint-rule-053");
        await Assert.That(RuleCatalog.GetCanonicalRuleId("stale-action-refs")).IsEqualTo("seiton-lint-rule-054");
    }

    [Test]
    public async Task RuleRegression_JobStructureRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-normal-job",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-uses-with-steps",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    steps:
                        - run: echo ng
            """,
            ["cannot have both uses and steps"]),
            new RuleCase(
            "ng-missing-runs-on",
            """
            on: push
            jobs:
                build:
                    steps:
                        - run: echo ng
            """,
            ["\"runs-on\" section is missing"]),
            new RuleCase(
            "ok-empty-uses-key-suppresses-runs-on-and-steps",
            """
            on: push
            jobs:
                call4:
                    uses:
                normal:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new JobStructureRule(), "job-structure", cases);
    }

    [Test]
    public async Task RuleRegression_ReusableWorkflowRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-reusable-allowed-keys",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    with:
                        target: prod
                    secrets: inherit
                    if: ${{ github.ref != '' }}
                    needs: []
                    concurrency: deploy
            """,
            []),
            new RuleCase(
            "ng-without-uses",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    with:
                        target: prod
                    steps:
                        - run: echo ng
            """,
            ["key 'with' requires uses"]),
            new RuleCase(
            "ng-forbidden-key-with-uses",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    container: node:20
            """,
            ["calls reusable workflow with uses"]),
            new RuleCase(
            "ng-remote-missing-ref",
            """
            on: push
            jobs:
                reuse:
                    uses: "foo/bar/workflow.yml"
            """,
            ["is not following the format"]),
            new RuleCase(
            "ng-remote-absolute-path",
            """
            on: push
            jobs:
                reuse:
                    uses: "/foo/bar/workflow.yml@main"
            """,
            ["is not following the format"]),
            new RuleCase(
            "ng-remote-missing-repo-path",
            """
            on: push
            jobs:
                reuse:
                    uses: "foo/workflow.yml@main"
            """,
            ["is not following the format"]),
            new RuleCase(
            "ok-remote-valid-format",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/path/to/workflow.yml@main
            """,
            []),
        };

        await AssertRuleCases(new ReusableWorkflowRule(), "reusable-workflow", cases);
    }

    [Test]
    public async Task LintEngine_ReusableWorkflowRule_LocalWorkflowContractValidation_ReportsMismatches()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-reuse-contract-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var calleePath = Path.Combine(workflowsDir, "reusable.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var calleeYaml = """
            on:
                workflow_call:
                    inputs:
                        target:
                            required: true
                            type: string
                        dry_run:
                            required: false
                            type: boolean
                    secrets:
                        token:
                            required: true
            jobs:
                noop:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo callee
            """;

            var callerYaml = """
            on: push
            jobs:
                deploy:
                    uses: ./.github/workflows/reusable.yml
                    with:
                        extra: test
                        dry_run: maybe
            """;

            File.WriteAllText(calleePath, NormalizeYaml(calleeYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            var result = new LintEngine([new ReusableWorkflowRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            var ruleDiagnostics = result.Diagnostics.Where(x => x.RuleId == "reusable-workflow").Select(x => x.Message).ToArray();

            await Assert.That(ruleDiagnostics.Any(m => m.Contains("unknown reusable workflow input 'extra'", StringComparison.Ordinal))).IsTrue();
            await Assert.That(ruleDiagnostics.Any(m => m.Contains("missing required reusable workflow input 'target'", StringComparison.Ordinal))).IsTrue();
            await Assert.That(ruleDiagnostics.Any(m => m.Contains("expects boolean but got 'maybe'", StringComparison.Ordinal))).IsTrue();
            await Assert.That(ruleDiagnostics.Any(m => m.Contains("missing required reusable workflow secret 'token'", StringComparison.Ordinal))).IsTrue();
            // Input type error should use jobs.'<id>'.with path (not .input)
            await Assert.That(ruleDiagnostics.Any(m => m.Contains("jobs.'deploy'.with", StringComparison.Ordinal) && m.Contains("expects boolean", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_ReusableWorkflowRule_LocalWorkflowContractValidation_AllowsValidCallerContract()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-reuse-contract-ok-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var calleePath = Path.Combine(workflowsDir, "reusable.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var calleeYaml = """
            on:
                workflow_call:
                    inputs:
                        retries:
                            required: true
                            type: number
                    secrets:
                        token:
                            required: true
            jobs:
                noop:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo callee
            """;

            var callerYaml = """
            on: push
            jobs:
                deploy:
                    uses: ./.github/workflows/reusable.yml
                    with:
                        retries: 3
                    secrets:
                        token: ${{ secrets.GITHUB_TOKEN }}
            """;

            File.WriteAllText(calleePath, NormalizeYaml(calleeYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            var result = new LintEngine([new ReusableWorkflowRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "reusable-workflow")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_ReusableWorkflowRule_LocalWorkflowContractValidation_NumberTypeMismatch_UsesWithPath()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-reuse-number-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var calleePath = Path.Combine(workflowsDir, "reusable.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var calleeYaml = """
            on:
                workflow_call:
                    inputs:
                        retries:
                            required: true
                            type: number
            jobs:
                noop:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo callee
            """;

            var callerYaml = """
            on: push
            jobs:
                deploy:
                    uses: ./.github/workflows/reusable.yml
                    with:
                        retries: abc
            """;

            File.WriteAllText(calleePath, NormalizeYaml(calleeYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            var result = new LintEngine([new ReusableWorkflowRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            var ruleDiagnostics = result.Diagnostics.Where(x => x.RuleId == "reusable-workflow").Select(x => x.Message).ToArray();

            // Number type error should use jobs.'<id>'.with path
            await Assert.That(ruleDiagnostics.Any(m => m.Contains("jobs.'deploy'.with", StringComparison.Ordinal) && m.Contains("expects number but got 'abc'", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_UnpinnedUsesRule_LocalReusableWorkflowWithAtRef_UsesPath()
    {
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                deploy:
                    uses: ./.github/workflows/reusable.yml@v1
            """);

        var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "unpinned-local-ref.yml");

        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "unpinned-uses").ToArray();
        await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
        // Local @ref warning should use jobs.'<id>'.uses path
        await Assert.That(diagnostics[0].Message).Contains("jobs.'deploy'.uses");
        await Assert.That(diagnostics[0].Message).Contains("must not contain '@ref'");
    }

    [Test]
    public async Task LintEngine_UnpinnedUsesRule_InvalidRemoteReusableWorkflowFormat_UsesPath()
    {
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                deploy:
                    uses: invalid-format-no-at-ref
            """);

        var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "unpinned-invalid-format.yml");

        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "unpinned-uses").ToArray();
        await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
        // Invalid format error should use jobs.'<id>'.uses path
        await Assert.That(diagnostics[0].Message).Contains("jobs.'deploy'.uses");
        await Assert.That(diagnostics[0].Message).Contains("invalid reference format");
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_UnknownAndRequiredInputs()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var actionYaml = """
            name: My action
            inputs:
                required_input:
                    required: true
                optional_input:
                    required: false
                legacy:
                    required: false
                    deprecationMessage: use optional_input instead
            runs:
              using: composite
              steps:
                - run: echo hi
                  shell: bash
            """;

            var callerYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
                          with:
                            extra_key: x
            """;

            File.WriteAllText(actionPath, NormalizeYaml(actionYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            var msgs = result.Diagnostics.Where(x => x.RuleId == "local-action-inputs").Select(x => x.Message).ToArray();
            await Assert.That(msgs.Any(m => m.Contains("unknown local action input 'extra_key'", StringComparison.Ordinal) && m.Contains("optional_input", StringComparison.Ordinal) && m.Contains("required_input", StringComparison.Ordinal))).IsTrue();
            await Assert.That(msgs.Any(m => m.Contains("required input 'required_input' is not set", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_DeprecatedInput_Warns()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-dep-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var actionYaml = """
            inputs:
                legacy:
                    required: false
                    deprecationMessage: use something else
            runs:
              using: composite
              steps:
                - run: echo hi
                  shell: bash
            """;

            var callerYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
                          with:
                            legacy: v
            """;

            File.WriteAllText(actionPath, NormalizeYaml(actionYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Severity == DiagnosticSeverity.Warning && x.Message.Contains("deprecated", StringComparison.Ordinal) && x.Message.Contains("use something else", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_Node16Runner_Error()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-node16-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "old-node");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var actionYaml = """
            runs:
              using: node16
              main: dist/index.js
            """;

            var callerYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/old-node
            """;

            File.WriteAllText(actionPath, NormalizeYaml(actionYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("deprecated runner 'node16'", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_Node20AndComposite_Allowed()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-ok-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(Path.Combine(actionsDir, "n20"));
        Directory.CreateDirectory(Path.Combine(actionsDir, "comp"));

        var actionN20 = Path.Combine(actionsDir, "n20", "action.yml");
        var actionComp = Path.Combine(actionsDir, "comp", "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            File.WriteAllText(actionN20, NormalizeYaml("""
            name: N20
            description: Node20 action
            runs:
              using: node20
              main: index.js
            """), Encoding.UTF8);

            // Create the index.js file so file-existence check passes
            File.WriteAllText(Path.Combine(actionsDir, "n20", "index.js"), "", Encoding.UTF8);

            File.WriteAllText(actionComp, NormalizeYaml("""
            name: Comp
            description: Composite action
            runs:
              using: composite
              steps:
                - run: echo ok
                  shell: bash
            """), Encoding.UTF8);

            var callerYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/n20
                        - uses: ./.github/actions/comp
            """;

            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_MissingActionFile_NoCrash()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-missing-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var callerYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/does-not-exist
            """;

            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_MissingDescription_Error()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-desc-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            File.WriteAllText(actionPath, NormalizeYaml("""
            name: No Description
            runs:
              using: composite
              steps:
                - run: echo hi
                  shell: bash
            """), Encoding.UTF8);

            File.WriteAllText(callerPath, NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
            """), Encoding.UTF8);

            var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("description is required", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_EnvNotAllowedForJsAction_Error()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-env-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            File.WriteAllText(actionPath, NormalizeYaml("""
            name: JS with env
            description: A JS action that incorrectly uses env
            runs:
              using: node20
              main: index.js
              env:
                SOME_VAR: value
            """), Encoding.UTF8);
            File.WriteAllText(Path.Combine(actionsDir, "index.js"), "", Encoding.UTF8);

            File.WriteAllText(callerPath, NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
            """), Encoding.UTF8);

            var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("\"env\" is not allowed", StringComparison.Ordinal) && x.Message.Contains("JavaScript action", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_MissingMainFile_Error()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-nofile-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            File.WriteAllText(actionPath, NormalizeYaml("""
            name: Missing Main
            description: A JS action with missing main file
            runs:
              using: node20
              main: nonexistent.js
            """), Encoding.UTF8);

            File.WriteAllText(callerPath, NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
            """), Encoding.UTF8);

            var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("does not exist", StringComparison.Ordinal) && x.Message.Contains("nonexistent.js", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_InvalidBranding_Error()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-brand-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            File.WriteAllText(actionPath, NormalizeYaml("""
            name: Bad Brand
            description: An action with bad branding
            branding:
              icon: dog
              color: neon-pink
            runs:
              using: composite
              steps:
                - run: echo ok
                  shell: bash
            """), Encoding.UTF8);

            File.WriteAllText(callerPath, NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
            """), Encoding.UTF8);

            var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("invalid branding icon", StringComparison.Ordinal))).IsTrue();
            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("invalid branding color", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_DockerEnvAllowed_NoError()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-docker-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            File.WriteAllText(actionPath, NormalizeYaml("""
            name: Docker Action
            description: A Docker action with env
            runs:
              using: docker
              image: Dockerfile
              env:
                SOME_VAR: value
            """), Encoding.UTF8);

            File.WriteAllText(callerPath, NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
            """), Encoding.UTF8);

            var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("env", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_ActionMetadataFixture_AllChecks()
    {
        // Full integration test against the testdata/examples fixture
        var root = FindRepoRoot();
        var path = Path.Combine(root, "testdata", "examples", "action_metadata_syntax_validation.yaml");
        if (!File.Exists(path))
        {
            return;
        }

        var result = new LintEngine([new LocalActionInputsRule()])
            .Check(File.ReadAllBytes(path), path);

        var msgs = result.Diagnostics.Where(x => x.RuleId == "local-action-inputs").Select(x => x.Message).ToArray();
        // 6 checks matching actionlint behavior
        await Assert.That(msgs.Any(m => m.Contains("\"env\" is not allowed", StringComparison.Ordinal))).IsTrue();
        await Assert.That(msgs.Any(m => m.Contains("description is required", StringComparison.Ordinal))).IsTrue();
        await Assert.That(msgs.Any(m => m.Contains("does not exist", StringComparison.Ordinal))).IsTrue();
        await Assert.That(msgs.Any(m => m.Contains("invalid branding color", StringComparison.Ordinal))).IsTrue();
        await Assert.That(msgs.Any(m => m.Contains("invalid branding icon", StringComparison.Ordinal))).IsTrue();
        await Assert.That(msgs.Any(m => m.Contains("invalid runs.using", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_LocalActionOutputs_StrictPropertyValidation()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-outputs-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action-with-output");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yaml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var actionYaml = """
            name: My action with output
            description: my action with outputs
            outputs:
              some_value:
                description: some value returned from this action
            runs:
              using: node20
              main: index.js
            """;

            var callerYaml = """
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - uses: ./.github/actions/my-action-with-output
                    id: my_action
                  - run: echo ${{ steps.my_action.outputs.some_value }}
                  - run: echo ${{ steps.my_action.outputs.some-value }}
            """;

            File.WriteAllText(actionPath, NormalizeYaml(actionYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            var msgs = result.Diagnostics.Where(x => x.RuleId == "expr-undefined-var").Select(x => x.Message).ToArray();
            // some_value should be valid (no error) — check that no diagnostic targets "some_value" as the undefined property
            await Assert.That(msgs.Any(m => m.Contains("property \"some_value\" is not defined", StringComparison.Ordinal))).IsFalse();
            // some-value should be flagged as undefined property
            await Assert.That(msgs.Any(m => m.Contains("\"some-value\" is not defined", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task RuleRegression_DenyInheritSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-reusable-explicit-secrets",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    secrets:
                        token: ${{ secrets.GITHUB_TOKEN }}
            """,
            []),
            new RuleCase(
            "ok-normal-job-not-target",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-reusable-secrets-inherit",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    secrets: inherit
            """,
            ["uses 'secrets: inherit'", "explicitly map only required secrets"]),
        };

        await AssertRuleCases(new DenyInheritSecretsRule(), "deny-inherit-secrets", cases);
    }

    [Test]
    public async Task RuleRegression_PermissionsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-job-scope-read",
            """
            on: push
            jobs:
                build:
                    permissions:
                        contents: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-invalid-scalar",
            """
            on: push
            permissions: admin-all
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar must be 'read-all' or 'write-all'"]),
            new RuleCase(
            "ng-job-invalid-scope",
            """
            on: push
            jobs:
                build:
                    permissions:
                        contents: admin
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"admin\" is invalid as permission of scope \"contents\". available values are \"read\", \"write\", \"none\""]),
            // regression: unknown scope name should be detected
            new RuleCase(
            "ng-unknown-scope-check",
            """
            on: push
            jobs:
                test:
                    permissions:
                        check: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["unknown permission scope \"check\". all available permission scopes are"]),
            // regression: models scope only allows read/none
            new RuleCase(
            "ng-models-write-restricted",
            """
            on: push
            jobs:
                test:
                    permissions:
                        models: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"write\" is invalid as permission of scope \"models\". available values are \"read\", \"none\""]),
            // regression: id-token scope only allows write/none
            new RuleCase(
            "ng-id-token-read-restricted",
            """
            on: push
            jobs:
                test:
                    permissions:
                        id-token: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"read\" is invalid as permission of scope \"id-token\". available values are \"write\", \"none\""]),
            // regression: vulnerability-alerts only allows read/none
            new RuleCase(
            "ng-vulnerability-alerts-write-restricted",
            """
            on: push
            jobs:
                test:
                    permissions:
                        vulnerability-alerts: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"write\" is invalid as permission of scope \"vulnerability-alerts\". available values are \"read\", \"none\""]),
            // regression: valid scopes should not produce errors
            new RuleCase(
            "ok-all-standard-scopes-valid",
            """
            on: push
            jobs:
                test:
                    permissions:
                        actions: read
                        contents: write
                        issues: none
                        packages: read
                        id-token: write
                        models: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // regression: empty permissions scalar at job level (issue170)
            new RuleCase(
            "ng-job-empty-permissions-scalar",
            """
            on: push
            jobs:
                test:
                    permissions:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"\" is invalid for permission for all the scopes. available values are \"read-all\", \"write-all\" or {}"]),
            // regression: empty permissions scalar at workflow level (issue170)
            new RuleCase(
            "ng-workflow-empty-permissions-scalar",
            """
            on: push
            permissions:
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"\" is invalid for permission for all the scopes. available values are \"read-all\", \"write-all\" or {}"]),
        };

        await AssertRuleCases(new PermissionsRule(), "permissions", cases);
    }

    [Test]
    public async Task RuleRegression_PopularActionInputsRule_TypoSuggestion()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-typo-underscore-for-hyphen",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/setup-node@v4
                          with: { node_version: '20' }
            """,
            ["unknown input 'node_version' for action 'actions/setup-node@v4'. did you mean 'node-version'?"]),
            new RuleCase(
            "ng-typo-close-misspelling",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with: { fetch-depht: 1 }
            """,
            ["unknown input 'fetch-depht' for action 'actions/checkout@v4'. did you mean 'fetch-depth'?"]),
            new RuleCase(
            "ng-no-suggestion-for-distant-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with: { totally-unknown-input: true }
            """,
            ["unknown input 'totally-unknown-input' for action 'actions/checkout@v4'"]),
        };

        await AssertRuleCases(new PopularActionInputsRule(), "popular-action-inputs", cases);
    }

    [Test]
    public async Task RuleRegression_PopularActionInputsRule_TypoAutoFix()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                          fetch-depht: 1
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new PopularActionInputsRule()]);
        var result = engine.Check(sourceBytes, "popular-action-inputs-fix.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x =>
            x.RuleId == "popular-action-inputs" && x.Message.Contains("fetch-depht", StringComparison.Ordinal));

        await Assert.That(diagnostic.Fix is not null).IsTrue();
        await Assert.That(diagnostic.Fix!.Value.Description).Contains("fetch-depth");

        var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "popular-action-inputs-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        await Assert.That(fixedText).Contains("fetch-depth: 1");
        await Assert.That(revalidated.After.Diagnostics.Any(x =>
            x.RuleId == "popular-action-inputs" && x.Message.Contains("unknown input", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task RuleRegression_PopularActionInputsRule_NoFixWhenDistant()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                          totally-unknown-input: true
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new PopularActionInputsRule()]);
        var result = engine.Check(sourceBytes, "popular-action-inputs-no-fix.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x =>
            x.RuleId == "popular-action-inputs" && x.Message.Contains("totally-unknown-input", StringComparison.Ordinal));

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task RuleRegression_PopularActionInputsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-known-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with: { fetch-depth: 1 }
            """,
            []),
            new RuleCase(
            "ng-typo-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with: { fetch-depht: 1 }
            """,
            ["unknown input 'fetch-depht' for action 'actions/checkout@v4'"]),
            new RuleCase(
            "ng-unknown-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with: { totally-unknown-input: true }
            """,
            ["unknown input 'totally-unknown-input' for action 'actions/checkout@v4'"]),
        };

        await AssertRuleCases(new PopularActionInputsRule(), "popular-action-inputs", cases);
    }

    [Test]
    public async Task RuleRegression_PopularActionInputsRule_RequiredInputs_TableDriven()
    {
        var cases = new[]
        {
            // #10: actions/cache requires 'path' and 'key' — missing both should warn
            new RuleCase(
            "ng-cache-missing-required-inputs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                            restore-keys: |
                                some-key-
            """,
            ["missing required input 'key' for action 'actions/cache@v4'", "missing required input 'path' for action 'actions/cache@v4'"]),
            // #10: actions/cache with required inputs present — no error
            new RuleCase(
            "ok-cache-all-required-inputs-present",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                            path: ~/.npm
                            key: npm-${{ runner.os }}
            """,
            []),
            // #10: actions/checkout has no required inputs without defaults — no error even with empty with
            new RuleCase(
            "ok-checkout-no-required-inputs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            []),
        };

        await AssertRuleCases(new PopularActionInputsRule(), "popular-action-inputs", cases);
    }

    [Test]
    public async Task RuleRegression_PopularActionInputsRule_DeprecatedInputs_TableDriven()
    {
        var cases = new[]
        {
            // Deprecated input for reviewdog/action-actionlint
            new RuleCase(
            "ng-deprecated-fail-on-error",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: reviewdog/action-actionlint@v1
                          with:
                            fail_on_error: true
            """,
            ["avoid using deprecated input \"fail_on_error\" in action \"reviewdog/action-actionlint@v1\": Deprecated, use `fail_level` instead"]),
            // Deprecated inputs for pypa/gh-action-pypi-publish
            new RuleCase(
            "ng-deprecated-pypa-packages-dir",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: pypa/gh-action-pypi-publish@release/v1
                          with:
                            packages_dir: /path/to/dir
                            repository_url: https://github.com/foo/bar
            """,
            [
                "avoid using deprecated input \"packages_dir\" in action \"pypa/gh-action-pypi-publish@release/v1\": The inputs have been normalized to use kebab-case. Use `packages-dir` instead",
                "avoid using deprecated input \"repository_url\" in action \"pypa/gh-action-pypi-publish@release/v1\": The inputs have been normalized to use kebab-case. Use `repository-url` instead",
            ]),
            // Non-deprecated input should not trigger warning
            new RuleCase(
            "ok-non-deprecated-input",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                            path: ~/.npm
                            key: npm-${{ runner.os }}
            """,
            []),
        };

        await AssertRuleCases(new PopularActionInputsRule(), "popular-action-inputs", cases);
    }

    [Test]
    public async Task RuleRegression_CheckoutPersistCredentialsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-checkout-persist-credentials-false",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
            """,
            []),
            new RuleCase(
            "ok-non-checkout-action",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/setup-node@v4
                          with:
                              persist-credentials: false
            """,
            []),
            new RuleCase(
            "ng-checkout-persist-credentials-missing",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            ["should set with.persist-credentials to false"]),
            new RuleCase(
            "ng-checkout-persist-credentials-true",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: true
            """,
            ["should set with.persist-credentials to false"]),
            new RuleCase(
            "ng-checkout-persist-credentials-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: ${{ inputs.persist_credentials }}
            """,
            ["should set with.persist-credentials to false"]),
        };

        await AssertRuleCases(new CheckoutPersistCredentialsRule(), "checkout-persist-credentials", cases);
    }

    [Test]
    public async Task LintEngine_CheckoutPersistCredentials_Fix_InsertsWithBlockAfterUses()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new CheckoutPersistCredentialsRule()]);
        var result = engine.Check(sourceBytes, "checkout-persist-fix-insert-with.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "checkout-persist-credentials");

        await Assert.That(diagnostic.Fix is not null).IsTrue();
        await Assert.That(diagnostic.Message.Contains("git remote set-url origin", StringComparison.Ordinal)).IsTrue();

        var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "checkout-persist-fix-insert-with.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        var withIndex = fixedText.IndexOf("with:", StringComparison.Ordinal);
        var persistIndex = fixedText.IndexOf("persist-credentials: false", StringComparison.Ordinal);
        await Assert.That(withIndex >= 0).IsTrue();
        await Assert.That(persistIndex > withIndex).IsTrue();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "checkout-persist-credentials")).IsFalse();
    }

    [Test]
    public async Task LintEngine_CheckoutPersistCredentials_Fix_InsertsMissingInputIntoExistingWithBlock()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                          fetch-depth: 1
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new CheckoutPersistCredentialsRule()]);
        var result = engine.Check(sourceBytes, "checkout-persist-fix-existing-with.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "checkout-persist-credentials");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "checkout-persist-fix-existing-with.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        var persistIndex = fixedText.IndexOf("persist-credentials: false", StringComparison.Ordinal);
        var fetchDepthIndex = fixedText.IndexOf("fetch-depth: 1", StringComparison.Ordinal);
        await Assert.That(persistIndex >= 0).IsTrue();
        await Assert.That(fetchDepthIndex > persistIndex).IsTrue();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "checkout-persist-credentials")).IsFalse();
    }

    [Test]
    public async Task LintEngine_CheckoutPersistCredentials_Fix_ReplacesTrueWithFalse()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                          persist-credentials: true
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new CheckoutPersistCredentialsRule()]);
        var result = engine.Check(sourceBytes, "checkout-persist-fix-replace.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "checkout-persist-credentials");

        await Assert.That(diagnostic.Fix is not null).IsTrue();
        await Assert.That(diagnostic.Fix!.Value.Description.Contains("git push", StringComparison.Ordinal)).IsTrue();

        var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "checkout-persist-fix-replace.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        await Assert.That(fixedText.Contains("persist-credentials: false", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("persist-credentials: true", StringComparison.Ordinal)).IsFalse();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "checkout-persist-credentials")).IsFalse();
    }

    [Test]
    public async Task LintEngine_CheckoutPersistCredentials_DoesNotAttachFix_ForExpressionOrFlowMapping()
    {
        var expressionYaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                          persist-credentials: ${{ inputs.persist_credentials }}
        """;

        var flowYaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with: { fetch-depth: 1 }
        """;

        var engine = new LintEngine([new CheckoutPersistCredentialsRule()]);
        var expressionResult = engine.Check(Encoding.UTF8.GetBytes(expressionYaml), "checkout-persist-no-fix-expression.yml");
        var flowResult = engine.Check(Encoding.UTF8.GetBytes(flowYaml), "checkout-persist-no-fix-flow.yml");

        await Assert.That(expressionResult.Diagnostics.First(x => x.RuleId == "checkout-persist-credentials").Fix is null).IsTrue();
        await Assert.That(flowResult.Diagnostics.First(x => x.RuleId == "checkout-persist-credentials").Fix is null).IsTrue();
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-action-pinned-sha",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@0123456789abcdef0123456789abcdef01234567
            """,
            []),
            new RuleCase(
            "ng-action-tag-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            ["not pinned to a full-length commit SHA"]),
            new RuleCase(
            "ng-action-missing-ref-format",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout
            """,
            ["invalid reference format", "owner/repo[/path]@ref"]),
            new RuleCase(
            "ok-local-action-reference",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/setup
            """,
            []),
            new RuleCase(
            "ng-local-action-with-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/setup@v1
            """,
            ["local action uses must not contain '@ref'"]),
            new RuleCase(
            "ok-docker-action-reference",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: docker://rhysd/actionlint:latest
            """,
            []),
            new RuleCase(
            "ok-reusable-workflow-pinned-sha",
            """
            on: push
            jobs:
                release:
                    uses: owner/repo/.github/workflows/reusable.yml@0123456789abcdef0123456789abcdef01234567
            """,
            []),
            new RuleCase(
            "ng-reusable-workflow-branch-ref",
            """
            on: push
            jobs:
                release:
                    uses: owner/repo/.github/workflows/reusable.yml@main
            """,
            ["not pinned to a full-length commit SHA"]),
            // regression: step without run/uses produces empty uses — should not trigger unpinned-uses rule
            new RuleCase(
            "ok-empty-uses-from-parser-error",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - name: broken step with no run or uses
            """,
            []),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases);
    }

    [Test]
    public async Task LintEngine_UnpinnedUsesRule_LocalActionResolution_ReportsMissingMetadata()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionDir = Path.Combine(rootDir, ".github", "actions", "setup");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionDir);

        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var callerYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/setup
            """;

            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            var result = new LintEngine([new UnpinnedUsesRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "unpinned-uses" && x.Message.Contains("missing action.yml or action.yaml", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_UnpinnedUsesRule_StepRefLocation_PointsToRefPart()
    {
        const string usesLine = "            - uses: owner/repo/action@main";
        var yaml = string.Join(
            "\n",
            "on: push",
            "jobs:",
            "    build:",
            "        runs-on: ubuntu-latest",
            "        steps:",
            usesLine,
            string.Empty);

        var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(NormalizeYaml(yaml)), "unpinned-uses-step-location.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "unpinned-uses");

        var refStartColumn = usesLine.IndexOf("@main", StringComparison.Ordinal) + 1;
        await Assert.That(diagnostic.Location.StartColumn).IsEqualTo(refStartColumn);
        await Assert.That(diagnostic.Location.EndColumn).IsEqualTo(refStartColumn + "@main".Length);
    }

    [Test]
    public async Task LintEngine_UnpinnedUsesRule_ReusableWorkflowRefLocation_PointsToRefPart()
    {
        const string usesLine = "        uses: owner/repo/.github/workflows/reusable.yml@main";
        var yaml = string.Join(
            "\n",
            "on: push",
            "jobs:",
            "    release:",
            usesLine,
            string.Empty);

        var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(NormalizeYaml(yaml)), "unpinned-uses-job-location.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "unpinned-uses");

        var refStartColumn = usesLine.IndexOf("@main", StringComparison.Ordinal) + 1;
        await Assert.That(diagnostic.Location.StartColumn).IsEqualTo(refStartColumn);
        await Assert.That(diagnostic.Location.EndColumn).IsEqualTo(refStartColumn + "@main".Length);
    }

    [Test]
    public async Task LintEngine_UnpinnedUsesRule_ReusableWorkflow_MessageIncludesUsesPath()
    {
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                release:
                    uses: owner/repo/.github/workflows/reusable.yml@main
            """);

        var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "unpinned-uses-path.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "unpinned-uses");

        // Message should include jobs.'<id>'.uses path segment
        await Assert.That(diagnostic.Message).Contains("jobs.'release'.uses");
    }

    [Test]
    public async Task RuleRegression_UnpinnedImageRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-docker-uses-pinned-digest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: docker://rhysd/actionlint@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
            """,
            []),
            new RuleCase(
            "ng-docker-uses-tag",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: docker://rhysd/actionlint:latest
            """,
            ["not pinned by digest"]),
            new RuleCase(
            "ok-job-container-pinned-digest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/example/app@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-container-tag",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/example/app:1.0.0
                    steps:
                        - run: echo ng
            """,
            ["not pinned by digest"]),
            new RuleCase(
            "ng-job-container-implicit-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/example/app
                    steps:
                        - run: echo ng
            """,
            ["not pinned by digest"]),
            new RuleCase(
            "ok-service-container-pinned-digest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        db:
                            image: postgres@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-service-container-tag",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        db:
                            image: postgres:16
                    steps:
                        - run: echo ng
            """,
            ["not pinned by digest"]),
            new RuleCase(
            "ok-non-docker-uses-is-ignored",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            []),
        };

        await AssertRuleCases(new UnpinnedImageRule(), "unpinned-image", cases);
    }

    [Test]
    public async Task RuleRegression_DangerousTriggersRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-push",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-pull-request",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-pull-request-target",
            """
            on: pull_request_target
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["potentially dangerous"]),
            new RuleCase(
            "ng-workflow-run",
            """
            on: workflow_run
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["potentially dangerous"]),
            new RuleCase(
            "ng-multiple-dangerous-triggers",
            """
            on:
                pull_request_target:
                workflow_run:
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["potentially dangerous"]),
        };

        await AssertRuleCases(new DangerousTriggersRule(), "dangerous-triggers", cases);
    }

    [Test]
    public async Task RuleRegression_JobPermissionsRequiredRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-permissions-defined",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions:
                        contents: read
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-permissions-read-all",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: read-all
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-reusable-workflow-job-with-permissions",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    permissions:
                        contents: read
            """,
            []),
            new RuleCase(
            "ng-reusable-workflow-job-no-permissions",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
            """,
            ["does not have permissions defined"]),
            new RuleCase(
            "ng-no-permissions",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["does not have permissions defined"]),
            new RuleCase(
            "ng-multiple-jobs-one-missing",
            """
            on: push
            jobs:
                ok-job:
                    runs-on: ubuntu-latest
                    permissions:
                        contents: read
                    steps:
                        - run: echo ok
                ng-job:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["does not have permissions defined"]),
        };

        await AssertRuleCases(new JobPermissionsRequiredRule(), "job-permissions-required", cases);
    }

    [Test]
    public async Task RuleRegression_NeedsGraphRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-no-needs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-needs-valid-job",
            """
            on: push
            jobs:
                setup:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo setup
                build:
                    needs: setup
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo build
            """,
            []),
            new RuleCase(
            "ok-needs-multiple-valid",
            """
            on: push
            jobs:
                setup:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo setup
                test:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo test
                deploy:
                    needs: [setup, test]
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo deploy
            """,
            []),
            new RuleCase(
            "ng-needs-unknown-job",
            """
            on: push
            jobs:
                build:
                    needs: nonexistent
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["references unknown job"]),
            new RuleCase(
            "ng-needs-one-of-multiple-unknown",
            """
            on: push
            jobs:
                setup:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo setup
                build:
                    needs: [setup, ghost]
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["references unknown job"]),
            new RuleCase(
            "ng-self-reference",
            """
            on: push
            jobs:
                build:
                    needs: build
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["cyclic dependencies in \"needs\" job configurations are detected"]),
            new RuleCase(
            "ng-two-job-cycle",
            """
            on: push
            jobs:
                a:
                    needs: b
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo a
                b:
                    needs: a
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo b
            """,
            ["cyclic dependencies in \"needs\" job configurations are detected"]),
            new RuleCase(
            "ng-three-job-cycle",
            """
            on: push
            jobs:
                a:
                    needs: b
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo a
                b:
                    needs: c
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo b
                c:
                    needs: a
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo c
            """,
            ["cyclic dependencies in \"needs\" job configurations are detected"]),
        };

        await AssertRuleCases(new NeedsGraphRule(), "needs-graph", cases);
    }

    // Duplicate job ID in needs array
    [Test]
    public async Task RuleRegression_NeedsGraphRule_DuplicateNeeds_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-duplicate-needs-id",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo build
                test:
                    runs-on: ubuntu-latest
                    needs: [build, build]
                    steps:
                        - run: echo test
            """,
            ["duplicates"]),
            new RuleCase(
            "ok-unique-needs-ids",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo build
                lint:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo lint
                test:
                    runs-on: ubuntu-latest
                    needs: [build, lint]
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "ng-duplicate-needs-case-insensitive",
            """
            on: push
            jobs:
                bar:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo bar
                foo:
                    needs: [bar, BAR]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo foo
            """,
            ["duplicates"]),
        };

        await AssertRuleCases(new NeedsGraphRule(), "needs-graph", cases);
    }

    // regression: cycle diagnostics should report at the needs value position (actionable)
    // with a cycle path in the message for clarity
    [Test]
    public async Task RuleRegression_NeedsGraphRule_CyclePosition()
    {
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                from:
                    needs: [to]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo from
                to:
                    needs: [from]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo to
            """);

        var result = new LintEngine([new NeedsGraphRule()]).Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var diags = result.Diagnostics.Where(x => x.RuleId == "needs-graph" && x.Message.Contains("cyclic")).ToArray();

        await Assert.That(diags.Length).IsGreaterThanOrEqualTo(1);

        // The cycle is reported at the first job in the cycle path (consistent with actionlint positioning).
        // DFS visits "from" first, detects cycle "from" -> "to" -> "from".
        // Report is at the first job in the cycle ("from" at line 3).
        var cycleD = diags[0];
        await Assert.That(cycleD.Location.StartLine).IsEqualTo(3);
        // Message should include cycle path
        await Assert.That(cycleD.Message).Contains("\"from\" -> \"to\" -> \"from\"");
    }

    [Test]
    public async Task RuleRegression_ShellNameRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-bash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: bash
            """,
            []),
            new RuleCase(
            "ok-pwsh",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: pwsh
            """,
            []),
            new RuleCase(
            "ok-powershell",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: powershell
            """,
            []),
            new RuleCase(
            "ok-sh",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: sh
            """,
            []),
            new RuleCase(
            "ok-cmd",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: cmd
            """,
            []),
            new RuleCase(
            "ok-python",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: print('ok')
                          shell: python
            """,
            []),
            new RuleCase(
            "ok-expression-skipped",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: ${{ inputs.shell }}
            """,
            []),
            new RuleCase(
            "ok-no-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-invalid-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
                          shell: zsh
            """,
            ["shell name", "invalid"]),
            new RuleCase(
            "ng-empty-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
                          shell: ''
            """,
            ["shell name", "invalid"]),
            new RuleCase(
            "ok-workflow-defaults-bash",
            """
            on: push
            defaults:
                run:
                    shell: bash
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-job-defaults-pwsh",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    permissions: {}
                    defaults:
                        run:
                            shell: pwsh
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-defaults-invalid-shell",
            """
            on: push
            defaults:
                run:
                    shell: zsh
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            ["shell name", "invalid"]),
            new RuleCase(
            "ng-job-defaults-invalid-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    defaults:
                        run:
                            shell: fish
                    steps:
                        - run: echo ok
            """,
            ["shell name", "invalid"]),
            new RuleCase(
            "ok-custom-shell-template-perl",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: print "ok"
                          shell: perl {0}
            """,
            []),
            new RuleCase(
            "ok-custom-shell-template-ruby",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: puts 'ok'
                          shell: ruby {0}
            """,
            []),
        };

        await AssertRuleCases(new ShellNameRule(), "shell-name", cases);
    }

    // OS-specific shell validation
    [Test]
    public async Task RuleRegression_ShellNameRule_OsSpecific_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-cmd-on-ubuntu",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
                          shell: cmd
            """,
            ["cmd", "not available on"]),
            new RuleCase(
            "ng-powershell-on-ubuntu",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
                          shell: powershell
            """,
            ["powershell", "not available on"]),
            new RuleCase(
            "ok-pwsh-on-ubuntu",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
                          shell: pwsh
            """,
            []),
            new RuleCase(
            "ok-cmd-on-windows",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    steps:
                        - run: echo ok
                          shell: cmd
            """,
            []),
            new RuleCase(
            "ng-sh-on-windows",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    steps:
                        - run: echo ng
                          shell: sh
            """,
            ["sh", "not available on"]),
            new RuleCase(
            "ok-sh-on-ubuntu",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
                          shell: sh
            """,
            []),
        };

        await AssertRuleCases(new ShellNameRule(), "shell-name", cases);
    }

    [Test]
    public async Task RuleRegression_RunnerLabelRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-ubuntu-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-windows-2022",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-2022
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-macos-14",
            """
            on: push
            jobs:
                build:
                    runs-on: macos-14
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-self-hosted-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: [self-hosted, linux, x64, custom-runner]
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-runs-on-expression-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ matrix.runner }}
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-unknown-ubuntu-label",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-9999
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["is unknown. available labels are"]),
            new RuleCase(
            "ng-unknown-mapping-label",
            """
            on: push
            jobs:
                build:
                    runs-on:
                        labels: [custom-hosted]
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["is unknown. available labels are"]),
            new RuleCase(
            "ok-mapping-labels-with-self-hosted-skip",
            """
            on: push
            jobs:
                build:
                    runs-on:
                        labels: [self-hosted, custom-hosted]
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new RunnerLabelRule(), "runner-label", cases);
    }

    // Runner label — matrix-expanded runs-on
    [Test]
    public async Task RuleRegression_RunnerLabelRule_MatrixExpanded_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-matrix-unknown-scalar",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - macos-latest
                                - linux-latest
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo test
            """,
            ["is unknown. available labels are"]),
            new RuleCase(
            "ok-matrix-known-labels-only",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - ubuntu-latest
                                - macos-latest
                                - windows-latest
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-matrix-self-hosted-array",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - [self-hosted, linux, x64]
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-matrix-self-hosted-preset-label",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - arm64
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-matrix-gpu-unknown",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - macos-latest
                                - gpu
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo test
            """,
            ["is unknown. available labels are"]),
            new RuleCase(
            "ok-matrix-expression-row-skip",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner: ${{ fromJson(needs.setup.outputs.runners) }}
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-matrix-no-strategy-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-matrix-mixed-known-and-self-hosted",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - ubuntu-latest
                                - [self-hosted, linux, x64]
                                - arm64
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-non-matrix-expression-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ github.event.inputs.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new RunnerLabelRule(), "runner-label", cases);
    }

    // Runner label conflict
    [Test]
    public async Task RuleRegression_RunnerLabelRule_OsConflict_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-mixed-os-labels",
            """
            on: push
            jobs:
                build:
                    runs-on: [ubuntu-latest, windows-latest]
                    steps:
                        - run: echo ng
            """,
            ["\"windows-latest\" conflicts with label \"ubuntu-latest\""]),
            new RuleCase(
            "ng-multiple-os-conflicts",
            """
            on: push
            jobs:
                build:
                    runs-on: [ubuntu-latest, windows-latest, macos-latest]
                    steps:
                        - run: echo ng
            """,
            ["\"windows-latest\" conflicts with label \"ubuntu-latest\"", "\"macos-latest\" conflicts with label \"ubuntu-latest\""]),
            new RuleCase(
            "ng-bare-os-label-conflict",
            """
            on: push
            jobs:
                build:
                    runs-on: [ubuntu-latest, windows]
                    steps:
                        - run: echo ng
            """,
            ["\"windows\" conflicts with label \"ubuntu-latest\""]),
            new RuleCase(
            "ok-single-os-label",
            """
            on: push
            jobs:
                build:
                    runs-on: [ubuntu-latest]
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new RunnerLabelRule(), "runner-label", cases);
    }

    // Runner label — matrix conflict with static labels
    [Test]
    public async Task RuleRegression_RunnerLabelRule_MatrixOsConflict_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-matrix-os-conflict-with-static",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [windows-latest, macos-latest]
                    runs-on: [ubuntu-latest, '${{matrix.os}}']
                    steps:
                        - run: echo ng
            """,
            ["\"windows-latest\" conflicts with label \"ubuntu-latest\"", "\"macos-latest\" conflicts with label \"ubuntu-latest\""]),
            new RuleCase(
            "ng-matrix-os-conflict-bare-label",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [windows-latest, macos-latest, windows]
                    runs-on: [ubuntu-latest, '${{matrix.os}}']
                    steps:
                        - run: echo ng
            """,
            ["\"windows-latest\" conflicts with label \"ubuntu-latest\"", "\"macos-latest\" conflicts with label \"ubuntu-latest\"", "\"windows\" conflicts with label \"ubuntu-latest\""]),
            new RuleCase(
            "ok-matrix-same-os-family",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [ubuntu-22.04, ubuntu-24.04]
                    runs-on: [ubuntu-latest, '${{matrix.os}}']
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new RunnerLabelRule(), "runner-label", cases);
    }

    [Test]
    public async Task RuleRegression_RunnerNoLatestRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-ubuntu-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["moving latest label"]),
            new RuleCase(
            "ng-windows-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    steps:
                        - run: echo ng
            """,
            ["moving latest label"]),
            new RuleCase(
            "ng-macos-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: macos-latest
                    steps:
                        - run: echo ng
            """,
            ["moving latest label"]),
            new RuleCase(
            "ok-version-pinned-label",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-24.04
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-self-hosted-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: [self-hosted, linux, x64]
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-runs-on-expression-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new RunnerNoLatestRule(), "runner-no-latest", cases);
    }

    [Test]
    public async Task RuleRegression_IdNamingRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-valid-job-and-step-ids",
            """
            on: push
            jobs:
                Build_123:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: setup-1
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-no-step-id",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-step-id-expression-skipped",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: ${{ matrix.step_id }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-id-with-space",
            """
            on: push
            jobs:
                "bad id":
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid job ID", "must start with a letter"]),
            new RuleCase(
            "ng-step-id-with-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: setup.v1
                          run: echo ng
            """,
            ["invalid step ID", "must start with a letter"]),
            new RuleCase(
            "ng-step-id-empty",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: ''
                          run: echo ng
            """,
            ["step ID should not be empty"]),
            new RuleCase(
            "ng-job-id-starts-with-digit",
            """
            on: push
            jobs:
                1build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid job ID", "must start with a letter"]),
            new RuleCase(
            "ng-step-id-starts-with-dash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: -setup
                          run: echo ng
            """,
            ["invalid step ID", "must start with a letter"]),
            new RuleCase(
            "ng-step-id-duplicate-case-insensitive",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: BuildStep
                          run: echo first
                        - id: buildstep
                          run: echo second
            """,
            ["duplicated in the same job", "case-insensitive"]),
        };

        await AssertRuleCases(new IdNamingRule(), "id-naming", cases);
    }

    [Test]
    public async Task RuleRegression_DispatchInputsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-choice-with-options-and-default",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: choice
                            options: [dev, prod]
                            default: dev
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-choice-without-options",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: choice
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["type 'choice' must define non-empty options"]),
            new RuleCase(
            "ng-choice-duplicate-options",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: choice
                            options: [dev, dev]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["has duplicated option"]),
            new RuleCase(
            "ng-choice-default-not-in-options",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: choice
                            options: [dev, prod]
                            default: staging
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["default value 'staging'", "not included in options"]),
            new RuleCase(
            "ng-non-choice-has-options",
            """
            on:
                workflow_dispatch:
                    inputs:
                        count:
                            type: number
                            options: [1, 2]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["has options but type is"]),
            new RuleCase(
            "ng-number-default-not-number",
            """
            on:
                workflow_dispatch:
                    inputs:
                        count:
                            type: number
                            default: NaNValue
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["is not a valid number"]),
            new RuleCase(
            "ng-boolean-default-invalid",
            """
            on:
                workflow_dispatch:
                    inputs:
                        force:
                            type: boolean
                            default: yes
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["must be 'true' or 'false'"]),
            new RuleCase(
            "ng-more-than-25-inputs",
            """
            on:
                workflow_dispatch:
                    inputs:
                        i01: { type: string }
                        i02: { type: string }
                        i03: { type: string }
                        i04: { type: string }
                        i05: { type: string }
                        i06: { type: string }
                        i07: { type: string }
                        i08: { type: string }
                        i09: { type: string }
                        i10: { type: string }
                        i11: { type: string }
                        i12: { type: string }
                        i13: { type: string }
                        i14: { type: string }
                        i15: { type: string }
                        i16: { type: string }
                        i17: { type: string }
                        i18: { type: string }
                        i19: { type: string }
                        i20: { type: string }
                        i21: { type: string }
                        i22: { type: string }
                        i23: { type: string }
                        i24: { type: string }
                        i25: { type: string }
                        i26: { type: string }
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["maximum number of inputs", "25 but 26"]),
        };

        await AssertRuleCases(new DispatchInputsRule(), "dispatch-inputs", cases);
    }

    // Workflow call input default validation
    [Test]
    public async Task RuleRegression_WorkflowCallInputDefaultRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-boolean-input-non-bool-default",
            """
            on:
                workflow_call:
                    inputs:
                        debug:
                            type: boolean
                            default: "yes"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["boolean", "default"]),
            new RuleCase(
            "ng-number-input-non-number-default",
            """
            on:
                workflow_call:
                    inputs:
                        retries:
                            type: number
                            default: "three"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["number", "default"]),
            new RuleCase(
            "ok-boolean-input-true-default",
            """
            on:
                workflow_call:
                    inputs:
                        debug:
                            type: boolean
                            default: true
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-string-input-any-default",
            """
            on:
                workflow_call:
                    inputs:
                        name:
                            type: string
                            default: "hello"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-required-input-with-default",
            """
            on:
                workflow_call:
                    inputs:
                        path:
                            type: string
                            required: true
                            default: ""
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["default", "required"]),
            new RuleCase(
            "ok-required-input-without-default",
            """
            on:
                workflow_call:
                    inputs:
                        path:
                            type: string
                            required: true
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new WorkflowCallInputDefaultRule(), "workflow-call-input-default", cases);
    }

    [Test]
    public async Task RuleRegression_OutdatedActionRunnerRule_TableDriven()
    {
        // This rule is catalog-driven and version-aware: it checks the popular actions catalog
        // for deprecated runner versions. Actions with maxDeprecatedMajorVersion in the catalog
        // are flagged when the referenced version is at or below that threshold.
        var cases = new[]
        {
            new RuleCase(
            "ok-latest-version-node20",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            []),
            new RuleCase(
            "ng-outdated-checkout-v3",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v3
            """,
            ["too old to run"]),
            new RuleCase(
            "ng-outdated-checkout-v2",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v2
            """,
            ["too old to run"]),
            new RuleCase(
            "ok-unknown-action-not-in-catalog",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: some/action@v1
            """,
            []),
            new RuleCase(
            "ok-sha-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@a5ac7e51b41094c92402da3b24376905380afc29
            """,
            []),
            new RuleCase(
            "ok-docker-login-current",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: docker/login-action@v3
            """,
            []),
            new RuleCase(
            "ng-docker-login-v2",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: docker/login-action@v2
            """,
            ["too old to run"]),
        };

        await AssertRuleCases(new OutdatedActionRunnerRule(), "outdated-action-runner", cases);
    }

    [Test]
    public async Task RuleRegression_ScheduleEventRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-valid-cron",
            """
            on:
                schedule:
                    - cron: "*/5 * * * *"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-invalid-cron-syntax",
            """
            on:
                schedule:
                    - cron: "* * * *"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["cron", "invalid", "exactly 5 fields"]),
            new RuleCase(
            "ng-cron-too-frequent",
            """
            on:
                schedule:
                    - cron: "* * * * *"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["runs too frequently", "once per", "once every 5 minutes"]),
            new RuleCase(
            "ng-invalid-timezone",
            """
            on:
                schedule:
                    - cron: "0 0 * * *"
                      timezone: "Mars/Phobos"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["timezone", "invalid"]),
            new RuleCase(
            "ng-iana-like-invalid-timezone",
            """
            on:
                schedule:
                    - cron: "0 0 * * *"
                      timezone: "Asia/Somewhere"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["timezone", "invalid"]),
            new RuleCase(
            "ng-empty-timezone",
            """
            on:
                schedule:
                    - cron: "0 0 * * *"
                      timezone: ""
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["timezone", "must not be empty"]),
            new RuleCase(
            "ng-empty-cron",
            """
            on:
                schedule:
                    - cron: ""
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["cron", "must not be empty"]),
        };

        await AssertRuleCases(new ScheduleEventRule(), "schedule-event", cases);
    }

    [Test]
    public async Task RuleRegression_GlobPatternRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-valid-branch-and-path-glob",
            """
            on:
                pull_request:
                    branches: [main, release/**]
                    paths: ['src/**', '!docs/**']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-expression-skipped",
            """
            on:
                push:
                    branches:
                        - ${{ github.ref_name }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-triple-star-in-branches",
            """
            on:
                push:
                    branches: ['feature/***']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "consecutive '*'"]),
            new RuleCase(
            "ng-unclosed-class-in-paths-ignore",
            """
            on:
                pull_request:
                    paths-ignore:
                        - 'src/[abc'
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "missing ]"]),
        };

        await AssertRuleCases(new GlobPatternRule(), "glob-pattern", cases);
    }

    // Glob pattern syntax validation
    [Test]
    public async Task RuleRegression_GlobPatternRule_Syntax_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-reversed-bracket-range",
            """
            on:
                push:
                    branches: ['feature/[z-a]']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["start of range", "is larger than end of range"]),
            new RuleCase(
            "ng-dot-dot-path-segment",
            """
            on:
                push:
                    paths: ['src/../etc/passwd']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["'.' and '..' are not allowed"]),
            new RuleCase(
            "ng-caret-char-in-branch-pattern",
            """
            on:
                push:
                    branches: ['^foo-']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["character '^' is invalid"]),
            new RuleCase(
            "ng-star-plus-in-tag-pattern",
            """
            on:
                push:
                    tags: ['v*+']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["unexpected character '+' after '*'"]),
            new RuleCase(
            "ng-dot-path-segment",
            """
            on:
                push:
                    paths: ['./foo/bar.txt']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["'.' and '..' are not allowed"]),
            new RuleCase(
            "ok-valid-bracket-range",
            """
            on:
                push:
                    branches: ['release/v[0-9].*']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-backslash-regex-escape-in-tags",
            """
            on:
                push:
                    tags: ['v\d+']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid for branch and tag names", "can be escaped"]),
            new RuleCase(
            "ng-trailing-backslash-in-branches",
            """
            on:
                push:
                    branches: ["feature\\"]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "trailing backslash"]),
            new RuleCase(
            "ok-valid-backslash-escape-star",
            """
            on:
                push:
                    branches: ['feature/\*']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-lone-bang-in-tags",
            """
            on:
                push:
                    tags: ['!']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["at least one character must follow !"]),
            new RuleCase(
            "ng-glob-errors-detected-after-null-entry-in-paths",
            """
            on:
                push:
                    paths:
                        -
                        - '!'
                        - '  foo'
                        - '.'
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["at least one character must follow !", "leading and trailing spaces", "'.' and '..' are not allowed"]),
            new RuleCase(
            "ng-leading-space-in-paths",
            """
            on:
                push:
                    paths: ['  foo']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["leading and trailing spaces"]),
            new RuleCase(
            "ng-trailing-space-in-paths",
            """
            on:
                push:
                    paths: ['foo  ']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["leading and trailing spaces"]),
            new RuleCase(
            "ng-space-only-in-paths",
            """
            on:
                push:
                    paths: [' ']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["leading and trailing spaces"]),
            new RuleCase(
            "ok-space-in-branches-is-ref-error",
            """
            on:
                push:
                    branches: [' ']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid for branch and tag names"]),
            new RuleCase(
            "ng-ref-starts-with-slash",
            """
            on:
                push:
                    tags: ['/v1.0']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["ref name must not start with /"]),
            new RuleCase(
            "ng-ref-ends-with-slash",
            """
            on:
                push:
                    branches: ['feature/']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["ref name must not end with /"]),
        };

        await AssertRuleCases(new GlobPatternRule(), "glob-pattern", cases);
    }

    [Test]
    public async Task RuleRegression_GlobPatternRule_SnapshotVersion_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-unclosed-bracket-in-snapshot-version",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    snapshot:
                        image-name: my-image
                        version: 'v[0-'
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "missing ]"]),
            new RuleCase(
            "ok-valid-snapshot-version",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    snapshot:
                        image-name: my-image
                        version: 'v1.2.3'
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new GlobPatternRule(), "glob-pattern", cases);
    }

    [Test]
    public async Task RuleRegression_GlobPatternRule_ImageVersionVersions_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-unclosed-bracket-in-image-version-versions",
            """
            on:
                image_version:
                    versions:
                        - 'v[0-'
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "missing ]"]),
            new RuleCase(
            "ng-lone-bang-in-image-version-versions",
            """
            on:
                image_version:
                    versions:
                        - '!'
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["at least one character must follow !"]),
        };

        await AssertRuleCases(new GlobPatternRule(), "glob-pattern", cases);
    }

    [Test]
    public async Task GlobPatternRule_BlockScalarTrailingNewline_ReportsAtIndicatorLine()
    {
        // MISS #6: block scalar `- |\n  foo.txt` should report at the `|` indicator line,
        // not at the content line.
        // Layout:
        //   line 5: "      - |"           <- `|` at col 9
        //   line 6: "        foo.txt"     <- content at col 9
        // actionlint expects line 5, col 9
        var yaml = "on:\n  push:\n    paths:\n      - 'ok'\n      - |\n        foo.txt\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n";
        var result = new LintEngine([new GlobPatternRule()]).Check(
            System.Text.Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "glob-pattern" && d.Message.Contains("leading and trailing spaces")).ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(1);
        // Must report at block scalar indicator line, not content line
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(5);
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(9);
    }

    [Test]
    public async Task RuleRegression_DenyWriteAllRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-workflow-read-all",
            """
            on: push
            permissions: read-all
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-job-scopes-only",
            """
            on: push
            jobs:
                build:
                    permissions:
                        contents: write
                        actions: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-write-all",
            """
            on: push
            permissions: write-all
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'write-all' is forbidden"]),
            new RuleCase(
            "ng-job-write-all",
            """
            on: push
            jobs:
                build:
                    permissions: write-all
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'write-all' is forbidden"]),
        };

        await AssertRuleCases(new DenyWriteAllRule(), "deny-write-all", cases);
    }

    [Test]
    public async Task LintEngine_DenyWriteAll_Fix_ReplacesValueAndClearsDiagnostic()
    {
        var yaml = """
        on: push
        permissions: 'write-all'
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new DenyWriteAllRule()]);
        var result = engine.Check(sourceBytes, "deny-write-all-fix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "deny-write-all");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "deny-write-all-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        // Workflow-level write-all should be fixed to {} (drop permissions), not read-all
        await Assert.That(fixedText.Contains("{}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("read-all", StringComparison.Ordinal)).IsFalse();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsFalse();
    }

    [Test]
    public async Task LintEngine_DenyWriteAll_Fix_JobLevel_ReplacesWithEmptyMapping()
    {
        var yaml = """
        on: push
        jobs:
            build:
                permissions: write-all
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new DenyWriteAllRule()]);
        var result = engine.Check(sourceBytes, "deny-write-all-job-fix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "deny-write-all");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "deny-write-all-job-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        // Job-level write-all should be fixed to {} (drop permissions)
        await Assert.That(fixedText.Contains("{}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("write-all", StringComparison.Ordinal)).IsFalse();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsFalse();
    }

    [Test]
    public async Task RuleRegression_DenyReadAllRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-workflow-explicit-scopes",
            """
            on: push
            permissions:
                contents: read
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-job-write-all-not-target",
            """
            on: push
            jobs:
                build:
                    permissions: write-all
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-read-all",
            """
            on: push
            permissions: read-all
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'read-all' is forbidden"]),
            new RuleCase(
            "ng-job-read-all",
            """
            on: push
            jobs:
                build:
                    permissions: read-all
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'read-all' is forbidden"]),
        };

        await AssertRuleCases(new DenyReadAllRule(), "deny-read-all", cases);
    }

    [Test]
    public async Task LintEngine_DenyReadAll_Fix_ReplacesReadAllWithExplicitMappingBaseline()
    {
        var yaml = """
        on: push
        permissions: 'read-all'
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new DenyReadAllRule()]);
        var result = engine.Check(sourceBytes, "deny-read-all-fix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "deny-read-all");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "deny-read-all-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        // Workflow-level read-all should be fixed to {}
        await Assert.That(fixedText.Contains("{}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "deny-read-all")).IsFalse();
    }

    [Test]
    public async Task LintEngine_DenyReadAll_Fix_JobLevel_ReplacesWithEmptyMapping()
    {
        var yaml = """
        on: push
        jobs:
            build:
                permissions: read-all
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new DenyReadAllRule()]);
        var result = engine.Check(sourceBytes, "deny-read-all-job-fix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "deny-read-all");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "deny-read-all-job-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        // Job-level read-all should be fixed to {} (drop permissions)
        await Assert.That(fixedText.Contains("{}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("read-all", StringComparison.Ordinal)).IsFalse();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "deny-read-all")).IsFalse();
    }

    [Test]
    public async Task RuleRegression_JobTimeoutMinutesRequiredRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-job-timeout-present",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    timeout-minutes: 15
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-step-timeout-on-all-steps",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - timeout-minutes: 3
                          run: echo ok
                        - timeout-minutes: 5
                          uses: actions/checkout@v4
            """,
            []),
            new RuleCase(
            "ok-reusable-workflow-call-not-target",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
            """,
            []),
            new RuleCase(
            "ng-missing-job-and-step-timeouts",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
                        - uses: actions/checkout@v4
            """,
            ["must define timeout-minutes", "set timeout-minutes on every step"]),
        };

        await AssertRuleCases(new JobTimeoutMinutesRequiredRule(), "job-timeout-minutes-required", cases);
    }

    [Test]
    public async Task LintEngine_JobTimeoutMinutesRequired_Fix_AttachesWhenDefaultTimeoutConfigured()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobTimeoutMinutesRequiredRule()]);
        var config = new LintConfig
        {
            Fix = new FixConfig { Enabled = true, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 15 } },
        };

        var result = engine.Check(sourceBytes, "job-timeout-minutes-required-fix.yml", config);
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-timeout-minutes-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "job-timeout-minutes-required-fix.yml", [diagnostic], config);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        await Assert.That(fixedText.Contains("timeout-minutes: 15", StringComparison.Ordinal)).IsTrue();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "job-timeout-minutes-required")).IsFalse();
    }

    [Test]
    public async Task LintEngine_JobTimeoutMinutesRequired_Fix_DoesNotAttachWhenDefaultTimeoutMissing()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobTimeoutMinutesRequiredRule()]);
        var result = engine.Check(sourceBytes, "job-timeout-minutes-required-no-fix.yml", new LintConfig());
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-timeout-minutes-required");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task RuleRegression_GitHubAppTokenInputsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-non-target-action",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            []),
            new RuleCase(
            "ok-actions-create-token-with-repositories-and-permission-prefix",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
                          with:
                              repositories: repo-a,repo-b
                              permission-contents: read
            """,
            []),
            new RuleCase(
            "ok-actions-create-token-current-repo-default-with-permission-prefix",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
                          with:
                              permission-contents: read
            """,
            []),
            new RuleCase(
            "ng-missing-permission-constraint-only-when-create-token-uses-current-repo-default",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
            """,
            ["permission constraints"]),
            new RuleCase(
            "ng-missing-repository-constraint-when-owner-set",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
                          with:
                              owner: ${{ github.repository_owner }}
                              permission-issues: write
            """,
            ["repository constraints"]),
            new RuleCase(
            "ng-missing-both-constraints-when-owner-set",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
                          with:
                              owner: ${{ github.repository_owner }}
            """,
            ["repository and permission constraints"]),
        };

        await AssertRuleCases(new GitHubAppTokenInputsRule(), "github-app-token-inputs", cases);
    }

    [Test]
    public async Task RuleRegression_WorkflowSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-single-job-workflow-exception",
            """
            on: push
            env:
                GITHUB_TOKEN: ${{ github.token }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-multi-job-non-secret-env",
            """
            on: push
            env:
                NORMAL_VALUE: plain
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
            []),
            new RuleCase(
            "ng-multi-job-github-token-in-workflow-env",
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
            ["must not set secrets.* or github.token", "move secret mapping to job/step env"]),
            new RuleCase(
            "ng-multi-job-secrets-in-workflow-env",
            """
            on: push
            env:
                DATADOG_API_KEY: ${{ secrets.DATADOG_API_KEY }}
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
            ["must not set secrets.* or github.token", "DATADOG_API_KEY"]),
        };

        await AssertRuleCases(new WorkflowSecretsRule(), "workflow-secrets", cases);
    }

    [Test]
    public async Task RuleRegression_JobSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-single-step-job-exception",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        GITHUB_TOKEN: ${{ github.token }}
                    steps:
                        - run: echo only-step
            """,
            []),
            new RuleCase(
            "ok-multi-step-non-secret-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        NORMAL_VALUE: plain
                    steps:
                        - run: echo first
                        - run: echo second
            """,
            []),
            new RuleCase(
            "ng-multi-step-github-token-in-job-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        GITHUB_TOKEN: ${{ github.token }}
                    steps:
                        - run: echo first
                        - run: echo second
            """,
            ["must not set secrets.* or github.token", "step env"]),
            new RuleCase(
            "ng-multi-step-secrets-in-job-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        DATADOG_API_KEY: ${{ secrets.DATADOG_API_KEY }}
                    steps:
                        - run: echo first
                        - run: echo second
            """,
            ["must not set secrets.* or github.token", "DATADOG_API_KEY"]),
        };

        await AssertRuleCases(new JobSecretsRule(), "job-secrets", cases);
    }

    [Test]
    public async Task RuleRegression_ActionShellIsRequiredRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
                        "ok-action-run-with-shell",
                        """
                        name: Sample action
                        runs:
                            using: composite
                            steps:
                                - run: echo hello
                                    shell: bash
                        """,
                        []),
                        new RuleCase(
                        "ok-run-with-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
                          shell: bash
            """,
            []),
            new RuleCase(
            "ok-action-step-no-run",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            []),
            new RuleCase(
            "ok-workflow-run-without-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
            """,
            []),
            new RuleCase(
            "ok-workflow-run-with-empty-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
                          shell: ""
            """,
            []),
        };

        await AssertRuleCases(new ActionShellIsRequiredRule(), "action-shell-is-required", cases);
    }

    [Test]
    public async Task RuleRegression_CachePoisoningRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-cache-on-trusted-trigger",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            []),
            new RuleCase(
            "ng-cache-on-pull-request",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            ["cache action", "untrusted triggers"]),
            new RuleCase(
            "ng-cache-restore-on-workflow-run",
            """
            on: workflow_run
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache/restore@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            ["cache action", "untrusted triggers"]),
        };

        await AssertRuleCases(new CachePoisoningRule(), "cache-poisoning", cases);
    }

    [Test]
    public async Task RuleRegression_SelfHostedRunnerRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-self-hosted-on-push",
            """
            on: push
            jobs:
                build:
                    runs-on: self-hosted
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-self-hosted-on-pull-request",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: self-hosted
                    steps:
                        - run: echo ok
            """,
            ["self-hosted runner", "untrusted triggers"]),
            new RuleCase(
            "ng-self-hosted-on-workflow-run",
            """
            on: workflow_run
            jobs:
                build:
                    runs-on:
                        - self-hosted
                        - linux
                    steps:
                        - run: echo ok
            """,
            ["self-hosted runner", "untrusted triggers"]),
            new RuleCase(
            "ng-self-hosted-message-has-runs-on-path",
            """
            on: pull_request
            jobs:
                ci:
                    runs-on: self-hosted
                    steps:
                        - run: echo ok
            """,
            ["jobs.'ci'.runs-on"]),
        };

        await AssertRuleCases(new SelfHostedRunnerRule(), "self-hosted-runner", cases);
    }

    [Test]
    public async Task RuleRegression_UnredactedSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-non-secret-env-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        VERSION: 1.2.3
                    steps:
                        - run: echo "${VERSION}"
            """,
            []),
            new RuleCase(
            "ng-secret-derived-env-echo",
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
            ["secret-derived variable", "without masking"]),
            new RuleCase(
            "ng-secret-derived-env-write-host",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    env:
                        TOKEN: ${{ secrets.GITHUB_TOKEN }}
                    steps:
                        - shell: pwsh
                          run: Write-Host "$env:TOKEN"
            """,
            ["secret-derived variable", "without masking"]),
        };

        await AssertRuleCases(new UnredactedSecretsRule(), "unredacted-secrets", cases);
    }

    [Test]
    public async Task LintEngine_UnredactedSecrets_DiagnosticLocation_PointsToRunExpression_NotFollowingEnvKey()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - name: called secret
                      run: |
                        echo "called secret. ${APPLES}"
                      env:
                        APPLES: ${{ secrets.APPLES }}
        """;

        var result = new LintEngine([new UnredactedSecretsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "unredacted-secrets-location.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "unredacted-secrets");

        var highlightedText = yaml.Split('\n')[diagnostic.Location.StartLine - 1].Trim();
        await Assert.That(highlightedText.Contains("${APPLES}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(highlightedText.StartsWith("echo", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task RuleRegression_SecretsOutsideEnvRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-secret-in-env-handoff",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        TOKEN: ${{ secrets.GITHUB_TOKEN }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-secret-in-step-if",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ secrets.GITHUB_TOKEN != '' }}
                          run: echo ng
            """,
            ["step.if", "secrets context"]),
            new RuleCase(
            "ok-secret-in-action-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/github-script@v7
                          with:
                              script: ${{ secrets.GITHUB_TOKEN }}
            """,
            []),
            new RuleCase(
            "ok-secret-in-create-github-app-token-inputs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
                          with:
                              app-id: ${{ secrets.APP_ID }}
                              private-key: ${{ secrets.PRIVATE_KEY }}
            """,
            []),
        };

        await AssertRuleCases(new SecretsOutsideEnvRule(), "secrets-outside-env", cases);
    }

    [Test]
    public async Task RuleRegression_MatrixRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-small-matrix",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                            node: [20]
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-empty-axis",
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
            ["strategy.matrix axis 'os' has no values"]),
            new RuleCase(
            "ok-include-new-axis",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                            include:
                                - arch: x64
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-include-mixed-existing-and-new-axes",
            """
            on: push
            jobs:
                dispatch:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            repo: [guitarrapc/testtest]
                            include:
                                - repo: guitarrapc/testtest
                                  ref: main
                                  workflow: test
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-exclude-unknown-axis",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                            exclude:
                                - arch: x64
                    steps:
                        - run: echo ng
            """,
            ["strategy.matrix.exclude references unknown axis 'arch'"]),
        };

        await AssertRuleCases(new MatrixRule(), "matrix", cases);
    }

    // Matrix duplicate value + exclude mismatch
    [Test]
    public async Task RuleRegression_MatrixRule_DuplicateValues_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-duplicate-axis-value",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-20.04, ubuntu-22.04, ubuntu-20.04]
                    steps:
                        - run: echo ng
            """,
            ["duplicate"]),
            new RuleCase(
            "ok-unique-axis-values",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-20.04, ubuntu-22.04, ubuntu-24.04]
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new MatrixRule(), "matrix", cases);
    }

    [Test]
    public async Task RuleRegression_MatrixRule_ExcludeValueMismatch_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-scalar-value-mismatch",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            node: [10, 12, 14]
                            os: [ubuntu-latest, macos-latest]
                            exclude:
                                - node: 13
                                  os: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["does not match in matrix \"node\" combinations"]),
            new RuleCase(
            "ok-scalar-value-matches",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            node: [10, 12, 14]
                            os: [ubuntu-latest, macos-latest]
                            exclude:
                                - node: 10
                                  os: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-exclude-value-is-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            foo: [aaa]
                            exclude:
                                - foo: ${{ fromJSON('"x"') }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-row-value-is-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            foo:
                                - ${{ fromJSON('{"bar":"x"}') }}
                            exclude:
                                - foo: bar
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-include-only-axis-value-mismatch",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                            include:
                                - os: ubuntu-latest
                                  gui: gnome
                            exclude:
                                - os: ubuntu-latest
                                  gui: kde
                    steps:
                        - run: echo ng
            """,
            ["does not match in matrix \"gui\" combinations"]),
            new RuleCase(
            "ok-include-only-axis-value-matches",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                            include:
                                - os: ubuntu-latest
                                  gui: gnome
                            exclude:
                                - os: ubuntu-latest
                                  gui: gnome
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new MatrixRule(), "matrix", cases);
    }

    [Test]
    public async Task RuleRegression_MatrixRule_ExcludeObjectValueReportsAtValueLine()
    {
        // Object value in exclude: diagnostic must point to the exclude entry line, not the matrix range
        var yaml = """
            on: push
            jobs:
                build:
                    runs-on: ${{ matrix.os.runner }}
                    strategy:
                        matrix:
                            os:
                                - {'runner': 'ubuntu-latest'}
                            exclude:
                                - os: {'runner': 'windows-latest'}
                    steps:
                        - run: echo ng
            """
            .Replace("\r\n", "\n");
        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "exclude-obj.yml");
        var diag = result.Diagnostics.FirstOrDefault(d => d.Message.Contains("does not match"));
        await Assert.That(diag.Message).IsNotNull();
        // The exclude entry is on line 10-11 area — diagnostic must not be on line 7 (matrix range)
        await Assert.That(diag.Location.StartLine).IsGreaterThanOrEqualTo(10);
    }

    [Test]
    public async Task RuleRegression_MatrixRule_ExcludeArrayValueReportsAtValueLine()
    {
        // Array value in exclude: diagnostic must point to the exclude entry line, not the matrix range
        var yaml = """
            on: push
            jobs:
                build:
                    runs-on: ${{ matrix.os[0] }}
                    strategy:
                        matrix:
                            os:
                                - ['ubuntu', 'latest']
                            exclude:
                                - os: ['macos', 'latest']
                    steps:
                        - run: echo ng
            """
            .Replace("\r\n", "\n");
        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "exclude-arr.yml");
        var diag = result.Diagnostics.FirstOrDefault(d => d.Message.Contains("does not match"));
        await Assert.That(diag.Message).IsNotNull();
        // The exclude entry is on line 10-11 area — diagnostic must not be on line 7 (matrix range)
        await Assert.That(diag.Location.StartLine).IsGreaterThanOrEqualTo(10);
    }

    [Test]
    public async Task RuleRegression_EnvVarRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-portable-env-keys",
            """
            on: push
            env:
                GLOBAL_TOKEN: x
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        JOB_TOKEN_1: x
                    steps:
                        - env:
                              STEP_TOKEN: x
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-env-key-lowercase",
            """
            on: push
            env:
                github_token: x
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["workflow.env key 'github_token' is not portable"]),
            new RuleCase(
            "ng-step-env-key-dash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                              TOKEN-NAME: x
                          run: echo ng
            """,
            ["step.env key 'TOKEN-NAME' is not portable"]),
        };

        await AssertRuleCases(new EnvVarRule(), "env-var", cases);
    }

    [Test]
    public async Task RuleRegression_DeprecatedCommandsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-modern-output-file",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "result=ok" >> "$GITHUB_OUTPUT"
            """,
            []),
            new RuleCase(
            "ng-set-output-command",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "::set-output name=result::ok"
            """,
            ["workflow command \"set-output\" was deprecated", "$GITHUB_OUTPUT"]),
            new RuleCase(
            "ng-set-env-command",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "::set-env name=TOKEN::x"
            """,
            ["workflow command \"set-env\" was deprecated", "$GITHUB_ENV"]),
            // regression: multi-line run script should report all deprecated commands
            new RuleCase(
            "ng-multiline-multiple-deprecated",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: |
                            echo "::set-output name=foo::bar"
                            echo "::set-env name=TOKEN::x"
            """,
            ["workflow command \"set-output\" was deprecated", "workflow command \"set-env\" was deprecated"]),
        };

        await AssertRuleCases(new DeprecatedCommandsRule(), "deprecated-commands", cases);
    }

    [Test]
    public async Task RuleRegression_IfCondRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-dynamic-condition",
            """
            on: push
            jobs:
                build:
                    if: ${{ github.ref != '' }}
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ success() }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-if-constant-false",
            """
            on: push
            jobs:
                build:
                    if: ${{ false }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["constant expression \"false\" in condition. remove the if: section"]),
            new RuleCase(
            "ng-step-if-constant-true",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ !false }}
                          run: echo ng
            """,
            ["constant expression \"!false\" in condition. remove the if: section"]),
            new RuleCase(
            "ng-step-if-always-true-multi-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ github.event_name == 'push' }} && ${{ github.ref_name == 'main' }}
                          run: echo ng
            """,
            ["always evaluated to true because extra characters are around"]),
            new RuleCase(
            "ng-step-if-always-true-trailing-space",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: "${{ github.event_name == 'push' }} "
                          run: echo ng
            """,
            ["always evaluated to true because extra characters are around"]),
            new RuleCase(
            "ok-step-if-bare-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: github.event_name == 'push'
                          run: echo ok
            """,
            []),
            // regression: null literal should be detected as constant (falsy)
            new RuleCase(
            "ng-step-if-null-literal",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ null }}
                          run: echo ng
            """,
            ["constant expression \"null\" in condition. remove the if: section"]),
            // regression: number literal should be detected as constant (0 = falsy)
            new RuleCase(
            "ng-step-if-number-zero",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ 0 }}
                          run: echo ng
            """,
            ["constant expression \"0\" in condition. remove the if: section"]),
            // regression: non-zero number is truthy
            new RuleCase(
            "ng-step-if-number-truthy",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ 42 }}
                          run: echo ng
            """,
            ["constant expression \"42\" in condition. remove the if: section"]),
            // regression: empty string literal is falsy
            new RuleCase(
            "ng-step-if-empty-string",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ '' }}
                          run: echo ng
            """,
            ["constant expression \"''\" in condition. remove the if: section"]),
            // regression: non-empty string literal is truthy
            new RuleCase(
            "ng-step-if-nonempty-string",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ 'hello' }}
                          run: echo ng
            """,
            ["constant expression \"'hello'\" in condition. remove the if: section"]),
            // regression: mixed type constant expression (true && 42 || !null)
            new RuleCase(
            "ng-step-if-mixed-constant",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: true && 42 || !null
                          run: echo ng
            """,
            ["constant expression \"true && 42 || !null\" in condition. remove the if: section"]),
            // regression: pure function with constant args (contains + format)
            new RuleCase(
            "ng-step-if-constant-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ contains(format('{0} {1} {2}', 'foo', 'bar', 'piyo'), 'o b') }}
                          run: echo ng
            """,
            ["constant expression"]),
            // ok case — impure function (success) should not be flagged
            new RuleCase(
            "ok-step-if-impure-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ contains(github.event.head_commit.message, 'skip') }}
                          run: echo ok
            """,
            []),
            // regression: trailing whitespace in bare constant should be trimmed in message text
            new RuleCase(
            "ng-step-if-constant-trailing-space",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: 'true '
                          run: echo ng
            """,
            ["constant expression \"true\" in condition. remove the if: section"]),
            // regression: leading whitespace in bare constant should be trimmed in message text
            new RuleCase(
            "ng-step-if-constant-leading-space",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ' false'
                          run: echo ng
            """,
            ["constant expression \"false\" in condition. remove the if: section"]),
            // regression: block scalar newline in constant should be trimmed in message text
            new RuleCase(
            "ng-step-if-constant-block-scalar",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - if: |\n          true\n        run: echo ng\n",
            ["constant expression \"true\" in condition. remove the if: section"]),
            // regression: snapshot.if constant should be detected
            new RuleCase(
            "ng-snapshot-if-constant",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    snapshot:
                        image-name: test
                        if: true
                    steps:
                        - run: echo ng
            """,
            ["constant expression \"true\" in condition. remove the if: section"]),
        };

        await AssertRuleCases(new IfCondRule(), "if-cond", cases);
    }

    [Test]
    public async Task IfCondRule_BlockScalarConstant_ReportsAtIfKeyLine()
    {
        // MISS #7: block scalar `if: |\n  true` should report at the `if:` value line (where `|` is),
        // not at the content line (where `true` is).
        // Layout:
        //   line 6: "      - if: |"       <- `if` at col 9, `|` at col 13
        //   line 7: "          true"       <- content at col 11
        // actionlint expects line 6, col 13 (the `|` position)
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - if: |\n          true\n        run: echo ng\n";
        var result = new LintEngine([new IfCondRule()]).Check(
            System.Text.Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(1);
        await Assert.That(diagnostics[0].Message).Contains("constant expression \"true\"");
        // Must report at block scalar indicator line, not content line
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(6);
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(13);
    }

    [Test]
    public async Task IfCondRule_BlockScalarAlwaysTrue_ReportsAtIfKeyLine()
    {
        // MISS #8: block scalar `if: |\n  ${{ false }}` should report at the `if:` value line,
        // not at the content line.
        // Layout:
        //   line 6: "      - if: |"              <- `|` at col 13
        //   line 7: "          ${{ false }}"      <- content at col 11
        // actionlint expects line 6, col 13
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - if: |\n          ${{ false }}\n        run: echo ng\n";
        var result = new LintEngine([new IfCondRule()]).Check(
            System.Text.Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(1);
        await Assert.That(diagnostics[0].Message).Contains("always evaluated to true");
        // Must report at block scalar indicator line, not content line
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(6);
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(13);
    }

    [Test]
    public async Task IfCondRule_BlockScalarJobIf_ReportsAtIfKeyLine()
    {
        // Block scalar job-level `if: |\n  true` should also report at the `|` position.
        // Layout:
        //   line 4: "    if: |"      <- `if` at col 5, `|` at col 9
        //   line 5: "      true"     <- content at col 7
        var yaml = "on: push\njobs:\n  build:\n    if: |\n      true\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ng\n";
        var result = new LintEngine([new IfCondRule()]).Check(
            System.Text.Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(1);
        await Assert.That(diagnostics[0].Message).Contains("constant expression \"true\"");
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(4);
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(9);
    }

    [Test]
    public async Task RuleRegression_FakeTernaryRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-boolean-short-circuit",
            """
            on: push
            jobs:
                build:
                    if: ${{ (github.event_name == 'push' && success()) || failure() }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-if-fake-ternary",
            """
            on: push
            jobs:
                build:
                    if: ${{ github.ref_name == 'main' && 'prod' || 'dev' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["avoid fake ternary pattern 'cond && a || b'", "case expression"]),
            new RuleCase(
            "ng-step-if-fake-ternary",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.deploy && 'yes' || 'no' }}
                          run: echo ng
            """,
            ["avoid fake ternary pattern 'cond && a || b'", "explicit branching"]),
        };

        await AssertRuleCases(new FakeTernaryRule(), "fake-ternary", cases);
    }

    [Test]
    public async Task RuleRegression_ArchivedUsesRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-active-action",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            []),
            new RuleCase(
            "ng-archived-action-repo",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions-rs/toolchain@v1
            """,
            ["archived repository", "actions-rs/toolchain"]),
            new RuleCase(
            "ng-archived-reusable-workflow-repo",
            """
            on: push
            jobs:
                reuse:
                    uses: actions-rs/cargo/.github/workflows/reuse.yml@v1
            """,
            ["archived repository", "actions-rs/cargo"]),
        };

        await AssertRuleCases(new ArchivedUsesRule(), "archived-uses", cases);
    }

    [Test]
    public async Task RuleRegression_InsecureCommandsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-unrelated-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        LOG_LEVEL: debug
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-env-unsecure-commands",
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
            ["ACTIONS_ALLOW_UNSECURE_COMMANDS", "migrate to environment files"]),
            new RuleCase(
            "ng-step-env-unsecure-commands",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            ACTIONS_ALLOW_UNSECURE_COMMANDS: "yes"
                          run: echo ng
            """,
            ["ACTIONS_ALLOW_UNSECURE_COMMANDS", "migrate to environment files"]),
        };

        await AssertRuleCases(new InsecureCommandsRule(), "insecure-commands", cases);
    }

    [Test]
    public async Task RuleRegression_OverprovisionedSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-single-secret-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            TOKEN: ${{ secrets.GITHUB_TOKEN }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-two-step-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            TOKEN: ${{ secrets.GITHUB_TOKEN }}
                            API_KEY: ${{ secrets.API_KEY }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-multiple-step-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            TOKEN: ${{ secrets.GITHUB_TOKEN }}
                            API_KEY: ${{ secrets.API_KEY }}
                            SECRET_KEY: ${{ secrets.SECRET_KEY }}
                            PRIVATE_KEY: ${{ secrets.PRIVATE_KEY }}
                            APP_ID: ${{ secrets.APP_ID }}
                            DEPLOY_KEY: ${{ secrets.DEPLOY_KEY }}
                          run: echo ng
            """,
            ["more than 5 secret values", "minimum required"]),
            new RuleCase(
            "ok-five-job-secrets",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@v1
                    secrets:
                        token: ${{ secrets.GITHUB_TOKEN }}
                        api_key: ${{ secrets.API_KEY }}
                        secret_key: ${{ secrets.SECRET_KEY }}
                        private_key: ${{ secrets.PRIVATE_KEY }}
                        app_id: ${{ secrets.APP_ID }}
            """,
            []),
            new RuleCase(
            "ng-reusable-call-many-secrets",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@v1
                    secrets:
                        token: ${{ secrets.GITHUB_TOKEN }}
                        api_key: ${{ secrets.API_KEY }}
                        secret_key: ${{ secrets.SECRET_KEY }}
                        private_key: ${{ secrets.PRIVATE_KEY }}
                        app_id: ${{ secrets.APP_ID }}
                        deploy_key: ${{ secrets.DEPLOY_KEY }}
            """,
            ["passes 6 explicit secrets", "minimum required secrets"]),
        };

        await AssertRuleCases(new OverprovisionedSecretsRule(), "overprovisioned-secrets", cases);
    }

    [Test]
    public async Task RuleRegression_ForbiddenUsesRule_TableDriven()
    {
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["forbidden-uses"] = new RuleConfig
                {
                    Allow = ["bad-org/safe-action"],
                    Deny = ["bad-org/*"],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-allowed-by-exception",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: bad-org/safe-action@v1
            """,
            []),
            new RuleCase(
            "ng-deny-policy-hit",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: bad-org/unsafe-action@v1
            """,
            ["denied by forbidden-uses policy", "bad-org/unsafe-action"]),
            new RuleCase(
            "ng-reusable-workflow-deny",
            """
            on: push
            jobs:
                reuse:
                    uses: bad-org/reusable/.github/workflows/reuse.yml@v1
            """,
            ["denied by forbidden-uses policy", "bad-org/reusable"]),
        };

        await AssertRuleCases(new ForbiddenUsesRule(), "forbidden-uses", cases, config);
    }

    [Test]
    public async Task RuleRegression_RefVersionMismatchRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-matching-major",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: owner/action-v2@v2.1.0
            """,
            []),
            new RuleCase(
            "ng-repo-major-mismatch",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: owner/action-v1@v2.0.0
            """,
            ["major version 'v2' mismatches", "path version hint 'v1'"]),
            new RuleCase(
            "ng-workflow-path-major-mismatch",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/release-v1.yml@v3
            """,
            ["major version 'v3' mismatches", "path version hint 'v1'"]),
        };

        await AssertRuleCases(new RefVersionMismatchRule(), "ref-version-mismatch", cases);
    }

    [Test]
    public async Task RuleRegression_UseTrustedPublishingRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-publish-with-id-token-write",
            """
            on: push
            jobs:
                publish:
                    permissions:
                        id-token: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: npm publish
            """,
            []),
            new RuleCase(
            "ng-npm-publish-without-id-token",
            """
            on: push
            jobs:
                publish:
                    runs-on: ubuntu-latest
                    steps:
                        - run: npm publish
            """,
            ["publish-like command detected", "trusted publishing"]),
            new RuleCase(
            "ng-twine-upload-without-id-token",
            """
            on: push
            jobs:
                publish:
                    runs-on: ubuntu-latest
                    steps:
                        - run: twine upload dist/*
            """,
            ["publish-like command detected", "id-token: write"]),
        };

        await AssertRuleCases(new UseTrustedPublishingRule(), "use-trusted-publishing", cases);
    }

    [Test]
    public async Task RuleRegression_CredentialsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-no-host-image",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: node:20
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-public-registry-without-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/owner/app:latest
                    services:
                        cache:
                            image: docker.io/library/redis:7
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-additional-public-registries-without-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: registry.k8s.io/pause:3.10
                    services:
                        a:
                            image: quay.io/org/app:1
                        b:
                            image: mcr.microsoft.com/dotnet/runtime:8.0
                        c:
                            image: cgr.dev/chainguard/wolfi-base:latest
                        d:
                            image: nvcr.io/nvidia/pytorch:24.01-py3
                        e:
                            image: registry.access.redhat.com/ubi9/ubi:latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-private-registry-with-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: registry.example.com/team/app:1.0.0
                        credentials:
                            username: ${{ secrets.REG_USER }}
                            password: ${{ secrets.REG_PASS }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-container-private-without-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: registry.example.com/team/app:1.0.0
                    steps:
                        - run: echo ng
            """,
            ["credentials are not configured", "registry.example.com"]),
            new RuleCase(
            "ng-service-private-without-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        db:
                            image: private.example.org/team/db:15
                    steps:
                        - run: echo ng
            """,
            ["credentials are not configured", "private.example.org"]),
            new RuleCase(
            "ng-hardcoded-password-in-container",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: 'example.com/owner/image'
                        credentials:
                            username: user
                            password: pass
                    steps:
                        - run: echo ng
            """,
            ["\"password\" section in \"container\" section should be specified via secrets"]),
            new RuleCase(
            "ng-hardcoded-password-in-service",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        redis:
                            image: redis
                            credentials:
                                username: user
                                password: pass
                    steps:
                        - run: echo ng
            """,
            ["\"password\" section in \"redis\" service should be specified via secrets"]),
            new RuleCase(
            "ok-password-via-secrets-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: 'example.com/owner/image'
                        credentials:
                            username: ${{ secrets.REG_USER }}
                            password: ${{ secrets.REG_PASS }}
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new CredentialsRule(), "credentials", cases);
    }

    [Test]
    public async Task RuleRegression_TemplateInjectionRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-run-with-safe-expression",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.ref }}"
            """,
            []),
            new RuleCase(
            "ok-run-without-expression",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
            """,
            []),
            new RuleCase(
            "ng-run-uses-github-event-pull-request-title",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.pull_request.title }}"
            """,
            ["\"github.event.pull_request.title\" is potentially untrusted"]),
            new RuleCase(
            "ok-env-maps-github-event-comment-body",
            """
            on: issue_comment
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            COMMENT_BODY: ${{ github.event.comment.body }}
                          run: echo "$COMMENT_BODY"
            """,
            []),
            new RuleCase(
            "ng-run-uses-bracket-event-access",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github['event'].pull_request.title }}"
            """,
            ["\"github.event.pull_request.title\" is potentially untrusted"]),
            new RuleCase(
            "ok-run-uses-github-event-number-not-leaf",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.number }}"
            """,
            []),
            new RuleCase(
            "ng-run-uses-github-head-ref",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.head_ref }}"
            """,
            ["\"github.head_ref\" is potentially untrusted"]),
            new RuleCase(
            "ok-safe-function-contains-untrusted-input",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ contains(github.event.issue.title, 'bug') }}"
            """,
            []),
            new RuleCase(
            "ok-safe-function-startswith-untrusted-input",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ startsWith(github.event.pull_request.head.ref, 'feature/') }}"
            """,
            []),
            new RuleCase(
            "ng-unsafe-function-format-untrusted-input",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ format('{0}', github.event.issue.title) }}"
            """,
            ["\"github.event.issue.title\" is potentially untrusted"]),
            new RuleCase(
            "ng-github-script-with-untrusted-input",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/github-script@v7
                          with:
                            script: console.log('${{ github.event.head_commit.author.name }}')
            """,
            ["\"github.event.head_commit.author.name\" is potentially untrusted"]),
            new RuleCase(
            "ok-github-script-with-safe-expression",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/github-script@v7
                          with:
                            script: console.log('${{ github.ref }}')
            """,
            []),
            new RuleCase(
            "ok-action-input-not-github-script",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/stale@v9
                          with:
                            stale-pr-message: ${{ github.event.pull_request.title }} was closed
            """,
            []),
            new RuleCase(
            "ng-run-with-object-filter-untrusted",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo '${{ toJSON(github.event.*.body) }}'
            """,
            ["is potentially untrusted"]),
        };

        await AssertRuleCases(new TemplateInjectionRule(), "template-injection", cases);
    }

    // Template injection — position precision & per-reference reporting

    [Test]
    public async Task RuleRegression_TemplateInjectionRule_PerReferenceReporting_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-single-untrusted-reference-names-path",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.head_commit.message }}"
            """,
            ["\"github.event.head_commit.message\" is potentially untrusted"]),
            new RuleCase(
            "ng-nested-untrusted-reports-all-three",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ github.event.pages[github.event.commits[github.event.issue.title].author.name].page_name }}
            """,
            [
                "\"github.event.pages.*.page_name\" is potentially untrusted",
                "\"github.event.commits.*.author.name\" is potentially untrusted",
                "\"github.event.issue.title\" is potentially untrusted",
            ]),
            new RuleCase(
            "ng-two-expressions-in-one-run",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.head_commit.message }}" and "${{ github.head_ref }}"
            """,
            [
                "\"github.event.head_commit.message\" is potentially untrusted",
                "\"github.head_ref\" is potentially untrusted",
            ]),
            new RuleCase(
            "ng-github-script-names-path",
            """
            on: issues
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/github-script@v7
                          with:
                            script: console.log('${{ github.event.head_commit.author.name }}')
            """,
            ["\"github.event.head_commit.author.name\" is potentially untrusted"]),
        };

        await AssertRuleCases(new TemplateInjectionRule(), "template-injection", cases);
    }

    [Test]
    public async Task RuleRegression_TemplateInjectionRule_PositionPrecision()
    {
        // actionlint expects 6:41 for: echo "Checking commit '${{ github.event.head_commit.message }}'"
        // Col 41 = start of "github" inside the expression body
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "Checking commit '${{ github.event.head_commit.message }}'"
            """);
        var result = new LintEngine([new TemplateInjectionRule()]).Check(
            System.Text.Encoding.UTF8.GetBytes(yaml), "position-test.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "template-injection").ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(1);
        await Assert.That(diagnostics[0].Message).Contains("github.event.head_commit.message");

        // The untrusted reference starts at the "g" of "github" inside the expression
        var line6 = yaml.Split('\n')[5]; // 0-based index for line 6
        var expectedCol = line6.IndexOf("github.event.head_commit.message", StringComparison.Ordinal) + 1; // 1-based
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(6);
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(expectedCol);
    }

    [Test]
    public async Task RuleRegression_TemplateInjectionRule_NestedUntrustedPositions()
    {
        // actionlint expects 7:23, 7:42, 7:63 for nested untrusted references
        var yaml = NormalizeYaml("""
            name: Test
            on: pull_request
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ github.event.pages[github.event.commits[github.event.issue.title].author.name].page_name }}
            """);
        var result = new LintEngine([new TemplateInjectionRule()]).Check(
            System.Text.Encoding.UTF8.GetBytes(yaml), "nested-test.yml");
        var diagnostics = result.Diagnostics
            .Where(x => x.RuleId == "template-injection")
            .OrderBy(x => x.Location.StartColumn)
            .ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(3);

        // All on line 7
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(7);
        await Assert.That(diagnostics[1].Location.StartLine).IsEqualTo(7);
        await Assert.That(diagnostics[2].Location.StartLine).IsEqualTo(7);

        // Check messages name correct paths
        await Assert.That(diagnostics[0].Message).Contains("github.event.pages.*.page_name");
        await Assert.That(diagnostics[1].Message).Contains("github.event.commits.*.author.name");
        await Assert.That(diagnostics[2].Message).Contains("github.event.issue.title");

        // Verify column positions
        var line7 = yaml.Split('\n')[6]; // 0-based for line 7
        var col1 = line7.IndexOf("github.event.pages[", StringComparison.Ordinal) + 1;
        var col2 = line7.IndexOf("github.event.commits[", StringComparison.Ordinal) + 1;
        var col3 = line7.IndexOf("github.event.issue.title", StringComparison.Ordinal) + 1;
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(col1);
        await Assert.That(diagnostics[1].Location.StartColumn).IsEqualTo(col2);
        await Assert.That(diagnostics[2].Location.StartColumn).IsEqualTo(col3);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-step-if-uses-steps-context",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: prep
                          run: echo ok
                        - if: ${{ steps.prep.outcome == 'success' }}
                          run: echo next
            """,
            []),
            new RuleCase(
            "ok-step-with-safe-context",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                            repository: ${{ github.repository }}
            """,
            []),
            new RuleCase(
            "ng-job-if-uses-steps-context",
            """
            on: push
            jobs:
                build:
                    if: ${{ steps.prep.outcome == 'success' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["context \"steps\" is not allowed here"]),
            new RuleCase(
            "ng-job-if-uses-strategy-context",
            """
            on: push
            jobs:
                build:
                    if: ${{ strategy.fail-fast }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["context \"strategy\" is not allowed here"]),
            new RuleCase(
            "ng-job-if-uses-matrix-context",
            """
            on: push
            jobs:
                build:
                    if: ${{ matrix.os == 'ubuntu-latest' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["context \"matrix\" is not allowed here"]),
            new RuleCase(
            "ng-job-if-uses-secrets-context",
            """
            on: push
            jobs:
                build:
                    if: ${{ secrets.TOKEN != '' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["context \"secrets\" is not allowed here"]),
            new RuleCase(
            "ng-step-if-uses-secrets-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ secrets.TOKEN != '' }}
                          run: echo ng
            """,
            ["context \"secrets\" is not allowed here"]),
            new RuleCase(
            "ok-step-run-uses-secrets-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ secrets.TOKEN }}
            """,
            []),
            new RuleCase(
            "ok-step-env-uses-secrets-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            TOKEN: ${{ secrets.TOKEN }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-step-if-uses-unknown-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ foobar.value == 'x' }}
                          run: echo ng
            """,
            ["undefined context \"foobar\""]),
            new RuleCase(
            "ng-step-env-uses-unknown-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            DATA: ${{ unknown.payload }}
                          run: echo "$DATA"
            """,
            ["undefined context \"unknown\""]),
            new RuleCase(
            "ng-step-with-uses-unknown-context",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                            repository: ${{ unknown.repository }}
            """,
            ["undefined context \"unknown\""]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_EnvKeyExpression_TableDriven()
    {
        var cases = new[]
        {
            // env key with valid runner property — should only get portability warning (from EnvVarRule, not here)
            new RuleCase(
            "ok-env-key-valid-runner-property",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hi
                          env:
                            ${{ runner.name }}: ''
            """,
            []),
            // env key with invalid runner property — should report property not defined
            new RuleCase(
            "ng-container-env-key-invalid-property",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: node:14.16
                        env:
                            ${{ runner.foooooo }}: ''
                    steps:
                        - run: echo hi
            """,
            ["property \"foooooo\" is not defined in object type"]),
            // job env key with invalid runner property
            new RuleCase(
            "ng-job-env-key-invalid-property",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        ${{ runner.fooooooo }}: ''
                    steps:
                        - run: echo hi
            """,
            ["property \"fooooooo\" is not defined in object type"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_InputDefaultTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            // ok: boolean default with boolean expression
            new RuleCase(
            "ok-bool-default-bool-expr",
            """
            on:
              workflow_call:
                inputs:
                  input1:
                    type: boolean
                  input2:
                    type: boolean
                    default: ${{ inputs.input1 }}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """,
            []),
            // ok: number default with number expression
            new RuleCase(
            "ok-number-default-number-expr",
            """
            on:
              workflow_call:
                inputs:
                  input1:
                    type: number
                  input2:
                    type: number
                    default: ${{ inputs.input1 }}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """,
            []),
            // ng: boolean input with string expression
            new RuleCase(
            "ng-bool-default-string-expr",
            """
            on:
              workflow_call:
                inputs:
                  input1:
                    type: string
                  input2:
                    type: boolean
                    default: ${{ inputs.input1 }}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ng
            """,
            ["type of input \"input2\" must be bool but found type string"]),
            // ng: number input with string expression
            new RuleCase(
            "ng-number-default-string-expr",
            """
            on:
              workflow_call:
                inputs:
                  input1:
                    type: string
                  input2:
                    type: number
                    default: ${{ inputs.input1 }}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ng
            """,
            ["type of input \"input2\" must be number but found type string"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ContextAvailability4C_TableDriven()
    {
        var cases = new[]
        {
            // 4.C-A: workflow_call output value should check root context availability
            new RuleCase(
            "ng-workflow-call-output-value-env-not-allowed",
            """
            on:
              workflow_call:
                outputs:
                  result:
                    value: ${{ env.FOO }}
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),

            new RuleCase(
            "ok-workflow-call-output-value-jobs-context",
            """
            on:
              workflow_call:
                outputs:
                  result:
                    value: ${{ jobs.build.outputs.foo }}
            jobs:
              build:
                runs-on: ubuntu-latest
                outputs:
                  foo: bar
                steps:
                  - run: echo ok
            """,
            []),

            // 4.C-B: snapshot.if should be checked for context availability
            new RuleCase(
            "ng-snapshot-if-env-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                snapshot:
                  image-name: my-image
                  if: ${{ env.FOO == 'foo' }}
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),

            new RuleCase(
            "ng-snapshot-if-runner-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                snapshot:
                  image-name: my-image
                  if: ${{ runner.name == 'foo' }}
                steps:
                  - run: echo ok
            """,
            ["context \"runner\" is not allowed here"]),

            new RuleCase(
            "ng-snapshot-if-secrets-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                snapshot:
                  image-name: my-image
                  if: ${{ secrets.FOO == 'foo' }}
                steps:
                  - run: echo ok
            """,
            ["context \"secrets\" is not allowed here"]),

            new RuleCase(
            "ok-snapshot-if-strategy-matrix-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    foo: [a, b]
                snapshot:
                  image-name: my-image
                  if: ${{ matrix.foo == 'a' && strategy.fail-fast }}
                steps:
                  - run: echo ok
            """,
            []),

            // 4.C-C: service entrypoint/command should be checked for context availability
            new RuleCase(
            "ng-service-entrypoint-env-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                services:
                  nginx:
                    image: nginx
                    entrypoint: ${{ env.FOO }}
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),

            new RuleCase(
            "ng-service-command-env-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                services:
                  nginx:
                    image: nginx
                    command: ${{ env.FOO }}
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),

            new RuleCase(
            "ok-service-entrypoint-github-context",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                services:
                  nginx:
                    image: nginx
                    entrypoint: ${{ github.actor }}
                steps:
                  - run: echo ok
            """,
            []),

            // Services expression form: env context should not be allowed
            new RuleCase(
            "ng-services-expression-env-not-allowed",
            """
            on:
              workflow_call:
                inputs:
                  bool:
                    type: boolean
            jobs:
              build:
                runs-on: ubuntu-latest
                services: ${{ inputs.bool || env.FOO }}
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_DynamicContext_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-step-accesses-known-step-id",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: prep
                          run: echo ok
                        - if: ${{ steps.prep.outcome == 'success' }}
                          run: echo next
            """,
            []),
            new RuleCase(
            "ok-step-accesses-known-matrix-key",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                    steps:
                        - run: echo ${{ matrix.os }}
            """,
            []),
            new RuleCase(
            "ok-step-accesses-known-needs-job",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo build
                test:
                    runs-on: ubuntu-latest
                    needs: [build]
                    steps:
                        - run: echo ${{ needs.build.result }}
            """,
            []),
            new RuleCase(
            "ok-step-accesses-known-workflow-call-input",
            """
            on:
                workflow_call:
                    inputs:
                        environment:
                            type: string
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ inputs.environment }}
            """,
            []),
            new RuleCase(
            "ok-matrix-no-rows-loose-object-no-error",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            include:
                                - os: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.os }}
            """,
            []),
            new RuleCase(
            "ng-step-accesses-unknown-step-id",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: prep
                          run: echo ok
                        - if: ${{ steps.nonexistent.outcome == 'success' }}
                          run: echo next
            """,
            ["\"nonexistent\" is not defined in object type"]),
            new RuleCase(
            "ng-step-accesses-unknown-matrix-key",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                    steps:
                        - env:
                            VALUE: ${{ matrix.unknown_key }}
                          run: echo "$VALUE"
            """,
            ["\"unknown_key\" is not defined in object type"]),
            new RuleCase(
            "ng-step-accesses-unknown-needs-job",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo build
                test:
                    runs-on: ubuntu-latest
                    needs: [build]
                    steps:
                        - env:
                            RESULT: ${{ needs.nonexistent.outputs.foo }}
                          run: echo "$RESULT"
            """,
            ["\"nonexistent\" is not defined in object type"]),
            new RuleCase(
            "ng-step-accesses-unknown-workflow-call-input",
            """
            on:
                workflow_call:
                    inputs:
                        environment:
                            type: string
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ inputs.unknown_param }}
                          run: echo "$VAL"
            """,
            ["\"unknown_param\" is not defined in object type"]),
            // regression: matrix include-only axis keys should be accessible
            new RuleCase(
            "ok-matrix-include-only-axis-accessible",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                            node: [14, 15]
                            include:
                                - node: 15
                                  npm: 7.5.4
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo ${{ matrix.os }}
                        - run: echo ${{ matrix.node }}
                        - run: echo ${{ matrix.npm }}
            """,
            []),
            // regression: include-only matrix (no row axes) should resolve keys
            new RuleCase(
            "ok-matrix-include-only-no-rows",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            include:
                                - os: ubuntu-latest
                                  version: 1
                                - os: windows-latest
                                  version: 2
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo ${{ matrix.version }}
            """,
            []),
            // regression: step env with expression scalar should not error
            new RuleCase(
            "ok-step-env-expression-scalar",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            env_object:
                                - FOO: BAR
                                - FOO: PIYO
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "$FOO"
                          env: ${{ matrix.env_object }}
            """,
            []),
            // A-3: matrix nested object property access — known property should be fine
            new RuleCase(
            "ok-matrix-nested-object-property",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            package:
                                - name: 'foo'
                                  optional: true
                                - name: 'bar'
                                  optional: false
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.package.name }}
            """,
            []),
            // A-3: matrix nested object — unknown property should error
            new RuleCase(
            "ng-matrix-nested-object-unknown-property",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            package:
                                - name: 'foo'
                                  optional: true
                                - name: 'bar'
                                  optional: false
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.package.dev }}
            """,
            ["\"dev\" is not defined"]),
            // A-3: matrix undefined axis (no such key at all)
            new RuleCase(
            "ng-matrix-undefined-axis",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo ${{ matrix.platform }}
            """,
            ["\"platform\" is not defined in object type"]),
            // A-3: empty matrix in other job — matrix should be strict empty
            new RuleCase(
            "ng-matrix-empty-in-other-job",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo test
                other:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.os }}
            """,
            ["\"os\" is not defined in object type"]),
            // A-19: popular action output — known output should be fine
            new RuleCase(
            "ok-popular-action-known-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          id: cache
                          with:
                            key: ${{ hashFiles('**/*.lock') }}
                            path: ./packages
                        - run: echo ${{ steps.cache.outputs.cache-hit }}
            """,
            []),
            // A-19: popular action output — typo should be flagged
            new RuleCase(
            "ng-popular-action-unknown-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          id: cache
                          with:
                            key: ${{ hashFiles('**/*.lock') }}
                            path: ./packages
                        - run: echo ${{ steps.cache.outputs.cache_hit }}
            """,
            ["\"cache_hit\" is not defined"]),
            // regression: github.event.inputs.unknown should be flagged for workflow_dispatch
            new RuleCase(
            "ng-github-event-inputs-unknown-property",
            """
            on:
              workflow_dispatch:
                inputs:
                  myinput:
                    type: string
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo "${{ github.event.inputs.select }}"
            """,
            ["\"select\" is not defined"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ComparisonTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-bool-input-greater-than-number",
            """
            on:
                workflow_call:
                    inputs:
                        timeout:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.timeout > 60 }}
                          run: echo timeout
            """,
            ["bool value cannot be compared to number value with '>' operator"]),
            new RuleCase(
            "ok-number-input-less-than-number",
            """
            on:
                workflow_call:
                    inputs:
                        count:
                            type: number
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.count < 100 }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-string-input-equals-string",
            """
            on:
                workflow_call:
                    inputs:
                        env:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.env == 'production' }}
                          run: echo deploy
            """,
            []),
            new RuleCase(
            "ng-bool-input-less-or-equal-number",
            """
            on:
                workflow_call:
                    inputs:
                        verbose:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.verbose <= 5 }}
                          run: echo ok
            """,
            ["bool value cannot be compared to number value with '<=' operator"]),
            new RuleCase(
            "ng-bool-input-greater-or-equal-number",
            """
            on:
                workflow_call:
                    inputs:
                        flag:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.flag >= 1 }}
                          run: echo ok
            """,
            ["bool value cannot be compared to number value with '>=' operator"]),
            new RuleCase(
            "ng-bool-input-not-equals-number",
            """
            on:
                workflow_call:
                    inputs:
                        flag:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.flag != 60 }}
                          run: echo ok
            """,
            ["bool value cannot be compared to number value with '!=' operator"]),
            new RuleCase(
            "ok-string-input-not-equals-string",
            """
            on:
                workflow_call:
                    inputs:
                        env:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.env != 'staging' }}
                          run: echo deploy
            """,
            []),
            new RuleCase(
            "ok-any-input-greater-than-number",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ github.event.number > 0 }}
                          run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_TemplateTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-step-env-object-in-template",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ fromJson('{"a":1}') }}
                          run: echo "$VAL"
            """,
            ["{a: number} value in ${{ }}"]),
            new RuleCase(
            "ng-step-env-null-in-template",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ null }}
                          run: echo "$VAL"
            """,
            ["null value in ${{ }}"]),
            new RuleCase(
            "ok-step-if-object-no-template-warning",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ fromJson('{"a":1}') }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-step-env-string-in-template",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ github.ref }}
                          run: echo "$VAL"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_MatrixArrayTemplateTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-matrix-array-in-template",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            bar:
                                - [42]
                                - [true]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.bar }}
            """,
            ["array value in ${{ }}"]),
            new RuleCase(
            "ok-matrix-array-element-access",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            bar:
                                - [42]
                                - [true]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.bar[0] }}
            """,
            []),
            new RuleCase(
            "ok-matrix-mixed-types-any",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            foo:
                                - 'string value'
                                - 42
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.foo }}
            """,
            []),
            new RuleCase(
            "ng-matrix-object-in-template",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            obj:
                                - { a: 1, b: 2 }
                                - { a: 3, b: 4 }
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.obj }}
            """,
            ["{a: number; b: number} value in ${{ }}"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_EnvMappingTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-env-string-as-mapping",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            env_string:
                                - 'FOO=BAR'
                                - 'FOO=PIYO'
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "$FOO"
                          env: ${{ matrix.env_string }}
            """,
            ["cannot be expanded as mapping"]),
            new RuleCase(
            "ok-env-object-as-mapping",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            env_object:
                                - FOO: BAR
                                - FOO: PIYO
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "$FOO"
                          env: ${{ matrix.env_object }}
            """,
            []),
            new RuleCase(
            "ok-env-any-as-mapping",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "$FOO"
                          env: ${{ fromJson('{"FOO":"bar"}') }}
            """,
            []),
            new RuleCase(
            "ng-env-array-as-mapping",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            arr:
                                - [1, 2]
                                - [3, 4]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo test
                          env: ${{ matrix.arr }}
            """,
            ["cannot be expanded as mapping"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_CredentialsObjectTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-credentials-fromjson-object",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    container:
                        image: ubuntu:latest
                        credentials: ${{ fromJSON('{}') }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-credentials-string-expression",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    container:
                        image: ubuntu:latest
                        credentials: ${{ 'username:password' }}
                    steps:
                        - run: echo
            """,
            ["type of expression at \"credentials\" must be object but found type string"]),
            new RuleCase(
            "ng-services-string-expression",
            """
            on: push
            jobs:
                test:
                    services: ${{ 'redis' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["type of expression at \"services\" must be object but found type string"]),
            new RuleCase(
            "ok-services-fromjson-object",
            """
            on: push
            jobs:
                test:
                    services: ${{ fromJSON('{}') }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-service-credentials-string-expression",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    services:
                        redis:
                            image: redis:latest
                            credentials: ${{ 'user:pass' }}
                    steps:
                        - run: echo
            """,
            ["type of expression at \"credentials\" must be object but found type string"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_IndexTypeCheckWithOverrides_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-bool-index-on-object",
            """
            on:
                workflow_dispatch:
                    inputs:
                        verbose:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ env[inputs.verbose] }}"
            """,
            ["index of object must be string, but got bool"]),
            new RuleCase(
            "ng-number-index-on-object",
            """
            on:
                workflow_dispatch:
                    inputs:
                        age:
                            type: number
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ env[inputs.age] }}"
            """,
            ["index of object must be string, but got number"]),
            new RuleCase(
            "ok-string-index-on-object",
            """
            on:
                workflow_dispatch:
                    inputs:
                        name:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ env[inputs.name] }}"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_SecretsResolution_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-workflow-call-secret-known",
            """
            on:
                workflow_call:
                    secrets:
                        DEPLOY_KEY:
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            KEY: ${{ secrets.DEPLOY_KEY }}
                          run: echo "$KEY"
            """,
            []),
            new RuleCase(
            "ng-workflow-call-secret-unknown",
            """
            on:
                workflow_call:
                    secrets:
                        DEPLOY_KEY:
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            KEY: ${{ secrets.UNKNOWN_SECRET }}
                          run: echo "$KEY"
            """,
            ["\"UNKNOWN_SECRET\" is not defined in object type"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    // Contextual Validation

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_NeedsOutputValidation_TableDriven()
    {
        var cases = new[]
        {
            // #8: needs.build.outputs.built should be detected as undefined when build has no such output
            new RuleCase(
            "ng-needs-unknown-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        image_tag: ${{ steps.build.outputs.tag }}
                    steps:
                        - id: build
                          run: echo "tag=v1" >> $GITHUB_OUTPUT
                test:
                    runs-on: ubuntu-latest
                    needs: [build]
                    steps:
                        - env:
                            TAG: ${{ needs.build.outputs.typo_output }}
                          run: echo "$TAG"
            """,
            ["\"typo_output\" is not defined in object type"]),
            // #8: needs.build.outputs.image_tag should be valid
            new RuleCase(
            "ok-needs-known-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        image_tag: ${{ steps.build.outputs.tag }}
                    steps:
                        - id: build
                          run: echo "tag=v1" >> $GITHUB_OUTPUT
                test:
                    runs-on: ubuntu-latest
                    needs: [build]
                    steps:
                        - env:
                            TAG: ${{ needs.build.outputs.image_tag }}
                          run: echo "$TAG"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_NeedsUndefinedJob_TableDriven()
    {
        var cases = new[]
        {
            // A-4: needs.prepare undefined when not in needs list
            new RuleCase(
            "ng-needs-job-not-in-needs-list",
            """
            on: push
            jobs:
                prepare:
                    runs-on: ubuntu-latest
                    outputs:
                        prepared: ${{ steps.a.outputs.val }}
                    steps:
                        - id: a
                          run: echo "val=1" >> $GITHUB_OUTPUT
                        - run: echo '${{ needs.prepare.outputs.prepared }}'
            """,
            ["\"prepare\" is not defined in object type"]),
            // A-4: needs.some_job undefined (job doesn't exist)
            new RuleCase(
            "ng-needs-nonexistent-job",
            """
            on: push
            jobs:
                install:
                    runs-on: ubuntu-latest
                    outputs:
                        installed: ok
                    steps:
                        - run: echo install
                build:
                    needs: [install]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo '${{ needs.some_job }}'
            """,
            ["\"some_job\" is not defined in object type"]),
            // A-4: needs.build undefined in other job (build not in other's needs)
            new RuleCase(
            "ng-needs-job-not-declared-in-needs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        built: ok
                    steps:
                        - run: echo build
                other:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo '${{ needs.build.outputs.built }}'
            """,
            ["\"build\" is not defined in object type"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_StepsCrossJob_TableDriven()
    {
        var cases = new[]
        {
            // A-5: steps.get_value undefined in other job (step IDs are job-local)
            new RuleCase(
            "ng-steps-cross-job-reference",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - id: get_value
                          run: echo "name=foo" >> $GITHUB_OUTPUT
                        - run: echo '${{ steps.get_value.outputs.name }}'
                other:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo '${{ steps.get_value.outputs.name }}'
            """,
            ["\"get_value\" is not defined in object type"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_StepsOrderValidation_TableDriven()
    {
        var cases = new[]
        {
            // #9: referencing a step ID that hasn't been defined yet should be an error
            new RuleCase(
            "ng-step-reference-before-definition",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ steps.later.outcome == 'success' }}
                          run: echo "first"
                        - id: later
                          run: echo "later"
            """,
            ["\"later\" is not defined in object type"]),
            // #9: referencing a step ID that was defined earlier is fine
            new RuleCase(
            "ok-step-reference-after-definition",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: earlier
                          run: echo "earlier"
                        - if: ${{ steps.earlier.outcome == 'success' }}
                          run: echo "second"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_RunnerContextInMatrix_TableDriven()
    {
        var cases = new[]
        {
            // #23: runner context should NOT be available in strategy.matrix expressions
            // (currently Job scope doesn't include runner, so this may already pass)
            new RuleCase(
            "ng-matrix-uses-runner-context",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                    runs-on: ${{ matrix.os }}
                    steps:
                        - if: ${{ runner.os == 'Linux' }}
                          run: echo ok
            """,
            []),
            // runner context IS valid in step scope — should not error
            new RuleCase(
            "ok-step-uses-runner-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ runner.os == 'Linux' }}
                          run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ReusableWorkflowOutputs_TableDriven()
    {
        var cases = new[]
        {
            // #25: jobs.<id>.outputs.<name> in workflow_call output value should validate
            new RuleCase(
            "ng-workflow-output-references-unknown-job-output",
            """
            on:
                workflow_call:
                    outputs:
                        image:
                            value: ${{ jobs.build.outputs.imagetag }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        image_tag: ${{ steps.b.outputs.tag }}
                    steps:
                        - id: b
                          run: echo "tag=v1" >> $GITHUB_OUTPUT
            """,
            ["\"imagetag\" is not defined"]),
            // #25: correct output name should not error
            new RuleCase(
            "ok-workflow-output-references-known-job-output",
            """
            on:
                workflow_call:
                    outputs:
                        image:
                            value: ${{ jobs.build.outputs.image_tag }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        image_tag: ${{ steps.b.outputs.tag }}
                    steps:
                        - id: b
                          run: echo "tag=v1" >> $GITHUB_OUTPUT
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_RunAndWithExpressions_TableDriven()
    {
        var cases = new[]
        {
            // A-4: run field expression uses unknown context
            new RuleCase(
            "ng-run-field-unknown-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ bogus.value }}
            """,
            ["undefined context \"bogus\""]),
            // A-4: run field expression uses matrix key from wrong job
            new RuleCase(
            "ng-run-field-matrix-key-from-wrong-job",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo build
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.os }}
            """,
            ["\"os\" is not defined in object type"]),
            // A-5: action with input expression using unknown context
            new RuleCase(
            "ng-action-with-input-unknown-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                            ref: ${{ nosuch.branch }}
            """,
            ["undefined context \"nosuch\""]),
            // A-4/A-5: run and with expressions using valid context should not error
            new RuleCase(
            "ok-run-and-with-valid-contexts",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                            ref: ${{ github.ref }}
                        - run: echo ${{ github.sha }}
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    // Context availability — missing field visits

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ContextAvailability_WorkflowLevel_TableDriven()
    {
        var cases = new[]
        {
            // run-name: env context not allowed
            new RuleCase(
            "ng-run-name-env",
            """
            run-name: ${{ env.FOO }}
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // workflow env: env context not allowed (self-reference)
            new RuleCase(
            "ng-workflow-env-self-ref",
            """
            on: push
            env:
                BAR: ${{ env.BAR }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // workflow concurrency: env context not allowed
            new RuleCase(
            "ng-workflow-concurrency-env",
            """
            on: push
            concurrency:
                group: ${{ env.FOO }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // workflow_call input default: env context not allowed
            new RuleCase(
            "ng-workflow-call-input-default-env",
            """
            on:
                workflow_call:
                    inputs:
                        foo:
                            type: string
                            default: ${{ env.FOO }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // OK: workflow env using github and secrets
            new RuleCase(
            "ok-workflow-env-github-secrets",
            """
            on: push
            env:
                FOO: ${{ github.sha }}
                BAR: ${{ secrets.TOKEN }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ContextAvailability_JobLevel_TableDriven()
    {
        var cases = new[]
        {
            // job.name: runner not allowed
            new RuleCase(
            "ng-job-name-runner",
            """
            on: push
            jobs:
                build:
                    name: ${{ runner.name }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.runs-on: env and runner not allowed
            new RuleCase(
            "ng-job-runs-on-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ env.SUFFIX }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            new RuleCase(
            "ng-job-runs-on-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ runner.OS }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.concurrency: env not allowed
            new RuleCase(
            "ng-job-concurrency-env",
            """
            on: push
            jobs:
                build:
                    concurrency:
                        group: ${{ env.FOO }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.container.credentials: runner not allowed
            new RuleCase(
            "ng-job-container-credentials-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: node:14
                        credentials:
                            username: ${{ runner.os }}
                            password: ${{ env.FOO }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.continue-on-error: env not allowed
            new RuleCase(
            "ng-job-continue-on-error-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    continue-on-error: ${{ env.FOO == '' }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.environment: runner not allowed
            new RuleCase(
            "ng-job-environment-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    environment:
                        name: ${{ runner.name }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.strategy: env not allowed
            new RuleCase(
            "ng-job-strategy-env",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os:
                                - ${{ env.OS }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.timeout-minutes: env not allowed
            new RuleCase(
            "ng-job-timeout-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    timeout-minutes: ${{ env.TIMEOUT }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.outputs: OK (env, runner, steps all allowed)
            new RuleCase(
            "ok-job-outputs-env-runner-steps",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        foo: ${{ runner.name }}-${{ env.FOO }}-${{ steps.s1.outputs.x }}
                    steps:
                        - id: s1
                          run: echo
            """,
            []),
            // job.defaults.run: env allowed, runner not allowed
            new RuleCase(
            "ng-job-defaults-run-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    defaults:
                        run:
                            working-directory: ${{ runner.temp }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.services.image: env not allowed
            new RuleCase(
            "ng-job-services-image-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        nginx:
                            image: ${{ env.IMAGE }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.services.credentials: runner not allowed
            new RuleCase(
            "ng-job-services-credentials-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        nginx:
                            image: nginx
                            credentials:
                                username: ${{ runner.name }}
                                password: ${{ env.PASSWORD }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.secrets: OK (secrets allowed for reusable workflow calls)
            new RuleCase(
            "ok-job-secrets-secrets",
            """
            on: push
            jobs:
                caller:
                    uses: owner/repo/workflow.yml@main
                    secrets:
                        password: ${{ secrets.PASSWORD }}
            """,
            []),
            // job.with (reusable): env not allowed
            new RuleCase(
            "ng-job-with-env",
            """
            on: push
            jobs:
                caller:
                    uses: owner/repo/workflow.yml@main
                    with:
                        some-input: ${{ env.HELLO }}
            """,
            ["context \"env\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ContextAvailability_StepLevel_TableDriven()
    {
        var cases = new[]
        {
            // step.name: OK (all step contexts available)
            new RuleCase(
            "ok-step-name-env-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - name: ${{ env.VERSION }} on ${{ runner.name }}
                          run: echo
            """,
            []),
            // step.continue-on-error: OK (inputs allowed)
            new RuleCase(
            "ok-step-continue-on-error-inputs",
            """
            on:
                workflow_call:
                    inputs:
                        bool:
                            type: boolean
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - continue-on-error: ${{ inputs.bool }}
                          run: echo
            """,
            []),
            // step.timeout-minutes: OK
            new RuleCase(
            "ok-step-timeout-minutes-inputs",
            """
            on:
                workflow_call:
                    inputs:
                        timeout:
                            type: number
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - timeout-minutes: ${{ inputs.timeout }}
                          run: echo
            """,
            []),
            // step.working-directory: OK (runner allowed at step level)
            new RuleCase(
            "ok-step-working-directory-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - working-directory: ${{ runner.temp }}
                          run: echo
            """,
            []),
            // step.if: secrets not allowed
            new RuleCase(
            "ng-step-if-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ secrets.PASSWORD != '' }}
                          run: echo
            """,
            ["context \"secrets\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    // env context banned in workflow/job env

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_EnvContextBanned_TableDriven()
    {
        var cases = new[]
        {
            // workflow env cannot reference env context
            new RuleCase(
            "ng-workflow-env-env-context",
            """
            on: push
            env:
                ERROR1: ${{ env.PATH }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job env cannot reference env context
            new RuleCase(
            "ng-job-env-env-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        ERROR2: ${{ env.PATH }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // step env CAN reference env context (OK)
            new RuleCase(
            "ok-step-env-env-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
                          env:
                            BAR: ${{ env.FOO }}
            """,
            []),
            // container env CAN reference env context (OK)
            new RuleCase(
            "ok-container-env-env-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: node:14
                        env:
                            MYPATH: ${{ env.PATH }}
                    steps:
                        - run: echo
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    // env context banned in job-level if

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_JobIfEnvBanned_TableDriven()
    {
        var cases = new[]
        {
            // job.if with env context: not allowed
            new RuleCase(
            "ng-job-if-env-dollar-brace",
            """
            on: push
            jobs:
                test1:
                    runs-on: ubuntu-latest
                    if: ${{ env.FOO == 'aaa' }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.if without ${{ }}: env not allowed
            new RuleCase(
            "ng-job-if-env-bare",
            """
            on: push
            jobs:
                test2:
                    runs-on: ubuntu-latest
                    if: env.FOO == 'aaa'
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // reusable workflow call job if: env not allowed
            new RuleCase(
            "ng-reusable-job-if-env",
            """
            on: push
            jobs:
                test3:
                    uses: org/repo/workflow.yml@v1
                    if: ${{ env.FOO == 'aaa' }}
            """,
            ["context \"env\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    // shell key context availability

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ShellKeyContextAvailability_TableDriven()
    {
        var cases = new[]
        {
            // workflow-level defaults.run.shell: no context available
            new RuleCase(
            "ng-workflow-defaults-shell-env",
            """
            on: push
            defaults:
                run:
                    shell: ${{ env.SHELL }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job-level defaults.run.shell: env IS available (OK)
            new RuleCase(
            "ok-job-defaults-shell-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    defaults:
                        run:
                            shell: ${{ env.SHELL }}
                    steps:
                        - run: echo
            """,
            []),
            // step-level shell: no context available
            new RuleCase(
            "ng-step-shell-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
                          shell: ${{ env.SHELL }}
            """,
            ["context \"env\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    // special function availability

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_SpecialFunctionAvailability_TableDriven()
    {
        var cases = new[]
        {
            // status functions OK in job.if
            new RuleCase(
            "ok-always-in-job-if",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: always()
                    steps:
                        - run: echo
            """,
            []),
            new RuleCase(
            "ok-failure-in-step-if",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: failure()
                          run: echo
            """,
            []),
            // status functions NOT OK in strategy.matrix
            new RuleCase(
            "ng-always-in-strategy-matrix",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            errors:
                                - ${{ always() }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["function \"always\" is not allowed here"]),
            // hashFiles OK in step level
            new RuleCase(
            "ok-hashfiles-in-step-run",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ hashFiles('...') }}"
            """,
            []),
            // hashFiles NOT OK in job.if
            new RuleCase(
            "ng-hashfiles-in-job-if",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: ${{ hashFiles('...') }}
                    steps:
                        - run: echo
            """,
            ["function \"hashFiles\" is not allowed here"]),
            // success() NOT OK in step.run
            new RuleCase(
            "ng-success-in-step-run",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo 'success? ${{ success() }}'
            """,
            ["function \"success\" is not allowed here"]),
            // hashFiles NOT OK in strategy.matrix
            new RuleCase(
            "ng-hashfiles-in-strategy-matrix",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            errors:
                                - ${{ hashFiles('...') }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["function \"hashFiles\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    // step.id no context allowed

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_StepIdNoContext_TableDriven()
    {
        var cases = new[]
        {
            // step.id: no context allowed
            new RuleCase(
            "ng-step-id-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: ${{ inputs.foo }}
                          run: echo
            """,
            ["context \"inputs\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    // available context listing in message

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_MessageIncludesAvailableContexts_TableDriven()
    {
        var cases = new[]
        {
            // Error message should list available contexts
            new RuleCase(
            "ng-job-if-env-lists-available-contexts",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    if: ${{ env.FOO == 'aaa' }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here", "available contexts are"]),
            // "no context is available here" for shell
            new RuleCase(
            "ng-workflow-shell-no-context",
            """
            on: push
            defaults:
                run:
                    shell: ${{ env.SHELL }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here", "no context is available here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    // expr-undefined-var scope expansion

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_InputsWithoutWorkflowCall_TableDriven()
    {
        var cases = new[]
        {
            // When no workflow_call event, inputs has no properties → inputs.some_input is undefined
            new RuleCase(
            "ng-inputs-without-workflow-call",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ inputs.some_input }}
            """,
            ["property \"some_input\" is not defined in object type"]),
            // With workflow_call + defined input → OK
            new RuleCase(
            "ok-inputs-with-workflow-call",
            """
            on:
                workflow_call:
                    inputs:
                        my_input:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ inputs.my_input }}
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_WorkflowCallOutputsSema_TableDriven()
    {
        var cases = new[]
        {
            // job0 has no outputs → jobs.job0.outputs.some_output is undefined
            new RuleCase(
            "ng-workflow-call-output-no-job-outputs",
            """
            on:
                workflow_call:
                    outputs:
                        output1:
                            value: ${{ jobs.job0.outputs.some_output }}
            jobs:
                job0:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hi
            """,
            ["property \"some_output\" is not defined"]),
            // job1 has outputs but unknown_output is not among them
            new RuleCase(
            "ng-workflow-call-output-unknown-property",
            """
            on:
                workflow_call:
                    outputs:
                        output2:
                            value: ${{ jobs.job1.outputs.unknown_output }}
            jobs:
                job1:
                    runs-on: ubuntu-latest
                    outputs:
                        foo: bar
                    steps:
                        - run: echo hello
            """,
            ["property \"unknown_output\" is not defined"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_InputDefaultForwardReference_TableDriven()
    {
        var cases = new[]
        {
            // input2 not yet defined when input1.default references it
            new RuleCase(
            "ng-input-default-forward-ref",
            """
            on:
                workflow_call:
                    inputs:
                        input1:
                            type: string
                            default: ${{ inputs.input2 }}
                        input2:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["property \"input2\" is not defined in object type"]),
            // input3 references itself — not yet defined
            new RuleCase(
            "ng-input-default-self-ref",
            """
            on:
                workflow_call:
                    inputs:
                        input1:
                            type: string
                        input2:
                            type: string
                        input3:
                            type: boolean
                            default: ${{ inputs.input3 }}
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["property \"input3\" is not defined in object type"]),
            // input2 references input1 (already defined) → OK
            new RuleCase(
            "ok-input-default-back-ref",
            """
            on:
                workflow_call:
                    inputs:
                        input1:
                            type: string
                        input2:
                            type: string
                            default: ${{ inputs.input1 }}
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    // fromJSON broken JSON validation

    [Test]
    public async Task RuleRegression_FromJsonBrokenJson()
    {
        // fromJSON validation is done in the parser (not linter rule), so diagnostics have RuleId=null
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                foo:
                    strategy:
                        matrix:
                            include:
                                - invalid1: ${{ fromJSON('"foo') }}
                                - invalid2: ${{ fromJSON('["foo"') }}
                                - invalid3: ${{ fromJSON('') }}
                                - valid: ${{ fromJSON('"hello"') }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """);
        var result = new LintEngine([]).Check(Encoding.UTF8.GetBytes(yaml), "fromjson-test.yml");
        var fromJsonErrors = result.Diagnostics
            .Where(x => x.Message.Contains("fromJSON()", StringComparison.Ordinal) && x.Message.Contains("JSON", StringComparison.Ordinal))
            .ToArray();

        // 3 broken JSON errors, none for valid JSON
        await Assert.That(fromJsonErrors).Count().IsEqualTo(3);
        await Assert.That(fromJsonErrors[0].Message).Contains("not valid JSON");
        await Assert.That(fromJsonErrors[1].Message).Contains("not valid JSON");
        await Assert.That(fromJsonErrors[2].Message).Contains("not valid JSON");
    }

    // double-quote string literal detection

    [Test]
    public async Task RuleRegression_ExpressionParser_DoubleQuoteDetection()
    {
        // Double-quote in expression should produce a parse error suggesting single quotes
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                foo:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          continue-on-error: ${{ env.OS == "macos-latest" }}
            """);
        var result = new LintEngine([]).Check(Encoding.UTF8.GetBytes(yaml), "issue193.yml");

        // Parser diagnostics have RuleId=null. Check all diagnostics.
        var hasDoubleQuoteError = result.Diagnostics.Any(x =>
            x.Message.Contains("'\"'", StringComparison.Ordinal) &&
            x.Message.Contains("single quote", StringComparison.OrdinalIgnoreCase));
        await Assert.That(hasDoubleQuoteError).IsTrue();
    }

    [Test]
    public async Task RuleRegression_RunEnvContextDirectUseRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-run-uses-shell-variable-only",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        VERSION: 1.2.3
                    steps:
                        - run: echo "$VERSION"
            """,
            []),
            new RuleCase(
            "ok-run-uses-non-env-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.ref_name }}"
            """,
            []),
            new RuleCase(
            "ng-run-uses-env-dot-access",
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
            ["must not reference", "env.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-env-bracket-access",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        VERSION: 1.2.3
                    steps:
                        - run: echo "${{ env['VERSION'] }}"
            """,
            ["must not reference", "env.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-env-in-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        VERSION: 1.2.3
                    steps:
                        - run: echo "${{ format('{0}', env.VERSION) }}"
            """,
            ["must not reference", "env.*", "shell variables"]),
        };

        await AssertRuleCases(new RunEnvContextDirectUseRule(), "run-env-context-direct-use", cases);
    }

    [Test]
    public async Task RuleRegression_RunSecretsContextDirectUseRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-run-uses-shell-variable-only",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        TOKEN: ${{ secrets.MY_TOKEN }}
                    steps:
                        - run: echo "$TOKEN"
            """,
            []),
            new RuleCase(
            "ok-run-uses-non-secrets-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.ref_name }}"
            """,
            []),
            new RuleCase(
            "ng-run-uses-secrets-dot-access",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ secrets.MY_TOKEN }}"
            """,
            ["must not reference", "secrets.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-secrets-bracket-access",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ secrets['MY_TOKEN'] }}"
            """,
            ["must not reference", "secrets.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-secrets-in-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ format('{0}', secrets.MY_TOKEN) }}"
            """,
            ["must not reference", "secrets.*", "shell variables"]),
        };

        await AssertRuleCases(new RunSecretsContextDirectUseRule(), "run-secrets-context-direct-use", cases);
    }

    [Test]
    public async Task RuleRegression_RunInputsContextDirectUseRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-run-uses-shell-variable-only",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        TARGET: ${{ inputs.target }}
                    steps:
                        - run: echo "$TARGET"
            """,
            []),
            new RuleCase(
            "ok-block-run-does-not-bleed-into-env-or-next-step-if",
            """
            name: ci
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                    - name: benchmark
                        run: |
                            dotnet run --filter "${FILTER}"
                            echo "result=success" >> "$GITHUB_OUTPUT"
                        env:
                            FILTER: ${{ inputs.target }}
                    - name: report
                        run: |
                            echo first

                            echo second
                    - name: update
                        if: ${{ inputs.target == '*' }}
                        run: |
                            echo done
            """.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            []),
            new RuleCase(
            "ok-run-uses-non-inputs-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.ref_name }}"
            """,
            []),
            new RuleCase(
            "ng-run-uses-inputs-dot-access",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ inputs.target }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-inputs-bracket-access",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ inputs['target'] }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-github-event-inputs-dot-access",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.inputs.target }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-inputs-in-function",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ format('{0}', inputs.target) }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
        };

        await AssertRuleCases(new RunInputsContextDirectUseRule(), "run-inputs-context-direct-use", cases);
    }

    [Test]
    public async Task RuleRegression_SecretsWholeContextAccessRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-specific-key-in-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        MY_SECRET: ${{ secrets.MY_TOKEN }}
                    steps:
                        - run: echo "$MY_SECRET"
            """,
            []),
            new RuleCase(
            "ok-no-secrets-reference",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.ref_name }}"
            """,
            []),
            new RuleCase(
            "ng-run-tojson-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ toJson(secrets) }}"
            """,
            ["must not reference", "secrets", "context object"]),
            new RuleCase(
            "ng-step-env-tojson-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/some-action@v1
                          env:
                            ALL_SECRETS: ${{ toJson(secrets) }}
            """,
            ["must not reference", "secrets", "context object"]),
            new RuleCase(
            "ng-step-with-tojson-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/some-action@v1
                          with:
                            all-secrets: ${{ toJson(secrets) }}
            """,
            ["must not reference", "secrets", "context object"]),
            new RuleCase(
            "ng-job-env-tojson-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        ALL_SECRETS: ${{ toJson(secrets) }}
                    steps:
                        - run: echo ok
            """,
            ["must not reference", "secrets", "context object"]),
            new RuleCase(
            "ng-format-function-whole-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ format('{0}', secrets) }}"
            """,
            ["must not reference", "secrets", "context object"]),
        };

        await AssertRuleCases(new SecretsWholeContextAccessRule(), "secrets-whole-context-access", cases);
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_Fix_InsertsPermissionsAfterRunsOn()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        var result = engine.Check(sourceBytes, "job-permissions-required-fix-runs-on.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "job-permissions-required-fix-runs-on.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        var runsOnIndex = fixedText.IndexOf("runs-on: ubuntu-latest", StringComparison.Ordinal);
        var permissionsIndex = fixedText.IndexOf("permissions: {}", StringComparison.Ordinal);
        var stepsIndex = fixedText.IndexOf("steps:", StringComparison.Ordinal);

        await Assert.That(runsOnIndex >= 0).IsTrue();
        await Assert.That(permissionsIndex > runsOnIndex).IsTrue();
        await Assert.That(stepsIndex > permissionsIndex).IsTrue();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_Fix_DoesNotIntroduceTabIndentation_WhenTargetScopeUsesSpaces()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        var result = engine.Check(sourceBytes, "job-permissions-required-fix-no-tab-introduce.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedText = Encoding.UTF8.GetString(FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var permissionsLine = fixedText.Split('\n').First(x => x.Contains("permissions: {}", StringComparison.Ordinal));

        await Assert.That(permissionsLine.Contains('\t')).IsFalse();
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_Fix_InsertsPermissionsAfterUses()
    {
        var yaml = """
        on: push
        jobs:
            reuse:
                uses: owner/repo/.github/workflows/reusable.yml@main
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        var result = engine.Check(sourceBytes, "job-permissions-required-fix-uses.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes).Replace("\r\n", "\n", StringComparison.Ordinal);

        var usesIndex = fixedText.IndexOf("uses: owner/repo/.github/workflows/reusable.yml@main", StringComparison.Ordinal);
        var permissionsIndex = fixedText.IndexOf("permissions: {}", StringComparison.Ordinal);

        await Assert.That(usesIndex >= 0).IsTrue();
        await Assert.That(permissionsIndex > usesIndex).IsTrue();
        var relint = engine.Check(fixedBytes, "job-permissions-required-fix-uses.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_Fix_DoesNotChangeWhitespaceOutsideInsertion()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        var result = engine.Check(sourceBytes, "job-permissions-required-fix-whitespace.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedText = Encoding.UTF8.GetString(FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var permissionsLine = fixedText.Split('\n').First(x => x.Contains("permissions: {}", StringComparison.Ordinal));
        var withoutInsertedPermissions = fixedText.Replace(permissionsLine + "\n", string.Empty, StringComparison.Ordinal);
        var original = yaml.Replace("\r\n", "\n", StringComparison.Ordinal);

        await Assert.That(withoutInsertedPermissions).IsEqualTo(original);
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_Fix_DoesNotIntroduceTrailingSpaces()
    {
        var yaml = """
        on: push
        jobs:
            build:
                uses: owner/repo/.github/workflows/reusable.yml@main
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        var result = engine.Check(sourceBytes, "job-permissions-required-fix-no-trailing.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedText = Encoding.UTF8.GetString(FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits));
        var normalized = fixedText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            await Assert.That(lines[i].EndsWith(" ", StringComparison.Ordinal)).IsFalse();
            await Assert.That(lines[i].EndsWith("\t", StringComparison.Ordinal)).IsFalse();
        }
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_DoesNotAttachFix_WhenIndentationInferenceIsAmbiguous()
    {
        var yaml = """
        on: push
        jobs:
            build: {}
        """;

        var result = new LintEngine([new JobPermissionsRequiredRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "job-permissions-required-no-fix-ambiguous.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task AutoFixCatalog_OnlySevenRulesAttachFix_TableDriven()
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
                ExpectsFix: false),
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
                ExpectsFix: false),
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
                "cache-poisoning",
                new CachePoisoningRule(),
                """
                on: pull_request
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
                "self-hosted-runner",
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
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var result = new LintEngine([c.Rule]).Check(
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
    public async Task LintEngine_RunEnvContextDirectUse_Fix_ReplacesSimpleDotAccessWithPosixVariable()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ env.VERSION }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunEnvContextDirectUseRule()]);
        var result = engine.Check(sourceBytes, "run-env-fix-posix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "run-env-fix-posix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        await Assert.That(fixedText.Contains("${VERSION}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("${{ env.VERSION }}", StringComparison.Ordinal)).IsFalse();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "run-env-context-direct-use")).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_Fix_ReplacesSimpleBracketAccessWithPowerShellVariable()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: windows-latest
                steps:
                    - shell: pwsh
                      run: Write-Host "${{ env['VERSION'] }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunEnvContextDirectUseRule()]);
        var result = engine.Check(sourceBytes, "run-env-fix-powershell.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("$env:VERSION", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("${{ env['VERSION'] }}", StringComparison.Ordinal)).IsFalse();
        var relint = engine.Check(fixedBytes, "run-env-fix-powershell.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "run-env-context-direct-use")).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_DoesNotAttachFix_ForCompositeExpression()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ format('{0}', env.VERSION) }}"
        """;

        var result = new LintEngine([new RunEnvContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-env-no-fix-composite.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_DoesNotAttachFix_InsideSingleQuotedHereDoc()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        cat << 'EOF' > pr_comment.md
                          Workflow [${{ env.GITHUB_ACTIONS_RUN_URL }}) found CRLF files.
                        EOF
        """;

        var result = new LintEngine([new RunEnvContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-env-no-fix-heredoc.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_DiagnosticLocation_PointsToExpression_NotFollowingEnvKey()
    {
        // Regression: diagnostic was pointing to the step-level env: key (after the block scalar)
        // instead of the ${{ env.* }} expression inside the run: script.
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - name: Dump environment
                      shell: bash
                      run: |
                        mkdir -p "${{ env.OUTPUT_PATH }}"
                        env | tee "${{ env.OUTPUT_PATH }}/out.sh"
                      env:
                        OUTPUT_PATH: ${{ inputs.output-path }}/env
        """;

        var result = new LintEngine([new RunEnvContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-env-location.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        // The diagnostic must NOT point to the env: key line (which comes after the run: block).
        // It must point to the actual ${{ env.* }} expression inside the run: script.
        var locationLine = diagnostic.Location.StartLine;
        var envKeyLineNumber = yaml.Split('\n')
            .Select((line, i) => (line, lineNumber: i + 1))
            .First(x => x.line.TrimStart().StartsWith("env:") && x.lineNumber > 10)
            .lineNumber;

        await Assert.That(locationLine).IsNotEqualTo(envKeyLineNumber);
        await Assert.That(locationLine).IsLessThan(envKeyLineNumber);
    }

    [Test]
    public async Task LintEngine_RunSecretsContextDirectUse_Fix_ReplacesSimpleReferenceWithMappedVariable()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                env:
                    TOKEN: ${{ secrets.MY_TOKEN }}
                steps:
                    - run: echo "${{ secrets.MY_TOKEN }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunSecretsContextDirectUseRule()]);
        var result = engine.Check(sourceBytes, "run-secrets-fix-posix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-secrets-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("run: echo \"${TOKEN}\"", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunSecretsContextDirectUse_DoesNotAttachFix_WithoutUniqueMapping()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                env:
                    TOKEN_A: ${{ secrets.MY_TOKEN }}
                    TOKEN_B: ${{ secrets.MY_TOKEN }}
                steps:
                    - run: echo "${{ secrets.MY_TOKEN }}"
        """;

        var result = new LintEngine([new RunSecretsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-secrets-no-fix-ambiguous.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-secrets-context-direct-use");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Fix_ReplacesSimpleReferenceWithMappedVariable()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: windows-latest
                env:
                    TARGET: ${{ github.event.inputs.target }}
                steps:
                    - shell: pwsh
                      run: Write-Host "${{ github.event.inputs.target }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunInputsContextDirectUseRule()]);
        var result = engine.Check(sourceBytes, "run-inputs-fix-powershell.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("run: Write-Host \"$env:TARGET\"", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_DoesNotAttachFix_WithoutUniqueMapping()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: ubuntu-latest
                env:
                    TARGET_A: ${{ inputs.target }}
                    TARGET_B: ${{ github.event.inputs.target }}
                steps:
                    - run: echo "${{ inputs.target }}"
        """;

        var result = new LintEngine([new RunInputsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-inputs-no-fix-ambiguous.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_BlockRunLocation_PointsToExpressionLine()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - name: benchmark
                      run: |
                        echo "${{ inputs.target }}"
                        echo done
                    - name: next
                      run: exit 1
        """.Replace("\r\n", "\n").Replace("\n", "\r\n");

        var result = new LintEngine([new RunInputsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-inputs-block-location.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Location.StartLine).IsEqualTo(8);
        await Assert.That(diagnostic.Location.StartColumn).IsEqualTo(23);
    }

    [Test]
    public async Task LintEngine_DeduplicatesRuleDiagnostics_ByPriority()
    {
        var yaml = """
                on: push
                jobs: {}
                """;

        var engine = new LintEngine(
        [
            new DuplicateDiagnosticRule(RuleId.Permissions),
                new DuplicateDiagnosticRule(RuleId.JobStructure),
        ]);

        var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "priority-dedup.yml");
        var duplicated = result.Diagnostics
            .Where(static x => x.Message == "shared duplicate diagnostic")
            .ToArray();

        await Assert.That(duplicated.Length).IsEqualTo(1);
        await Assert.That(duplicated[0].RuleId).IsEqualTo("job-structure");
    }

    [Test]
    public async Task LintEngine_DisabledRule_DoesNotEmitDiagnostics()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        var disabledConfig = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["job-permissions-required"] = new RuleConfig { Enabled = false },
            },
        };

        var disabledResult = engine.Check(Encoding.UTF8.GetBytes(yaml), "rule-disable.yml", disabledConfig);
        await Assert.That(disabledResult.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();

        var enabledResult = engine.Check(Encoding.UTF8.GetBytes(yaml), "rule-enabled.yml");
        await Assert.That(enabledResult.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsTrue();
    }

    [Test]
    public async Task LintEngine_DisabledRule_CanonicalIdInRuleOptions_DoesNotEmitDiagnostics()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        var disabledConfig = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["seiton-lint-rule-008"] = new RuleConfig { Enabled = false },
            },
        };

        var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "rule-disable-canonical.yml", disabledConfig);
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();
    }

    [Test]
    public async Task LintEngine_RuleSeverityOverride_RewritesDiagnosticSeverity()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        var overrideConfig = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["job-permissions-required"] = new RuleConfig { Severity = DiagnosticSeverity.Error },
            },
        };

        var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "severity-override.yml", overrideConfig);
        var diagnostic = result.Diagnostics.FirstOrDefault(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Message.Length).IsGreaterThan(0);
        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task LintEngine_InlineDisableNextLine_SuppressesTargetRuleOnlyOnNextLine()
    {
        var yaml = """
        on: push
        jobs:
            # seiton: disable-next-line seiton-lint-rule-008
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-next-line.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "job-permissions-required").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(8);
    }

    [Test]
    public async Task LintEngine_InlineDisableNextLine_SupportsMultipleRuleIds()
    {
        var yaml = """
        on:
            # seiton: disable-next-line seiton-lint-rule-007, seiton-lint-rule-008
            pull_request_target:
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo test
        """;

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-multi.yml");

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "dangerous-triggers")).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsTrue();
    }

    [Test]
    public async Task LintEngine_InlineDisableNextLine_SupportsSemanticRuleId()
    {
        var yaml = """
        on: push
        jobs:
            # seiton: disable-next-line job-permissions-required
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-semantic.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "job-permissions-required").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(8);
    }

    [Test]
    public async Task LintEngine_InlineSeitonDisableNextLine_SupportsSemanticRuleId()
    {
        var yaml = """
        on: push
        jobs:
            # seiton: disable-next-line job-permissions-required
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-seiton-next-line.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "job-permissions-required").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(8);
    }

    [Test]
    public async Task LintEngine_InlineSeitonDisableFile_SuppressesRuleForEntireFile()
    {
        var yaml = """
        # seiton: disable-file job-permissions-required
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-seiton-file.yml");

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();
    }

    [Test]
    public async Task LintEngine_InlineSeitonDisableJob_SuppressesRuleOnlyForTargetJob()
    {
        var yaml = """
        # seiton: disable-job build job-permissions-required
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-seiton-job.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "job-permissions-required").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(8);
    }

    [Test]
    public async Task LintEngine_InlineDisableNextLine_UnknownRuleId_ReportsConfigurationError()
    {
        var yaml = """
        on: push
        jobs:
            # seiton: disable-next-line job-permissions-requred
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo test
        """;

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-unknown-rule.yml");
        var configError = result.Diagnostics.FirstOrDefault(x =>
            x.RuleId is null
            && x.Message.Contains("unknown rule-id", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(configError.Message.Contains("Did you mean 'job-permissions-required'", StringComparison.Ordinal)).IsTrue();
        await Assert.That(configError.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task LintEngine_InlineSeitonDisableJob_UnknownJobId_ReportsConfigurationError()
    {
        var yaml = """
        # seiton: disable-job buid job-permissions-required
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo test
        """;

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-seiton-unknown-job.yml");
        var configError = result.Diagnostics.FirstOrDefault(x =>
            x.RuleId is null
            && x.Message.Contains("unknown job-id", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(configError.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_FileGlob_SuppressesDiagnosticsAndEmitsSummary()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["job-permissions-required"]),
            ],
        };

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();
        await Assert.That(result.SuppressionSummary.TotalSuppressed).IsEqualTo(2);
        await Assert.That(result.SuppressionSummary.SuppressedByRule.TryGetValue("job-permissions-required", out var count) && count == 2).IsTrue();
        await Assert.That(result.SuppressionSummary.Records.All(x => x.Source == SuppressionSource.ConfigFile)).IsTrue();
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_JobScope_SuppressesTargetJobOnly()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["job-permissions-required"], Jobs: ["build"]),
            ],
        };

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "job-permissions-required").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(7);
        await Assert.That(result.SuppressionSummary.TotalSuppressed).IsEqualTo(1);
        await Assert.That(result.SuppressionSummary.Records.Length).IsEqualTo(1);
        await Assert.That(result.SuppressionSummary.Records[0].Source).IsEqualTo(SuppressionSource.ConfigJob);
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_UnknownRuleId_ReportsConfigurationError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["job-permissions-requred"]),
            ],
        };

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);
        var configError = result.Diagnostics.FirstOrDefault(x =>
            x.RuleId is null
            && x.Message.Contains("unknown rule-id", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(configError.Message.Contains("Did you mean 'job-permissions-required'", StringComparison.Ordinal)).IsTrue();
        await Assert.That(configError.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_UnknownJobId_ReportsConfigurationError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["job-permissions-required"], Jobs: ["buid"]),
            ],
        };

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);
        var configError = result.Diagnostics.FirstOrDefault(x =>
            x.RuleId is null
            && x.Message.Contains("unknown job-id", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(configError.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task LintEngine_NonDisableableRule_InRuleOptions_ReportsConfigurationErrorAndKeepsRuleEnabled()
    {
        var yaml = """
        on: push
        permissions: write-all
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["deny-write-all"] = new RuleConfig { Enabled = false },
            },
        };

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "failsafe-rule-options.yml", config);
        var configError = result.Diagnostics.FirstOrDefault(x => x.RuleId is null && x.Message.Contains("non-disableable", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsTrue();
    }

    [Test]
    public async Task LintEngine_MinimumSeverity_InRuleOptions_ReportsConfigurationErrorAndKeepsEffectiveSeverity()
    {
        var yaml = """
        on: push
        permissions: write-all
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["deny-write-all"] = new RuleConfig { Severity = DiagnosticSeverity.Warning },
            },
        };

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "failsafe-min-severity.yml", config);
        var configError = result.Diagnostics.FirstOrDefault(x => x.RuleId is null && x.Message.Contains("minimum severity", StringComparison.Ordinal));
        var ruleDiagnostic = result.Diagnostics.FirstOrDefault(x => x.RuleId == "deny-write-all");

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(ruleDiagnostic.Message.Length).IsGreaterThan(0);
        await Assert.That(ruleDiagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task LintEngine_NonDisableableRule_DenyReadAll_InRuleOptions_ReportsConfigurationErrorAndKeepsRuleEnabled()
    {
        var yaml = """
        on: push
        permissions: read-all
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["deny-read-all"] = new RuleConfig { Enabled = false },
            },
        };

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "failsafe-rule-options-deny-read-all.yml", config);
        var configError = result.Diagnostics.FirstOrDefault(x => x.RuleId is null && x.Message.Contains("non-disableable", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "deny-read-all")).IsTrue();
    }

    [Test]
    public async Task LintEngine_MinimumSeverity_DenyReadAll_InRuleOptions_ReportsConfigurationErrorAndKeepsEffectiveSeverity()
    {
        var yaml = """
        on: push
        permissions: read-all
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["deny-read-all"] = new RuleConfig { Severity = DiagnosticSeverity.Warning },
            },
        };

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "failsafe-min-severity-deny-read-all.yml", config);
        var configError = result.Diagnostics.FirstOrDefault(x => x.RuleId is null && x.Message.Contains("minimum severity", StringComparison.Ordinal));
        var ruleDiagnostic = result.Diagnostics.FirstOrDefault(x => x.RuleId == "deny-read-all");

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(ruleDiagnostic.Message.Length).IsGreaterThan(0);
        await Assert.That(ruleDiagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task LintEngine_NonDisableableRule_InlineSuppression_ReportsConfigurationErrorAndDoesNotSuppress()
    {
        var yaml = """
        on: push
        # seiton: disable-next-line deny-write-all
        permissions: write-all
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "failsafe-inline.yml");
        var configError = result.Diagnostics.FirstOrDefault(x => x.RuleId is null && x.Message.Contains("non-disableable", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsTrue();
    }

    [Test]
    public async Task LintEngine_NonDisableableRule_ConfigExclusion_ReportsConfigurationErrorAndDoesNotSuppress()
    {
        var yaml = """
        on: push
        permissions: write-all
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["deny-write-all"]),
            ],
        };

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "failsafe-exclusion.yml", config);
        var configError = result.Diagnostics.FirstOrDefault(x => x.RuleId is null && x.Message.Contains("non-disableable", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsTrue();
        await Assert.That(result.SuppressionSummary.TotalSuppressed).IsEqualTo(0);
    }

    [Test]
    public async Task LintEngine_RuleOptions_UnknownRuleId_ReportsConfigurationErrorWithSuggestion()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["job-permissions-requred"] = new RuleConfig { Enabled = false },
            },
        };

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "rule-options-unknown.yml", config);
        var configError = result.Diagnostics.FirstOrDefault(x =>
            x.RuleId is null
            && x.Message.Contains("unknown rule-id", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(configError.Message.Contains("Did you mean 'job-permissions-required'", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_AdditiveCustomization_IsPassedToRuleConfig()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var rule = new ConfigCaptureRule();
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["dangerous-triggers"] = new RuleConfig { Events = new ExtendableList(["issue_comment", "pull_request_review_comment"]) },
                ["runner-label"] = new RuleConfig { KnownHostedLabels = new ExtendableList(["ubuntu-24.04-arm", "windows-2025-vs2026"]) },
                ["credentials"] = new RuleConfig { PublicRegistries = new ExtendableList(["registry.example.com", "mirror.example.net:5000"]) },
                ["cache-poisoning"] = new RuleConfig { UntrustedTriggers = new ExtendableList(["issue_comment"]) },
                ["unredacted-secrets"] = new RuleConfig { OutputCommands = new ExtendableList(["tee"]) },
            },
        };

        _ = new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization.yml", config);

        await Assert.That(rule.LastConfig is not null).IsTrue();
        var dtRule = rule.LastConfig!.GetRuleConfig("dangerous-triggers");
        await Assert.That(dtRule?.Events?.Extend).IsEquivalentTo(new[] { "issue_comment", "pull_request_review_comment" });
        var rlRule = rule.LastConfig.GetRuleConfig("runner-label");
        await Assert.That(rlRule?.KnownHostedLabels?.Extend).IsEquivalentTo(new[] { "ubuntu-24.04-arm", "windows-2025-vs2026" });
        var crRule = rule.LastConfig.GetRuleConfig("credentials");
        await Assert.That(crRule?.PublicRegistries?.Extend).IsEquivalentTo(new[] { "registry.example.com", "mirror.example.net:5000" });
        var cpRule = rule.LastConfig.GetRuleConfig("cache-poisoning");
        await Assert.That(cpRule?.UntrustedTriggers?.Extend).IsEquivalentTo(new[] { "issue_comment" });
        var usRule = rule.LastConfig.GetRuleConfig("unredacted-secrets");
        await Assert.That(usRule?.OutputCommands?.Extend).IsEquivalentTo(new[] { "tee" });
    }

    [Test]
    public async Task LintEngine_AdditiveCustomization_DefaultsToEmptyWhenConfigOmitsIt()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var rule = new ConfigCaptureRule();

        _ = new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization-default.yml", new LintConfig());

        await Assert.That(rule.LastConfig is not null).IsTrue();
        await Assert.That(rule.LastConfig!.Rules).IsNull();
    }

    [Test]
    public async Task LintEngine_AdditiveCustomization_NormalizesToAsciiLowerAndDeduplicates()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var rule = new ConfigCaptureRule();
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["dangerous-triggers"] = new RuleConfig { Events = new ExtendableList(["Issue_Comment", "issue_comment"]) },
                ["runner-label"] = new RuleConfig { KnownHostedLabels = new ExtendableList(["Custom-Large", "custom-large"]) },
                ["credentials"] = new RuleConfig { PublicRegistries = new ExtendableList(["Registry.Example.Com", "registry.example.com"]) },
                ["cache-poisoning"] = new RuleConfig { UntrustedTriggers = new ExtendableList(["Issue_Comment", "issue_comment"]) },
                ["unredacted-secrets"] = new RuleConfig { OutputCommands = new ExtendableList(["TEE", "tee"]) },
            },
        };

        _ = new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization-normalized.yml", config);

        await Assert.That(rule.LastConfig is not null).IsTrue();
        await Assert.That(rule.LastConfig!.GetRuleConfig("dangerous-triggers")?.Events?.Extend).IsEquivalentTo(new[] { "issue_comment" });
        await Assert.That(rule.LastConfig.GetRuleConfig("runner-label")?.KnownHostedLabels?.Extend).IsEquivalentTo(new[] { "custom-large" });
        await Assert.That(rule.LastConfig.GetRuleConfig("credentials")?.PublicRegistries?.Extend).IsEquivalentTo(new[] { "registry.example.com" });
        await Assert.That(rule.LastConfig.GetRuleConfig("cache-poisoning")?.UntrustedTriggers?.Extend).IsEquivalentTo(new[] { "issue_comment" });
        await Assert.That(rule.LastConfig.GetRuleConfig("unredacted-secrets")?.OutputCommands?.Extend).IsEquivalentTo(new[] { "tee" });
    }

    [Test]
    public async Task LintEngine_DangerousTriggers_AdditionalDangerousEvents_EmitWarning()
    {
        var yaml = """
        on: issue_comment
        jobs:
            build:
                runs-on: ubuntu-latest
                permissions: {}
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["dangerous-triggers"] = new RuleConfig { Events = new ExtendableList(["issue_comment"]) },
            },
        };

        var result = new LintEngine([new DangerousTriggersRule()]).Check(Encoding.UTF8.GetBytes(yaml), "dangerous-trigger-custom.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "dangerous-triggers" && x.Message.Contains("issue_comment", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_CachePoisoning_AdditionalUntrustedTriggers_EmitWarning()
    {
        var yaml = """
        on: issue_comment
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/cache@v4
                      with:
                          path: ~/.npm
                          key: npm-${{ runner.os }}
        """;

        var engine = new LintEngine([new CachePoisoningRule()]);
        var withoutConfig = engine.Check(Encoding.UTF8.GetBytes(yaml), "cache-poisoning-custom-without.yml");
        var withConfig = engine.Check(
            Encoding.UTF8.GetBytes(yaml),
            "cache-poisoning-custom-with.yml",
            new LintConfig
            {
                Rules = new Dictionary<string, RuleConfig>
                {
                    ["cache-poisoning"] = new RuleConfig { UntrustedTriggers = new ExtendableList(["issue_comment"]) },
                },
            });

        await Assert.That(withoutConfig.Diagnostics.Any(x => x.RuleId == "cache-poisoning")).IsFalse();
        await Assert.That(withConfig.Diagnostics.Any(x => x.RuleId == "cache-poisoning" && x.Message.Contains("untrusted triggers", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_SelfHostedRunner_AdditionalUntrustedTriggers_EmitWarning()
    {
        var yaml = """
        on: issue_comment
        jobs:
            build:
                runs-on: self-hosted
                steps:
                    - run: echo ok
        """;

        var engine = new LintEngine([new SelfHostedRunnerRule()]);
        var withoutConfig = engine.Check(Encoding.UTF8.GetBytes(yaml), "self-hosted-runner-custom-without.yml");
        var withConfig = engine.Check(
            Encoding.UTF8.GetBytes(yaml),
            "self-hosted-runner-custom-with.yml",
            new LintConfig
            {
                Rules = new Dictionary<string, RuleConfig>
                {
                    ["self-hosted-runner"] = new RuleConfig { UntrustedTriggers = new ExtendableList(["issue_comment"]) },
                },
            });

        await Assert.That(withoutConfig.Diagnostics.Any(x => x.RuleId == "self-hosted-runner")).IsFalse();
        await Assert.That(withConfig.Diagnostics.Any(x => x.RuleId == "self-hosted-runner" && x.Message.Contains("untrusted triggers", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_UnredactedSecrets_AdditionalOutputCommands_EmitWarning()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                env:
                    TOKEN: ${{ secrets.GITHUB_TOKEN }}
                steps:
                    - run: tee /dev/null <<< "${TOKEN}"
        """;

        var engine = new LintEngine([new UnredactedSecretsRule()]);
        var withoutConfig = engine.Check(Encoding.UTF8.GetBytes(yaml), "unredacted-secrets-custom-without.yml");
        var withConfig = engine.Check(
            Encoding.UTF8.GetBytes(yaml),
            "unredacted-secrets-custom-with.yml",
            new LintConfig
            {
                Rules = new Dictionary<string, RuleConfig>
                {
                    ["unredacted-secrets"] = new RuleConfig { OutputCommands = new ExtendableList(["tee"]) },
                },
            });

        await Assert.That(withoutConfig.Diagnostics.Any(x => x.RuleId == "unredacted-secrets")).IsFalse();
        await Assert.That(withConfig.Diagnostics.Any(x => x.RuleId == "unredacted-secrets" && x.Message.Contains("without masking", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunnerLabel_AdditionalKnownHostedLabels_SuppressWarning()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: custom-large
                permissions: {}
                steps:
                    - run: echo hello
        """;

        var engine = new LintEngine([new RunnerLabelRule()]);
        var withoutConfig = engine.Check(Encoding.UTF8.GetBytes(yaml), "runner-label-custom-without.yml");
        var withConfig = engine.Check(
            Encoding.UTF8.GetBytes(yaml),
            "runner-label-custom-with.yml",
            new LintConfig
            {
                Rules = new Dictionary<string, RuleConfig>
                {
                    ["runner-label"] = new RuleConfig { KnownHostedLabels = new ExtendableList(["custom-large"]) },
                },
            });

        await Assert.That(withoutConfig.Diagnostics.Any(x => x.RuleId == "runner-label")).IsTrue();
        await Assert.That(withConfig.Diagnostics.Any(x => x.RuleId == "runner-label")).IsFalse();
    }

    [Test]
    public async Task LintEngine_Credentials_AdditionalPublicRegistries_SuppressWarning()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                container:
                    image: registry.example.com/team/app:1.0.0
                steps:
                    - run: echo hello
        """;

        var engine = new LintEngine([new CredentialsRule()]);
        var withoutConfig = engine.Check(Encoding.UTF8.GetBytes(yaml), "credentials-custom-without.yml");
        var withConfig = engine.Check(
            Encoding.UTF8.GetBytes(yaml),
            "credentials-custom-with.yml",
            new LintConfig
            {
                Rules = new Dictionary<string, RuleConfig>
                {
                    ["credentials"] = new RuleConfig { PublicRegistries = new ExtendableList(["registry.example.com"]) },
                },
            });

        await Assert.That(withoutConfig.Diagnostics.Any(x => x.RuleId == "credentials")).IsTrue();
        await Assert.That(withConfig.Diagnostics.Any(x => x.RuleId == "credentials")).IsFalse();
    }

    [Test]
    public async Task LintEngine_AdditiveCustomization_InvalidValues_ReportConfigurationErrors()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["dangerous-triggers"] = new RuleConfig { Events = new ExtendableList(["   "]) },
                ["runner-label"] = new RuleConfig { KnownHostedLabels = new ExtendableList([""]) },
                ["credentials"] = new RuleConfig { PublicRegistries = new ExtendableList(["https://registry.example.com/team/app"]) },
                ["cache-poisoning"] = new RuleConfig { UntrustedTriggers = new ExtendableList([""]) },
                ["unredacted-secrets"] = new RuleConfig { OutputCommands = new ExtendableList(["   "]) },
                ["forbidden-uses"] = new RuleConfig { Allow = ["   "], Deny = ["   "] },
            },
        };

        var result = new LintEngine([new ConfigCaptureRule()]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization-invalid.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("events extend entry must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("known-hosted-labels extend entry must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("credentials additional public registry host 'https://registry.example.com/team/app' is invalid", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("untrusted-triggers extend entry must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("output-commands extend entry must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("allow pattern must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("deny pattern must not be empty", StringComparison.Ordinal))).IsTrue();
    }

    private static async Task AssertRuleCases(IRule rule, string ruleId, RuleCase[] cases, LintConfig? config = null)
    {
        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var yaml = NormalizeYaml(c.Yaml);
            var result = config is null
                ? new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), $"rule-case-{c.Name}.yml")
                : new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), $"rule-case-{c.Name}.yml", config);
            var diagnostics = result.Diagnostics.Where(x => x.RuleId == ruleId).ToArray();

            if (c.ExpectedSubstrings.Length == 0)
            {
                await Assert.That(diagnostics).IsEmpty();
                continue;
            }

            for (var j = 0; j < c.ExpectedSubstrings.Length; j++)
            {
                var expected = c.ExpectedSubstrings[j];
                var found = diagnostics.Any(x => x.Message.Contains(expected, StringComparison.Ordinal));
                if (!found)
                {
                    var observed = diagnostics.Length == 0
                        ? "<no rule diagnostics>"
                        : string.Join(" | ", diagnostics.Select(static x => x.Message));
                    throw new InvalidOperationException($"rule={ruleId} case={c.Name} expected={expected} observed={observed}");
                }
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "seiton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string NormalizeYaml(string raw)
    {
        var normalized = raw.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');

        var start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        var end = lines.Length - 1;
        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        if (end < start)
        {
            return string.Empty;
        }

        var minIndent = int.MaxValue;
        for (var i = start; i <= end; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }

            if (indent < minIndent)
            {
                minIndent = indent;
            }
        }

        if (minIndent == int.MaxValue)
        {
            minIndent = 0;
        }

        var builder = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            var line = lines[i];
            if (line.Length >= minIndent)
            {
                builder.Append(line[minIndent..]);
            }
            else
            {
                builder.Append(line);
            }

            if (i < end)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private readonly record struct RuleCase(string Name, string Yaml, string[] ExpectedSubstrings);

    private readonly record struct FixabilityCase(string RuleId, IRule Rule, string Yaml, bool ExpectsFix);

    private sealed class DuplicateDiagnosticRule : IRule
    {
        private readonly List<Diagnostic> diagnostics = [];

        public DuplicateDiagnosticRule(RuleId id)
        {
            Id = id;
        }

        public RuleId Id { get; }

        public string Name => $"Duplicate-{Id.ToId()}";

        public bool SupportsDocumentKind(DocumentKind documentKind) => true;

        public IReadOnlyList<Diagnostic> GetDiagnostics() => diagnostics;

        public void SetConfig(LintConfig config)
        {
        }

        public void VisitWorkflowPre(Workflow workflow)
        {
            diagnostics.Clear();
            diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "shared duplicate diagnostic",
                    new TextRange(0, 0, 1, 1, 1, 1),
                    RuleId: Id.ToId()));
        }

        public void VisitWorkflowPost(Workflow workflow)
        {
        }

        public void VisitEvent(Event ev)
        {
        }

        public void VisitJobPre(Job job)
        {
        }

        public void VisitJobPost(Job job)
        {
        }

        public void VisitStep(Step step)
        {
        }
    }

    private sealed class ConfigCaptureRule : IRule
    {
        public RuleId Id => RuleId.JobStructure;

        public string Name => "Config Capture Rule";

        public bool SupportsDocumentKind(DocumentKind documentKind) => true;

        public LintConfig? LastConfig { get; private set; }

        public IReadOnlyList<Diagnostic> GetDiagnostics() => [];

        public void SetConfig(LintConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            LastConfig = config;
        }

        public void VisitWorkflowPre(Workflow workflow)
        {
        }

        public void VisitWorkflowPost(Workflow workflow)
        {
        }

        public void VisitEvent(Event ev)
        {
        }

        public void VisitJobPre(Job job)
        {
        }

        public void VisitJobPost(Job job)
        {
        }

        public void VisitStep(Step step)
        {
        }
    }

    private sealed class CountingRule : IRule
    {
        private LintConfig? config;

        public RuleId Id => RuleId.JobStructure;

        public string Name => "Test Rule";

        public bool SupportsDocumentKind(DocumentKind documentKind) => true;

        public int WorkflowPreCount { get; private set; }

        public int WorkflowPostCount { get; private set; }

        public int EventCount { get; private set; }

        public int JobPreCount { get; private set; }

        public int JobPostCount { get; private set; }

        public int StepCount { get; private set; }

        public IReadOnlyList<Diagnostic> GetDiagnostics() => [];

        public void SetConfig(LintConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            this.config = config;
        }

        public void VisitWorkflowPre(Workflow workflow)
        {
            EnsureConfigured();
            WorkflowPreCount++;
        }

        public void VisitWorkflowPost(Workflow workflow)
        {
            EnsureConfigured();
            WorkflowPostCount++;
        }

        public void VisitEvent(Event ev)
        {
            EnsureConfigured();
            EventCount++;
        }

        public void VisitJobPre(Job job)
        {
            EnsureConfigured();
            JobPreCount++;
        }

        public void VisitJobPost(Job job)
        {
            EnsureConfigured();
            JobPostCount++;
        }

        public void VisitStep(Step step)
        {
            EnsureConfigured();
            StepCount++;
        }

        private void EnsureConfigured()
        {
            if (config is null)
            {
                throw new InvalidOperationException("Rule is not configured.");
            }
        }
    }

    // regression: parser + lint rule duplicate diagnostics are suppressed
    [Test]
    public async Task LintEngine_DuplicateParserAndLintDiagnostics_AreDeduplicated()
    {
        // Job without runs-on triggers both parser and job-structure rule
        var yaml = """
        on: push
        jobs:
          test:
            steps:
              - run: echo ok
        """u8;
        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var runsOnDiags = result.Diagnostics.Where(d => d.Message.Contains("\"runs-on\" section is missing")).ToArray();
        await Assert.That(runsOnDiags).Count().IsEqualTo(1);
    }

    [Test]
    public async Task LintEngine_DuplicateParserAndLintDiagnostics_BothUsesAndSteps_AreDeduplicated()
    {
        // Job with both uses and steps triggers both parser and lint rules
        var yaml = """
        on: push
        jobs:
          test:
            uses: org/repo/.github/workflows/build.yml@main
            runs-on: ubuntu-latest
            steps:
              - run: echo ok
        """u8;
        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var bothDiags = result.Diagnostics.Where(d => d.Message.Contains("cannot have both uses and steps")).ToArray();
        await Assert.That(bothDiags).Count().IsEqualTo(1);
    }

    // C-3: hashFiles function context restriction (linter diagnostic)

    [Test]
    public async Task LintEngine_HashFilesInWorkflowEnv_ReportsParserDiagnostic()
    {
        var yaml = """
        on: push
        env:
            CACHE_KEY: ${{ hashFiles('**/package-lock.json') }}
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """u8;
        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"hashFiles\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_HashFilesInJobIf_ReportsParserDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            build:
                if: ${{ hashFiles('**/package-lock.json') != '' }}
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """u8;
        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"hashFiles\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_HashFilesInStepRun_NoDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ${{ hashFiles('**/package-lock.json') }}
        """u8;
        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("hashFiles", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task LintEngine_HashFilesInStepWith_NoDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/cache@v4
                      with:
                        key: ${{ hashFiles('**/package-lock.json') }}
                        path: ./packages
        """u8;
        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("hashFiles", StringComparison.Ordinal))).IsFalse();
    }

    // C-4: job-level secrets exclusion

    [Test]
    public async Task LintEngine_JobName_WithSecrets_ReportsDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            build:
                name: ${{ secrets.TOKEN }}
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """u8;
        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"secrets\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_JobEnv_WithSecrets_NoDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                env:
                    TOKEN: ${{ secrets.TOKEN }}
                steps:
                    - run: echo ok
        """u8;
        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"secrets\" is not allowed here", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ReusableWorkflowRule_InvalidFormat_IncludesDocUrl()
    {
        var yaml = """
        on: push
        jobs:
            reuse:
                uses: "foo/bar/workflow.yml"
        """u8;

        var result = new LintEngine([new ReusableWorkflowRule()]).Check(yaml.ToArray(), "test.yaml");
        var msgs = result.Diagnostics.Where(d => d.Message.Contains("is not following the format", StringComparison.Ordinal)).ToArray();
        await Assert.That(msgs.Length).IsGreaterThan(0);
        await Assert.That(msgs[0].Message.Contains("see https://docs.github.com/en/actions/learn-github-actions/reusing-workflows for more details", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_ReusableWorkflowSteps_NoDuplicateForbiddenKeyDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            call1:
                uses: owner/repo/.github/workflows/reuse.yml@main
                steps:
                    - run: echo hello
        """u8;

        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        // Count messages about "steps" being not allowed — should be exactly 1, not 2 (parser + linter)
        var stepsNotAllowed = result.Diagnostics
            .Where(d => d.Message.Contains("key 'steps' is not allowed", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(stepsNotAllowed).Count().IsEqualTo(1);
    }

    // regression: alias-expanded steps that produce the same error at the same position
    // must be deduplicated even though each step gets a unique step-index prefix.
    [Test]
    public async Task LintEngine_AliasExpandedSteps_DedupDiagnosticsAtSamePosition()
    {
        var yaml = """
        on: push
        jobs:
          test:
            runs-on: ubuntu-latest
            steps:
              - &step
                run: echo
                with:
                  foo: bar
              - *step
              - *step
              - *step
        """u8;

        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        // All alias-expanded steps point to the anchor position (same line:col).
        // The "unexpected key" errors differ only in step index prefix and must dedup to 1.
        var unexpectedKeyDiags = result.Diagnostics
            .Where(d => d.Message.Contains("unexpected key \"with\"", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(unexpectedKeyDiags).Count().IsEqualTo(1);
    }

    // regression: action metadata composite steps with alias expansion must also dedup.
    // steps[N] prefix (no jobs.'<id>') must be stripped for dedup consistency.
    [Test]
    public async Task LintEngine_AliasExpandedActionMetadataSteps_DedupDiagnosticsAtSamePosition()
    {
        var yaml = """
        name: test
        description: test action
        runs:
          using: composite
          steps:
            - &step
              run: echo
              with:
                foo: bar
            - *step
            - *step
            - *step
        """u8;

        var result = new LintEngine().Check(yaml.ToArray(), "action.yaml");
        // All alias-expanded steps point to the anchor position (same line:col).
        // The "unexpected key" errors differ only in step index prefix (steps[N]) and must dedup to 1.
        var unexpectedKeyDiags = result.Diagnostics
            .Where(d => d.Message.Contains("unexpected key \"with\"", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(unexpectedKeyDiags).Count().IsEqualTo(1);
    }

    // reusable-workflow forbidden-key diagnostics must report at the forbidden key position
    [Test]
    public async Task ReusableWorkflowRule_ForbiddenKey_ReportsAtKeyPosition()
    {
        var yaml = """
        on: push
        jobs:
          call1:
            uses: org/repo/workflow.yml@v1
            steps:
              - run: echo
        """u8;

        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var forbiddenKeyDiag = result.Diagnostics
            .Where(d => d.Message.Contains("key 'steps' is not allowed", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(forbiddenKeyDiag).Count().IsEqualTo(1);
        // Must report at the 'steps:' key position (line 5), not the job ID position (line 3)
        await Assert.That(forbiddenKeyDiag[0].Location.StartLine).IsEqualTo(5);
        await Assert.That(forbiddenKeyDiag[0].Location.StartColumn).IsEqualTo(5);
    }

    [Test]
    public async Task ReusableWorkflowRule_ForbiddenRunsOn_ReportsAtKeyPosition()
    {
        var yaml = """
        on: push
        jobs:
          call1:
            uses: org/repo/workflow.yml@v1
            runs-on: ubuntu-latest
        """u8;

        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var forbiddenKeyDiag = result.Diagnostics
            .Where(d => d.Message.Contains("key 'runs-on' is not allowed", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(forbiddenKeyDiag).Count().IsEqualTo(1);
        // Must report at the 'runs-on:' key position (line 5), not the job ID position (line 3)
        await Assert.That(forbiddenKeyDiag[0].Location.StartLine).IsEqualTo(5);
        await Assert.That(forbiddenKeyDiag[0].Location.StartColumn).IsEqualTo(5);
    }

    [Test]
    public async Task ReusableWorkflowRule_WithRequiresUses_ReportsAtWithKeyPosition()
    {
        var yaml = """
        on: push
        jobs:
          call2:
            with:
              foo: bar
            runs-on: ubuntu-latest
            steps:
              - run: echo
        """u8;

        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var requiresUsesDiag = result.Diagnostics
            .Where(d => d.Message.Contains("key 'with' requires uses", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(requiresUsesDiag).Count().IsEqualTo(1);
        // Must report at the 'with:' key position (line 4), not the job ID position (line 3)
        await Assert.That(requiresUsesDiag[0].Location.StartLine).IsEqualTo(4);
        await Assert.That(requiresUsesDiag[0].Location.StartColumn).IsEqualTo(5);
    }

    [Test]
    public async Task ReusableWorkflowRule_SecretsRequiresUses_ReportsAtSecretsKeyPosition()
    {
        var yaml = """
        on: push
        jobs:
          call3:
            secrets:
              aaa: bbb
            runs-on: ubuntu-latest
            steps:
              - run: echo
        """u8;

        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var requiresUsesDiag = result.Diagnostics
            .Where(d => d.Message.Contains("key 'secrets' requires uses", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(requiresUsesDiag).Count().IsEqualTo(1);
        // Must report at the 'secrets:' key position (line 4), not the job ID position (line 3)
        await Assert.That(requiresUsesDiag[0].Location.StartLine).IsEqualTo(4);
        await Assert.That(requiresUsesDiag[0].Location.StartColumn).IsEqualTo(5);
    }

    [Test]
    public async Task JobStructureRule_CannotHaveBothUsesAndSteps_ReportsAtStepsKeyPosition()
    {
        var yaml = """
        on: push
        jobs:
          call1:
            uses: org/repo/workflow.yml@v1
            steps:
              - run: echo
        """u8;

        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var bothDiag = result.Diagnostics
            .Where(d => d.Message.Contains("cannot have both uses and steps", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(bothDiag).Count().IsEqualTo(1);
        // Must report at the 'steps:' key position (line 5), not the job ID position (line 3)
        await Assert.That(bothDiag[0].Location.StartLine).IsEqualTo(5);
        await Assert.That(bothDiag[0].Location.StartColumn).IsEqualTo(5);
    }

    [Test]
    public async Task JobStructureRule_CannotHaveBothUsesAndRunsOn_ReportsAtRunsOnKeyPosition()
    {
        var yaml = """
        on: push
        jobs:
          call1:
            uses: org/repo/workflow.yml@v1
            runs-on: ubuntu-latest
        """u8;

        var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var bothDiag = result.Diagnostics
            .Where(d => d.Message.Contains("cannot have both uses and runs-on", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(bothDiag).Count().IsEqualTo(1);
        // Must report at the 'runs-on:' key position (line 5), not the job ID position (line 3)
        await Assert.That(bothDiag[0].Location.StartLine).IsEqualTo(5);
        await Assert.That(bothDiag[0].Location.StartColumn).IsEqualTo(5);
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

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yaml");
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
}
