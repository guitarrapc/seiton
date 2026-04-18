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
        await Assert.That(result.ParseDiagnostics.Any(x => x.Message.Contains("requires runs-on", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("requires runs-on", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_FatalParse_ReturnsParseDiagnosticsOnly()
    {
        var yaml = "[]";

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "fatal.yml");

        await Assert.That(result.HasFatalError).IsTrue();
        await Assert.That(result.Workflow).IsNull();
        await Assert.That(result.Diagnostics).HasSingleItem();
        await Assert.That(result.Diagnostics[0].Message).IsEqualTo("workflow root must be mapping");
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
            && x.Message.Contains("requires runs-on", StringComparison.Ordinal));

        await Assert.That(diagnostic.Message.Length).IsGreaterThan(0);
        await Assert.That(diagnostic.RuleId).IsEqualTo("job-structure");
        await Assert.That(diagnostic.FilePath).IsEqualTo("rule-filepath.yml");
    }

    [Test]
    public async Task RuleInterface_CanBeUsedWithWorkflowVisitor()
    {
        var workflow = new Workflow
        {
            On =
            [
                new WebhookEvent
                {
                    EventName = new StringNode { Value = new Utf8Slice(0, 0) },
                    Hook = new StringNode { Value = new Utf8Slice(0, 0) },
                },
                new ScheduledEvent
                {
                    EventName = new StringNode { Value = new Utf8Slice(0, 0) },
                },
            ],
            Jobs = new Dictionary<Utf8String, Job>
            {
                [new Utf8String("build"u8)] = new Job
                {
                    Id = new StringNode { Value = new Utf8Slice(0, 0) },
                    Steps =
                    [
                        new Step
                        {
                            Exec = new ExecRun
                            {
                                Kind = StepExecKind.Run,
                                Run = new StringNode { Value = new Utf8Slice(0, 0) },
                            },
                        },
                    ],
                },
            },
        };

        var rule = new CountingRule();
        rule.SetConfig(LintConfig.Empty);

        var visitor = new WorkflowVisitor();
        visitor.AddPass(rule);
        visitor.Visit(workflow);

        await Assert.That(rule.Id).IsEqualTo("test-rule");
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

        var workflow = new Workflow
        {
            Jobs = new Dictionary<Utf8String, Job>
            {
                [new Utf8String("build"u8)] = new Job
                {
                    Id = new StringNode
                    {
                        Value = new Utf8Slice(source.IndexOf("build", StringComparison.Ordinal), "build".Length),
                        Range = new TextRange(0, 0, 1, 1, 1, 1),
                    },
                    RunsOn = new Runner(),
                    WorkflowCall = new WorkflowCall
                    {
                        Uses = new StringNode { Value = new Utf8Slice(source.IndexOf("./.github/workflows/reusable.yml", StringComparison.Ordinal), "./.github/workflows/reusable.yml".Length) },
                    },
                    Steps =
                    [
                        new Step
                        {
                            Exec = new ExecRun
                            {
                                Kind = StepExecKind.Run,
                                Run = new StringNode { Value = new Utf8Slice(0, 0) },
                            },
                        },
                    ],
                },
            },
        };

        var visitor = new WorkflowVisitor();
        var rule = new SyntaxRule();
        rule.SetConfig(new LintConfig { Utf8Yaml = Encoding.UTF8.GetBytes(source) });
        visitor.AddPass(rule);

        visitor.Visit(workflow);
        var diagnostics = rule.GetDiagnostics();

        await Assert.That(diagnostics.Any(x => x.Message.Contains("cannot have both uses and steps", StringComparison.Ordinal))).IsTrue();
        await Assert.That(diagnostics.Any(x => x.Message.Contains("cannot have both uses and runs-on", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task SyntaxRule_ReportsUnknownInputForPopularAction()
    {
        var source = "actions/checkout@v4";
        var sourceBytes = Encoding.UTF8.GetBytes(source);

        var workflow = new Workflow
        {
            Jobs = new Dictionary<Utf8String, Job>
            {
                [new Utf8String("build"u8)] = new Job
                {
                    Id = new StringNode
                    {
                        Value = new Utf8Slice(0, 0),
                        Range = new TextRange(0, 0, 1, 1, 1, 1),
                    },
                    RunsOn = new Runner(),
                    Steps =
                    [
                        new Step
                        {
                            Exec = new ExecAction
                            {
                                Kind = StepExecKind.Action,
                                Uses = new StringNode
                                {
                                    Value = new Utf8Slice(0, sourceBytes.Length),
                                    Range = new TextRange(0, sourceBytes.Length, 1, 1, 1, sourceBytes.Length + 1),
                                },
                                Inputs = new Dictionary<Utf8String, StringNode>
                                {
                                    [new Utf8String("fetch-depht"u8)] = new StringNode { Value = new Utf8Slice(0, 0) },
                                },
                            },
                            Range = new TextRange(0, 0, 1, 1, 1, 1),
                        },
                    ],
                },
            },
        };

        var visitor = new WorkflowVisitor();
        var rule = new SyntaxRule();
        rule.SetConfig(new LintConfig { Utf8Yaml = sourceBytes });
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("permissions.contents must be one of 'read', 'write', or 'none'", StringComparison.Ordinal))).IsTrue();
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

        await Assert.That(result.ParseDiagnostics.Any(x => x.Message.Contains("calls reusable workflow with uses", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("calls reusable workflow with uses", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task RuleCatalog_DefaultRules_MatchDocumentedScope()
    {
        var rules = RuleCatalog.CreateDefaultRules();

        await Assert.That(rules.Length).IsEqualTo(46);
        await Assert.That(rules[0].Id).IsEqualTo("job-structure");
        await Assert.That(rules[1].Id).IsEqualTo("reusable-workflow");
        await Assert.That(rules[2].Id).IsEqualTo("permissions");
        await Assert.That(rules[3].Id).IsEqualTo("popular-action-inputs");
        await Assert.That(rules[4].Id).IsEqualTo("unpinned-uses");
        await Assert.That(rules[5].Id).IsEqualTo("unpinned-image");
        await Assert.That(rules[6].Id).IsEqualTo("dangerous-triggers");
        await Assert.That(rules[7].Id).IsEqualTo("job-permissions-required");
        await Assert.That(rules[8].Id).IsEqualTo("needs-graph");
        await Assert.That(rules[9].Id).IsEqualTo("shell-name");
        await Assert.That(rules[10].Id).IsEqualTo("runner-label");
        await Assert.That(rules[11].Id).IsEqualTo("id-naming");
        await Assert.That(rules[12].Id).IsEqualTo("glob-pattern");
        await Assert.That(rules[13].Id).IsEqualTo("deny-write-all");
        await Assert.That(rules[14].Id).IsEqualTo("credentials");
        await Assert.That(rules[15].Id).IsEqualTo("template-injection");
        await Assert.That(rules[16].Id).IsEqualTo("expr-undefined-var");
        await Assert.That(rules[17].Id).IsEqualTo("run-env-context-direct-use");
        await Assert.That(rules[18].Id).IsEqualTo("runner-no-latest");
        await Assert.That(rules[19].Id).IsEqualTo("run-secrets-context-direct-use");
        await Assert.That(rules[20].Id).IsEqualTo("run-inputs-context-direct-use");
        await Assert.That(rules[21].Id).IsEqualTo("secrets-whole-context-access");
        await Assert.That(rules[22].Id).IsEqualTo("checkout-persist-credentials");
        await Assert.That(rules[23].Id).IsEqualTo("deny-read-all");
        await Assert.That(rules[24].Id).IsEqualTo("deny-inherit-secrets");
        await Assert.That(rules[25].Id).IsEqualTo("job-timeout-minutes-required");
        await Assert.That(rules[26].Id).IsEqualTo("github-app-token-inputs");
        await Assert.That(rules[27].Id).IsEqualTo("cache-poisoning");
        await Assert.That(rules[28].Id).IsEqualTo("self-hosted-runner");
        await Assert.That(rules[29].Id).IsEqualTo("unredacted-secrets");
        await Assert.That(rules[30].Id).IsEqualTo("secrets-outside-env");
        await Assert.That(rules[31].Id).IsEqualTo("workflow-secrets");
        await Assert.That(rules[32].Id).IsEqualTo("job-secrets");
        await Assert.That(rules[33].Id).IsEqualTo("action-shell-is-required");
        await Assert.That(rules[34].Id).IsEqualTo("matrix");
        await Assert.That(rules[35].Id).IsEqualTo("env-var");
        await Assert.That(rules[36].Id).IsEqualTo("deprecated-commands");
        await Assert.That(rules[37].Id).IsEqualTo("if-cond");
        await Assert.That(rules[38].Id).IsEqualTo("fake-ternary");
        await Assert.That(rules[39].Id).IsEqualTo("deny-job-container-latest-image");
        await Assert.That(rules[40].Id).IsEqualTo("archived-uses");
        await Assert.That(rules[41].Id).IsEqualTo("insecure-commands");
        await Assert.That(rules[42].Id).IsEqualTo("overprovisioned-secrets");
        await Assert.That(rules[43].Id).IsEqualTo("forbidden-uses");
        await Assert.That(rules[44].Id).IsEqualTo("ref-version-mismatch");
        await Assert.That(rules[45].Id).IsEqualTo("use-trusted-publishing");

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
        await Assert.That(RuleCatalog.GetPriority("deny-write-all")).IsEqualTo(13);
        await Assert.That(RuleCatalog.GetPriority("credentials")).IsEqualTo(14);
        await Assert.That(RuleCatalog.GetPriority("template-injection")).IsEqualTo(15);
        await Assert.That(RuleCatalog.GetPriority("expr-undefined-var")).IsEqualTo(16);
        await Assert.That(RuleCatalog.GetPriority("run-env-context-direct-use")).IsEqualTo(17);
        await Assert.That(RuleCatalog.GetPriority("runner-no-latest")).IsEqualTo(18);
        await Assert.That(RuleCatalog.GetPriority("run-secrets-context-direct-use")).IsEqualTo(19);
        await Assert.That(RuleCatalog.GetPriority("run-inputs-context-direct-use")).IsEqualTo(20);
        await Assert.That(RuleCatalog.GetPriority("secrets-whole-context-access")).IsEqualTo(21);
        await Assert.That(RuleCatalog.GetPriority("checkout-persist-credentials")).IsEqualTo(22);
        await Assert.That(RuleCatalog.GetPriority("deny-read-all")).IsEqualTo(23);
        await Assert.That(RuleCatalog.GetPriority("deny-inherit-secrets")).IsEqualTo(24);
        await Assert.That(RuleCatalog.GetPriority("job-timeout-minutes-required")).IsEqualTo(25);
        await Assert.That(RuleCatalog.GetPriority("github-app-token-inputs")).IsEqualTo(26);
        await Assert.That(RuleCatalog.GetPriority("cache-poisoning")).IsEqualTo(31);
        await Assert.That(RuleCatalog.GetPriority("self-hosted-runner")).IsEqualTo(32);
        await Assert.That(RuleCatalog.GetPriority("unredacted-secrets")).IsEqualTo(33);
        await Assert.That(RuleCatalog.GetPriority("secrets-outside-env")).IsEqualTo(34);
        await Assert.That(RuleCatalog.GetPriority("workflow-secrets")).IsEqualTo(35);
        await Assert.That(RuleCatalog.GetPriority("job-secrets")).IsEqualTo(36);
        await Assert.That(RuleCatalog.GetPriority("action-shell-is-required")).IsEqualTo(37);
        await Assert.That(RuleCatalog.GetPriority("matrix")).IsEqualTo(38);
        await Assert.That(RuleCatalog.GetPriority("env-var")).IsEqualTo(39);
        await Assert.That(RuleCatalog.GetPriority("deprecated-commands")).IsEqualTo(40);
        await Assert.That(RuleCatalog.GetPriority("if-cond")).IsEqualTo(41);
        await Assert.That(RuleCatalog.GetPriority("fake-ternary")).IsEqualTo(42);
        await Assert.That(RuleCatalog.GetPriority("deny-job-container-latest-image")).IsEqualTo(43);
        await Assert.That(RuleCatalog.GetPriority("archived-uses")).IsEqualTo(44);
        await Assert.That(RuleCatalog.GetPriority("insecure-commands")).IsEqualTo(45);
        await Assert.That(RuleCatalog.GetPriority("overprovisioned-secrets")).IsEqualTo(46);
        await Assert.That(RuleCatalog.GetPriority("forbidden-uses")).IsEqualTo(47);
        await Assert.That(RuleCatalog.GetPriority("ref-version-mismatch")).IsEqualTo(48);
        await Assert.That(RuleCatalog.GetPriority("use-trusted-publishing")).IsEqualTo(49);
        await Assert.That(RuleCatalog.GetPriority("known-vulnerable-actions")).IsEqualTo(27);
        await Assert.That(RuleCatalog.GetPriority("impostor-commit")).IsEqualTo(28);
        await Assert.That(RuleCatalog.GetPriority("ref-confusion")).IsEqualTo(29);
        await Assert.That(RuleCatalog.GetPriority("stale-action-refs")).IsEqualTo(30);
    }

    [Test]
    public async Task RuleCatalog_OnlineAuditRules_AreKnownForResolutionAndCanonicalIds()
    {
        await Assert.That(RuleCatalog.TryResolveRuleId("known-vulnerable-actions", out var knownVulnerable)).IsTrue();
        await Assert.That(knownVulnerable).IsEqualTo("known-vulnerable-actions");
        await Assert.That(RuleCatalog.GetCanonicalRuleId("known-vulnerable-actions")).IsEqualTo("seiton-lint-rule-047");

        await Assert.That(RuleCatalog.TryResolveRuleId("seiton-lint-rule-048", out var impostorCommit)).IsTrue();
        await Assert.That(impostorCommit).IsEqualTo("impostor-commit");
        await Assert.That(RuleCatalog.GetCanonicalRuleId("ref-confusion")).IsEqualTo("seiton-lint-rule-049");
        await Assert.That(RuleCatalog.GetCanonicalRuleId("stale-action-refs")).IsEqualTo("seiton-lint-rule-050");
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
            ["requires runs-on"]),
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
            ["permissions.contents must be one of 'read', 'write', or 'none'"]),
        };

        await AssertRuleCases(new PermissionsRule(), "permissions", cases);
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
        var result = engine.Check(sourceBytes, "checkout-persist-fix-insert-with.yml");
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
        var result = engine.Check(sourceBytes, "checkout-persist-fix-existing-with.yml");
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
        var result = engine.Check(sourceBytes, "checkout-persist-fix-replace.yml");
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
            ["circular 'needs' dependency"]),
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
            ["circular 'needs' dependency"]),
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
            ["circular 'needs' dependency"]),
        };

        await AssertRuleCases(new NeedsGraphRule(), "needs-graph", cases);
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
            ["not a known GitHub-hosted runner label"]),
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
            ["not a known GitHub-hosted runner label"]),
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
            ["job id", "contains invalid characters"]),
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
            ["step id", "contains invalid characters"]),
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
            ["step id", "contains invalid characters"]),
        };

        await AssertRuleCases(new IdNamingRule(), "id-naming", cases);
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
            ["invalid glob pattern", "not closed"]),
            new RuleCase(
            "ng-invalid-activity-type",
            """
            on:
                pull_request:
                    types: [bogus]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["unsupported activity type 'bogus'"]),
            new RuleCase(
            "ng-filter-mutual-exclusion",
            """
            on:
                pull_request:
                    branches: [main]
                    branches-ignore: ['release/**']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["cannot be used together"]),
        };

        await AssertRuleCases(new GlobPatternRule(), "glob-pattern", cases);
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

        await Assert.That(fixedText.Contains("read-all", StringComparison.Ordinal)).IsTrue();
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
            DefaultJobTimeoutMinutesForFix = 15,
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
            "ok-tibdex-with-repository-and-permissions",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: tibdex/github-app-token@v2
                          with:
                              repository: owner/repo
                              permissions: >-
                                  {"contents":"read"}
            """,
            []),
            new RuleCase(
            "ng-missing-both-constraints",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
            """,
            ["repository and permission constraints"]),
            new RuleCase(
            "ng-missing-repository-constraint",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
                          with:
                              permission-issues: write
            """,
            ["repository constraints"]),
            new RuleCase(
            "ng-missing-permission-constraint",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: tibdex/github-app-token@v2
                          with:
                              repositories: repo-a
            """,
            ["permission constraints"]),
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
            "ng-run-without-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
            """,
            ["shell is required if run is set"]),
            new RuleCase(
            "ng-run-with-empty-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
                          shell: ""
            """,
            ["shell is required if run is set"]),
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
            "ng-secret-in-action-input",
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
            ["action input", "outside env handoff"]),
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
            "ng-include-unknown-axis",
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
                        - run: echo ng
            """,
            ["strategy.matrix.include references unknown axis 'arch'"]),
        };

        await AssertRuleCases(new MatrixRule(), "matrix", cases);
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
            ["deprecated command '::set-output'", "$GITHUB_OUTPUT"]),
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
            ["deprecated command '::set-env'", "$GITHUB_ENV"]),
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
            ["job if condition is always false"]),
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
            ["step if condition is always true"]),
        };

        await AssertRuleCases(new IfCondRule(), "if-cond", cases);
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
    public async Task RuleRegression_DenyJobContainerLatestImageRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-job-container-version-tag",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/example/app:1.2.3
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-job-container-latest-with-digest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/example/app:latest@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-container-explicit-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/example/app:latest
                    steps:
                        - run: echo ng
            """,
            ["must not use mutable ':latest'", "@sha256:<64-hex>"]),
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
            ["has implicit latest tag", "@sha256:<64-hex>"]),
            new RuleCase(
            "ok-service-latest-is-out-of-scope",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        db:
                            image: postgres:latest
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new DenyJobContainerLatestImageRule(), "deny-job-container-latest-image", cases);
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
                          run: echo ng
            """,
            ["multiple secret values", "minimum required"]),
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
            """,
            ["passes 2 explicit secrets", "minimum required secrets"]),
        };

        await AssertRuleCases(new OverprovisionedSecretsRule(), "overprovisioned-secrets", cases);
    }

    [Test]
    public async Task RuleRegression_ForbiddenUsesRule_TableDriven()
    {
        var config = new LintConfig
        {
            AdditiveCustomization = new RuleSpecificAdditiveCustomization(
                ForbiddenUsesDenyPatterns: ["bad-org/*"],
                ForbiddenUsesAllowPatterns: ["bad-org/safe-action"]),
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
            ["template injection risk", "run", "github.event"]),
            new RuleCase(
            "ng-env-uses-github-event-comment-body",
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
            ["template injection risk", "env.COMMENT_BODY", "github.event"]),
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
            ["template injection risk", "run", "github.event"]),
        };

        await AssertRuleCases(new TemplateInjectionRule(), "template-injection", cases);
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
            ["job.if", "undefined context 'steps'", "job scope"]),
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
            ["step.if", "undefined context 'foobar'", "step scope"]),
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
            ["step.env.DATA", "undefined context 'unknown'", "step scope"]),
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
            ["step.with.repository", "undefined context 'unknown'", "step scope"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
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
        var result = engine.Check(sourceBytes, "job-permissions-required-fix-runs-on.yml");
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
        var result = engine.Check(sourceBytes, "job-permissions-required-fix-no-tab-introduce.yml");
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
        var result = engine.Check(sourceBytes, "job-permissions-required-fix-uses.yml");
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
        var result = engine.Check(sourceBytes, "job-permissions-required-fix-whitespace.yml");
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
        var result = engine.Check(sourceBytes, "job-permissions-required-fix-no-trailing.yml");
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
                ExpectsFix: false),
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
                "action-shell-is-required",
                new ActionShellIsRequiredRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo hello
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
                "deny-job-container-latest-image",
                new DenyJobContainerLatestImageRule(),
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        container:
                            image: ghcr.io/example/app:latest
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
                $"fixability-{c.RuleId}.yml");
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
    public async Task LintEngine_DeduplicatesRuleDiagnostics_ByPriority()
    {
        var yaml = """
                on: push
                jobs: {}
                """;

        var engine = new LintEngine(
        [
            new DuplicateDiagnosticRule("permissions"),
                new DuplicateDiagnosticRule("job-structure"),
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
            RuleOptions = new Dictionary<string, RuleOption>
            {
                ["job-permissions-required"] = new RuleOption(Enabled: false),
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
            RuleOptions = new Dictionary<string, RuleOption>
            {
                ["seiton-lint-rule-008"] = new RuleOption(Enabled: false),
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
            RuleOptions = new Dictionary<string, RuleOption>
            {
                ["job-permissions-required"] = new RuleOption(Severity: DiagnosticSeverity.Error),
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
                new LintExclusion("**/*.yml", ["job-permissions-required"], JobId: "build"),
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
                new LintExclusion("**/*.yml", ["job-permissions-required"], JobId: "buid"),
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
            RuleOptions = new Dictionary<string, RuleOption>
            {
                ["deny-write-all"] = new RuleOption(Enabled: false),
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
            RuleOptions = new Dictionary<string, RuleOption>
            {
                ["deny-write-all"] = new RuleOption(Severity: DiagnosticSeverity.Warning),
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
            RuleOptions = new Dictionary<string, RuleOption>
            {
                ["deny-read-all"] = new RuleOption(Enabled: false),
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
            RuleOptions = new Dictionary<string, RuleOption>
            {
                ["deny-read-all"] = new RuleOption(Severity: DiagnosticSeverity.Warning),
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
            RuleOptions = new Dictionary<string, RuleOption>
            {
                ["job-permissions-requred"] = new RuleOption(Enabled: false),
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
            AdditiveCustomization = new RuleSpecificAdditiveCustomization(
                AdditionalDangerousEvents: ["issue_comment", "pull_request_review_comment"],
                AdditionalKnownHostedLabels: ["ubuntu-24.04-arm", "windows-2025-vs2026"],
                AdditionalPublicRegistries: ["registry.example.com", "mirror.example.net:5000"],
                AdditionalUntrustedTriggers: ["issue_comment"],
                AdditionalOutputCommands: ["tee"]),
        };

        _ = new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization.yml", config);

        await Assert.That(rule.LastConfig is not null).IsTrue();
        await Assert.That(rule.LastConfig!.AdditiveCustomization.AdditionalDangerousEvents).IsEquivalentTo(new[] { "issue_comment", "pull_request_review_comment" });
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalKnownHostedLabels).IsEquivalentTo(new[] { "ubuntu-24.04-arm", "windows-2025-vs2026" });
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalPublicRegistries).IsEquivalentTo(new[] { "registry.example.com", "mirror.example.net:5000" });
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalUntrustedTriggers).IsEquivalentTo(new[] { "issue_comment" });
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalOutputCommands).IsEquivalentTo(new[] { "tee" });
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
        await Assert.That(rule.LastConfig!.AdditiveCustomization).IsEqualTo(RuleSpecificAdditiveCustomization.Empty);
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalDangerousEvents).IsNull();
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalKnownHostedLabels).IsNull();
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalPublicRegistries).IsNull();
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalUntrustedTriggers).IsNull();
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalOutputCommands).IsNull();
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
            AdditiveCustomization = new RuleSpecificAdditiveCustomization(
                AdditionalDangerousEvents: ["Issue_Comment", "issue_comment"],
                AdditionalKnownHostedLabels: ["Custom-Large", "custom-large"],
                AdditionalPublicRegistries: ["Registry.Example.Com", "registry.example.com"],
                AdditionalUntrustedTriggers: ["Issue_Comment", "issue_comment"],
                AdditionalOutputCommands: ["TEE", "tee"]),
        };

        _ = new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization-normalized.yml", config);

        await Assert.That(rule.LastConfig is not null).IsTrue();
        await Assert.That(rule.LastConfig!.AdditiveCustomization.AdditionalDangerousEvents).IsEquivalentTo(new[] { "issue_comment" });
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalKnownHostedLabels).IsEquivalentTo(new[] { "custom-large" });
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalPublicRegistries).IsEquivalentTo(new[] { "registry.example.com" });
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalUntrustedTriggers).IsEquivalentTo(new[] { "issue_comment" });
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalOutputCommands).IsEquivalentTo(new[] { "tee" });
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
            AdditiveCustomization = new RuleSpecificAdditiveCustomization(
                AdditionalDangerousEvents: ["issue_comment"]),
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
                AdditiveCustomization = new RuleSpecificAdditiveCustomization(
                    AdditionalUntrustedTriggers: ["issue_comment"]),
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
                AdditiveCustomization = new RuleSpecificAdditiveCustomization(
                    AdditionalUntrustedTriggers: ["issue_comment"]),
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
                AdditiveCustomization = new RuleSpecificAdditiveCustomization(
                    AdditionalOutputCommands: ["tee"]),
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
                AdditiveCustomization = new RuleSpecificAdditiveCustomization(
                    AdditionalKnownHostedLabels: ["custom-large"]),
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
                AdditiveCustomization = new RuleSpecificAdditiveCustomization(
                    AdditionalPublicRegistries: ["registry.example.com"]),
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
            AdditiveCustomization = new RuleSpecificAdditiveCustomization(
                AdditionalDangerousEvents: ["   "],
                AdditionalKnownHostedLabels: [""],
                AdditionalPublicRegistries: ["https://registry.example.com/team/app"],
                AdditionalUntrustedTriggers: [""],
                AdditionalOutputCommands: ["   "],
                ForbiddenUsesAllowPatterns: ["   "],
                ForbiddenUsesDenyPatterns: ["   "]),
        };

        var result = new LintEngine([new ConfigCaptureRule()]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization-invalid.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("dangerous-triggers additional dangerous event must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("runner-label additional known hosted label must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("credentials additional public registry host 'https://registry.example.com/team/app' is invalid", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("cache-poisoning/self-hosted-runner additional untrusted trigger must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("unredacted-secrets additional output command must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("forbidden-uses additional allow pattern must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("forbidden-uses additional deny pattern must not be empty", StringComparison.Ordinal))).IsTrue();
    }

    static async Task AssertRuleCases(IRule rule, string ruleId, RuleCase[] cases, LintConfig? config = null)
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

    static string NormalizeYaml(string raw)
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

    readonly record struct RuleCase(string Name, string Yaml, string[] ExpectedSubstrings);

    readonly record struct FixabilityCase(string RuleId, IRule Rule, string Yaml, bool ExpectsFix);

    sealed class DuplicateDiagnosticRule : IRule
    {
        readonly List<Diagnostic> diagnostics = [];

        public DuplicateDiagnosticRule(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public string Name => $"Duplicate-{Id}";

        public Diagnostic[] GetDiagnostics() => diagnostics.ToArray();

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
                    RuleId: Id));
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

    sealed class ConfigCaptureRule : IRule
    {
        public string Id => "config-capture";

        public string Name => "Config Capture Rule";

        public LintConfig? LastConfig { get; private set; }

        public Diagnostic[] GetDiagnostics() => [];

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

    sealed class CountingRule : IRule
    {
        LintConfig? config;

        public string Id => "test-rule";

        public string Name => "Test Rule";

        public int WorkflowPreCount { get; private set; }

        public int WorkflowPostCount { get; private set; }

        public int EventCount { get; private set; }

        public int JobPreCount { get; private set; }

        public int JobPostCount { get; private set; }

        public int StepCount { get; private set; }

        public Diagnostic[] GetDiagnostics() => [];

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

        void EnsureConfigured()
        {
            if (config is null)
            {
                throw new InvalidOperationException("Rule is not configured.");
            }
        }
    }
}
