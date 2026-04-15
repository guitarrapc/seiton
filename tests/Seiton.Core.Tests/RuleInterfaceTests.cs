using System.Text;
using System.Linq;
using Seiton.Core.Linting;
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

        await Assert.That(rules.Length).IsEqualTo(15);
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
