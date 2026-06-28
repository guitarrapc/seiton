using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{
    [Test]
    public async Task RuleRegression_BackgroundStepsRule_TableDriven()
    {
        var elevenParallelChildren = BuildParallelChildrenYaml(11);
        var elevenParallelConditional = BuildParallelChildrenYaml(11, ParallelChildIfKind.ConstantFalse);

        var cases = new[]
        {
            new RuleCase(
                "ok-wait-after-background",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - id: build-frontend
                              run: npm run build
                              background: true
                            - id: build-backend
                              run: npm run build
                              background: true
                            - wait: [build-frontend, build-backend]
                """,
                []),
            new RuleCase(
                "ok-wait-parallel-child-id",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - parallel:
                                - id: child
                                  run: echo child
                            - wait: [child]
                """,
                []),
            new RuleCase(
                "ok-cancel-case-insensitive",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - id: BUILD
                              run: echo build
                              background: true
                            - cancel: build
                """,
                []),
            new RuleCase(
                "ng-wait-unknown-id",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - wait: [missing]
                """,
                ["\"wait\" references unknown background step id 'missing'"]),
            new RuleCase(
                "ng-wait-forward-ref",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - wait: [later]
                            - id: later
                              run: echo later
                              background: true
                """,
                ["background step id 'later' is referenced by \"wait\" before it is defined"]),
            new RuleCase(
                "ng-wait-forward-ref-non-background",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - wait: [plain]
                            - id: plain
                              run: echo plain
                """,
                ["\"wait\" references step id 'plain' that is not a background step"]),
            new RuleCase(
                "ng-wait-non-background",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - id: plain
                              run: echo plain
                            - wait: [plain]
                """,
                ["\"wait\" references step id 'plain' that is not a background step"]),
            new RuleCase(
                "ng-wait-control-step-id",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - id: gate
                              wait-all: null
                            - wait: [gate]
                """,
                ["\"wait\" references step id 'gate' that is not a background step"]),
            new RuleCase(
                "ng-parallel-eleven-children",
                $"""
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - parallel:
                {elevenParallelChildren}
                """,
                ["more than 10 background steps may run concurrently in this job"]),
            new RuleCase(
                "ok-no-background-flow",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo hello
                            - uses: actions/checkout@v4
                """,
                []),
            new RuleCase(
                "ok-parallel-eleven-conditional",
                $"""
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - parallel:
                {elevenParallelConditional}
                """,
                []),
            new RuleCase(
                "ok-parallel-eleven-dynamic-if",
                $"""
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - parallel:
                {BuildParallelChildrenYaml(11, ParallelChildIfKind.Dynamic)}
                """,
                []),
        };

        await AssertRuleCases(new BackgroundStepsRule(), "background-steps", cases);
    }

    private enum ParallelChildIfKind
    {
        None,
        ConstantFalse,
        Dynamic,
    }

    private static string BuildParallelChildrenYaml(int count, ParallelChildIfKind ifKind = ParallelChildIfKind.None)
    {
        var lines = new string[count];
        for (var i = 0; i < count; i++)
        {
            var ifLine = ifKind switch
            {
                ParallelChildIfKind.ConstantFalse => "\n                  if: ${{ false }}",
                ParallelChildIfKind.Dynamic => "\n                  if: ${{ github.ref == 'main' }}",
                _ => string.Empty,
            };
            lines[i] = $"                - id: child{i}\n                  run: echo {i}{ifLine}";
        }

        return string.Join('\n', lines);
    }
}
