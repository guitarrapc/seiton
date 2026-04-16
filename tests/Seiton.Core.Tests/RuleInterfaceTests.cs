using System.Text;
using System.Linq;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
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

        await Assert.That(rules.Length).IsEqualTo(18);
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

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("read-all", StringComparison.Ordinal)).IsTrue();
        var relint = engine.Check(fixedBytes, "deny-write-all-fix.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsFalse();
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

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes).Replace("\r\n", "\n", StringComparison.Ordinal);

        var runsOnIndex = fixedText.IndexOf("runs-on: ubuntu-latest", StringComparison.Ordinal);
        var permissionsIndex = fixedText.IndexOf("permissions: {}", StringComparison.Ordinal);
        var stepsIndex = fixedText.IndexOf("steps:", StringComparison.Ordinal);

        await Assert.That(runsOnIndex >= 0).IsTrue();
        await Assert.That(permissionsIndex > runsOnIndex).IsTrue();
        await Assert.That(stepsIndex > permissionsIndex).IsTrue();
        var relint = engine.Check(fixedBytes, "job-permissions-required-fix-runs-on.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();
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

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("${VERSION}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("${{ env.VERSION }}", StringComparison.Ordinal)).IsFalse();
        var relint = engine.Check(fixedBytes, "run-env-fix-posix.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "run-env-context-direct-use")).IsFalse();
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
                AdditionalPublicRegistries: ["registry.example.com", "mirror.example.net:5000"]),
        };

        _ = new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization.yml", config);

        await Assert.That(rule.LastConfig is not null).IsTrue();
        await Assert.That(rule.LastConfig!.AdditiveCustomization.AdditionalDangerousEvents).IsEquivalentTo(new[] { "issue_comment", "pull_request_review_comment" });
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalKnownHostedLabels).IsEquivalentTo(new[] { "ubuntu-24.04-arm", "windows-2025-vs2026" });
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalPublicRegistries).IsEquivalentTo(new[] { "registry.example.com", "mirror.example.net:5000" });
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
                AdditionalPublicRegistries: ["Registry.Example.Com", "registry.example.com"]),
        };

        _ = new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization-normalized.yml", config);

        await Assert.That(rule.LastConfig is not null).IsTrue();
        await Assert.That(rule.LastConfig!.AdditiveCustomization.AdditionalDangerousEvents).IsEquivalentTo(new[] { "issue_comment" });
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalKnownHostedLabels).IsEquivalentTo(new[] { "custom-large" });
        await Assert.That(rule.LastConfig.AdditiveCustomization.AdditionalPublicRegistries).IsEquivalentTo(new[] { "registry.example.com" });
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
                AdditionalPublicRegistries: ["https://registry.example.com/team/app"]),
        };

        var result = new LintEngine([new ConfigCaptureRule()]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization-invalid.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("dangerous-triggers additional dangerous event must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("runner-label additional known hosted label must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("credentials additional public registry host 'https://registry.example.com/team/app' is invalid", StringComparison.Ordinal))).IsTrue();
    }

    static async Task AssertRuleCases(IRule rule, string ruleId, RuleCase[] cases)
    {
        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var yaml = NormalizeYaml(c.Yaml);
            var result = new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), $"rule-case-{c.Name}.yml");
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
