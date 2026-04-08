using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

public sealed class RuleInterfaceTests
{
    [Test]
    public async Task RuleInterface_CanBeUsedWithWorkflowVisitor()
    {
        var workflow = new Workflow
        {
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
            runs-on: ubuntu-latest
            uses: ./.github/workflows/reusable.yml
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
                        Uses = new StringNode { Value = new Utf8Slice(0, 0) },
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

    sealed class CountingRule : IRule
    {
        LintConfig? config;

        public string Id => "test-rule";

        public string Name => "Test Rule";

        public int WorkflowPreCount { get; private set; }

        public int WorkflowPostCount { get; private set; }

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
