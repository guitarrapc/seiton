using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

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
}
